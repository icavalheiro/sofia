using System.Text.Json;
using Avalonia.Threading;
using Sofia.Contracts.Infrastructure;

namespace Sofia.App.Services;

/// <summary>
/// Local UI state that the web keeps in <c>localStorage</c>: connection, per-conversation drafts, delivery mode, chip
/// choices, panel and task-panel flags/positions and shortcut overrides. The keys keep the web's exact names
/// (<c>sofia.rightPanel.openBySession</c>, …) so the two UIs stay interchangeable while both exist, and so the browser
/// head can swap this file for <c>localStorage</c> in spec 044 without touching call sites.
/// </summary>
/// <remarks>
/// Writes are debounced and atomic (temp file + move). A corrupt file is renamed to
/// <c>state.corrupt-&lt;timestamp&gt;.json</c> and the store starts empty instead of blocking startup.
/// </remarks>
public sealed class LocalState
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private DispatcherTimer? _flushTimer;

    public LocalState(string? path = null)
    {
        FilePath = path ?? Path.Combine(DefaultDirectory, "state.json");
        Load();
    }

    public static LocalState Current { get; } = new();

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sofia");

    public string FilePath { get; }

    /// <summary>True when the previous file could not be parsed and was moved aside.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    public IReadOnlyDictionary<string, JsonElement> Values => _values;

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
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_values, Json.Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // State is a nicety: a read-only or locked file must never break the UI.
        }
    }

    public void Load()
    {
        _values.Clear();
        if (!File.Exists(FilePath)) return;
        try
        {
            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text, Json.Options);
            if (parsed is null) return;
            foreach (var (key, value) in parsed) _values[key] = value;
        }
        catch
        {
            RecoveredFromCorruption = true;
            try
            {
                var backup = Path.Combine(
                    Path.GetDirectoryName(FilePath) ?? ".",
                    $"state.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                File.Move(FilePath, backup, overwrite: true);
            }
            catch
            {
                // Nothing else to do: the in-memory state is already empty.
            }
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
