using System.Text.Json;
using Avalonia.Threading;
using Sofia.Contracts.Infrastructure;

namespace Sofia.App.Services;

/// <summary>
/// Where <see cref="LocalState"/> keeps its JSON blob. The desktop head writes an app-data file; the browser head
/// (spec 044) stores the very same document under one <c>localStorage</c> key. Implementations must never throw into
/// the UI: local state is a nicety.
/// </summary>
public interface ILocalStateStorage
{
    /// <summary>Returns the stored document, or null when there is none (or it cannot be read).</summary>
    string? Read();

    /// <summary>Replaces the stored document.</summary>
    void Write(string json);
}

/// <summary>
/// Atomic app-data file storage (temp file + move). A corrupt document is renamed to
/// <c>state.corrupt-&lt;timestamp&gt;.json</c> and reported through <see cref="LocalState.RecoveredFromCorruption"/>.
/// </summary>
public sealed class FileLocalStateStorage : ILocalStateStorage
{
    public FileLocalStateStorage(string path) => FilePath = path;

    public string FilePath { get; }

    public string? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return File.ReadAllText(FilePath);
        }
        catch
        {
            return null;
        }
    }

    public void Write(string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // State is a nicety: a read-only or locked file must never break the UI.
        }
    }

    /// <summary>Moves an unparseable document aside so the next start begins empty.</summary>
    public void Quarantine()
    {
        try
        {
            var backup = Path.Combine(
                Path.GetDirectoryName(FilePath) ?? ".",
                $"state.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            if (File.Exists(FilePath)) File.Move(FilePath, backup, overwrite: true);
        }
        catch
        {
            // Nothing else to do: the in-memory state is already empty.
        }
    }
}

/// <summary>
/// Local UI state that the web keeps in <c>localStorage</c>: connection, per-conversation drafts, delivery mode, chip
/// choices, panel and task-panel flags/positions and shortcut overrides. The keys keep the web's exact names
/// (<c>sofia.rightPanel.openBySession</c>, …) so the desktop file and the browser's <c>localStorage</c> entry stay
/// interchangeable.
/// </summary>
/// <remarks>
/// Writes are debounced (250 ms) and handed to the head's <see cref="ILocalStateStorage"/>. The desktop default is an
/// atomic JSON file; the browser head installs a <c>localStorage</c> store before the Avalonia app starts.
/// </remarks>
public sealed class LocalState
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private ILocalStateStorage _storage;
    private DispatcherTimer? _flushTimer;
    private static LocalState? _current;

    public LocalState(string? path = null)
        : this(new FileLocalStateStorage(path ?? Path.Combine(DefaultDirectory, "state.json")))
    {
    }

    public LocalState(ILocalStateStorage storage)
    {
        _storage = storage;
        Load();
    }

    /// <summary>
    /// Replaces the default file storage (the browser head points this at <c>localStorage</c>). Must be set before the
    /// first access to <see cref="Current"/>, or call <see cref="UseStorage"/> on the live instance.
    /// </summary>
    public static Func<ILocalStateStorage>? StorageFactory { get; set; }

    /// <summary>The process-wide instance, created on first use with <see cref="StorageFactory"/> or the app-data file.</summary>
    public static LocalState Current => _current ??= new LocalState(StorageFactory?.Invoke()
        ?? new FileLocalStateStorage(Path.Combine(DefaultDirectory, "state.json")));

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sofia");

    /// <summary>File backing the desktop store, or an empty string when the head keeps state elsewhere.</summary>
    public string FilePath => (_storage as FileLocalStateStorage)?.FilePath ?? "";

    /// <summary>True when the previous document could not be parsed and was moved aside.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    public IReadOnlyDictionary<string, JsonElement> Values => _values;

    /// <summary>Swaps the backing store and reloads the in-memory document from it (used by the browser head).</summary>
    public void UseStorage(ILocalStateStorage storage)
    {
        _storage = storage;
        Load();
    }

    // --- the keys the shell consumes (the web's localStorage names) ---------------------------------------

    public const string RightPanelKey = "sofia.rightPanel.openBySession";
    public const string TasksPanelKey = "sofia.tasks.openBySession";
    /// <summary>Floating task card position (web <c>sofia.tasks.pos</c>).</summary>
    public const string TasksPositionKey = "sofia.tasks.pos";
    public const string DraftsKey = "sofia.drafts";
    public const string DeliveryKey = "sofia.delivery";
    public const string ChipsKey = "sofia.chips";
    /// <summary>Partial shortcut overrides, id → combo (web <c>sofia.shortcuts.v1</c>).</summary>
    public const string ShortcutsKey = "sofia.shortcuts.v1";
    /// <summary>Language and the local general/appearance preferences (web <c>sofia.settings.v1</c>).</summary>
    public const string SettingsKey = "sofia.settings.v1";
    public const string AppearanceKey = "sofia.appearance";
    public const string SoundKey = "sofia.sound";
    public const string ConnectionKey = "sofia.connection";
    public const string WindowKey = "sofia.window";
    /// <summary>Pin state of the composer's context-meter popup (the web opens it on hover only).</summary>
    public const string ContextPopoverKey = "sofia.contextMeter.popover";

    public T? Get<T>(string key)
    {
        if (!_values.TryGetValue(key, out var element)) return default;
        try
        {
            return element.Deserialize<T>(Json.Options);
        }
        catch
        {
            return default;
        }
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = Get<T>(key);
        return value is not null;
    }

    /// <summary>Stores a value and schedules the debounced write.</summary>
    public void Set<T>(string key, T value)
    {
        _values[key] = JsonSerializer.SerializeToElement(value, Json.Options);
        ScheduleFlush();
    }

    public void Remove(string key)
    {
        if (_values.Remove(key)) ScheduleFlush();
    }

    /// <summary>Reads a boolean entry from a per-id map (e.g. a panel flag stored per conversation).</summary>
    public bool GetFlag(string key, string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var map = Get<Dictionary<string, bool>>(key);
        return map is not null && map.TryGetValue(id, out var value) && value;
    }

    public void SetFlag(string key, string? id, bool value)
    {
        if (string.IsNullOrEmpty(id)) return;
        var map = Get<Dictionary<string, bool>>(key) ?? new Dictionary<string, bool>();
        map[id] = value;
        Set(key, map);
    }

    public void SaveNow()
    {
        _flushTimer?.Stop();
        _flushTimer = null;
        _storage.Write(JsonSerializer.Serialize(_values, Json.Options));
    }

    public void Load()
    {
        _values.Clear();
        var text = _storage.Read();
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text, Json.Options);
            if (parsed is null) return;
            foreach (var (key, value) in parsed) _values[key] = value;
        }
        catch
        {
            RecoveredFromCorruption = true;
            (_storage as FileLocalStateStorage)?.Quarantine();
            _values.Clear();
        }
    }

    private void ScheduleFlush()
    {
        // Off the UI thread (or without a running dispatcher, e.g. in tests) write synchronously so nothing is lost.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            SaveNow();
            return;
        }

        _flushTimer ??= new DispatcherTimer(FlushDelay, DispatcherPriority.Background, (_, _) => SaveNow());
        _flushTimer.Stop();
        _flushTimer.Start();
    }
}
