using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Features.Composer;

/// <summary>
/// The composer state (web <c>ChatComposer.vue</c> + the prompt/command paths of <c>useHarness.ts</c>): the text and
/// its per-conversation draft, the attachment strip and session references, the four chips over the session fields,
/// the <c>/</c> command palette, the <c>@</c> mention popup (subagents, sessions, files), the follow-up delivery mode
/// (steer/queue), the hint row metrics and the segmented context meter.
/// </summary>
/// <remarks>
/// Server-owned values (agent, model, permission and reasoning modes) are read from the session and written with
/// <c>PATCH /api/sessions/{id}</c>; only drafts, the delivery mode, the last chip picks and the context popover state
/// live in <see cref="LocalState"/>. Everything the composer does goes through <see cref="ISofiaBackend"/>, so the
/// desktop (in-process or HTTP) and browser heads share this class.
/// </remarks>
public sealed class ComposerViewModel : ObservableObject
{
    /// <summary>Statuses that mean a run is in flight (web <c>BUSY</c>): the send control becomes stop + steer/queue.</summary>
    private static readonly HashSet<string> Busy = new(StringComparer.Ordinal)
    {
        SessionStatus.Working,
        SessionStatus.Waiting,
        SessionStatus.Permission,
        SessionStatus.Question,
    };

    public static readonly IReadOnlyList<ComposerOption> PermissionModes =
    [
        new("read-only", "Read only", "Can read files but never write or run commands.", "shield", ComposerTone.Info),
        new("plan", "Plan", "Researches and proposes a plan without applying changes.", "shield", ComposerTone.Detail),
        new("ask", "Ask before edits", "Prompts before any write, edit or command.", "shield", ComposerTone.Primary),
        new("full", "Full access", "Edits files and runs commands without asking.", "shield", ComposerTone.Err),
    ];

    public static readonly IReadOnlyList<ComposerOption> ReasoningModes =
    [
        new("off", "Off", "Answer directly, no extended thinking.", "zap"),
        new("low", "Low", "Brief internal reasoning.", "zap"),
        new("medium", "Medium", "Balanced reasoning depth.", "zap"),
        new("high", "High", "Extended reasoning for hard problems.", "zap"),
    ];

    /// <summary>Follow-up delivery modes while a run is active (web <c>DELIVERY_MODES</c>).</summary>
    public static readonly IReadOnlyList<ComposerOption> DeliveryModes =
    [
        new("steer", "Steer", "Deliver mid-run: the working reply picks it up", "send"),
        new("queue", "Queue", "Run afterwards, once the current reply ends", "clock"),
    ];

    private const string DefaultAgentId = "build";
    private static readonly JsonSerializerOptions JsonOptions = Sofia.Contracts.Infrastructure.Json.Create();

    private readonly HarnessStore? _store;
    private readonly ComposerDraftStore _drafts;
    private readonly LocalState _local;
    private readonly List<ComposerModel> _models = [];
    private readonly List<ComposerAgent> _agents = [];
    private readonly List<ComposerCommand> _commands = [];
    private readonly List<string> _files = [];
    private readonly List<ComposerReference> _recentSessions = [];
    private readonly List<ComposerReference> _matchedSessions = [];

    private ISofiaBackend? _backend;
    private Session? _session;
    private string _sessionId = "";
    private string _directory = "";
    private string _status = SessionStatus.Idle;
    private string _text = "";
    private string _delivery = "steer";
    private bool _steering;
    private string? _error;
    private bool _paletteOpen;
    private int _paletteIndex;
    private string _mentionQuery = "";
    private int _mentionIndex;
    private bool _mentionOpen;
    private int _mentionStart;
    private int _mentionEnd;
    private bool _sessionsLoading;
    private int _searchSeq;
    private DispatcherTimer? _sessionTimer;
    private bool _commandsLoaded;
    private bool _commandsLoading;
    private string _branch = "";
    private ComposerMenu _menu = ComposerMenu.None;
    private bool _contextPinned;
    private bool _suppressTextEcho;

    // live metrics (session.metrics) plus the session's persisted totals for the idle readouts
    private long _durationMs;
    private long _tokensPerSec;
    private double _cost;
    private bool _costAvailable;
    private long _contextUsed;
    private long _contextLimit;
    private long _contextSystem;
    private long _contextTools;

    public ComposerViewModel(HarnessStore? store = null, LocalState? localState = null)
    {
        _store = store;
        _local = localState ?? LocalState.Current;
        _drafts = new ComposerDraftStore(_local);
        _delivery = _local.Get<string>(LocalState.DeliveryKey) is "queue" ? "queue" : "steer";
        _contextPinned = _local.GetFlag(LocalState.ContextPopoverKey, "open");
        if (_store is not null) _store.Applied += OnApplied;
    }

    /// <summary>Raised when the surface must rebuild (text, attachments, palettes, chips, metrics).</summary>
    public event Action? Changed;

    /// <summary>Raised when a shortcut or a preview asked the input to take focus.</summary>
    public event Action? FocusRequested;

    // --- wiring -------------------------------------------------------------------------------------------

    public ISofiaBackend? Backend => _backend;

    public HarnessStore? Store => _store;

    /// <summary>Session the composer writes to (null in fixture mode).</summary>
    public Session? Session => _session;

    public string SessionId => _sessionId;

    /// <summary>Workspace folder used to scope commands, agents, files and the branch chip ('' for chat groups).</summary>
    public string Directory => _directory;

    /// <summary>Server status of the open conversation; drives send vs stop and steer vs queue.</summary>
    public string Status => _status;

    public bool IsReplying => Busy.Contains(_status);

    public void SetBackend(ISofiaBackend? backend)
    {
        if (ReferenceEquals(_backend, backend)) return;
        _backend = backend;
        _commandsLoaded = false;
        _commands.Clear();
        _agents.Clear();
        _files.Clear();
        _branch = "";
        _models.Clear();
        if (backend is not null) _ = RefreshAsync();
        Changed?.Invoke();
    }

    public void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        if (!IsReplying) _menu = _menu == ComposerMenu.Mode ? ComposerMenu.None : _menu;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsReplying));
        OnPropertyChanged(nameof(SendIcon));
        OnPropertyChanged(nameof(SendTitle));
        OnPropertyChanged(nameof(DurationLabel));
        RaiseChanged();
    }

    /// <summary>
    /// Points the composer at another conversation: parks the open draft, loads the new one, resets the palettes and
    /// re-reads the session-owned chip values (the server is the only source for agent/model/permission/reasoning).
    /// </summary>
    public void SetSession(Session? session, string sessionId, string directory, string? status = null)
    {
        var changedSession = !string.Equals(_sessionId, sessionId, StringComparison.Ordinal);
        if (changedSession)
        {
            FlushDraft();
            _sessionId = sessionId;
            LoadDraft(sessionId);
            _paletteOpen = false;
            _paletteIndex = 0;
            CloseMention();
            _menu = ComposerMenu.None;
            _error = null;
        }
        _session = session;
        var directoryChanged = !string.Equals(_directory, directory, StringComparison.Ordinal);
        _directory = directory;
        if (status is not null) SetStatus(status);
        ResetMetricsFromSession();
        if (changedSession || directoryChanged) _commandsLoaded = false;
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(ReasoningMode));
        RaiseChanged();
        if (changedSession || directoryChanged) _ = RefreshAsync();
    }

    /// <summary>Applies the session the store received (session.updated) without parking the draft.</summary>
    public void UpdateSession(Session? session)
    {
        if (session is null || session.Id != _sessionId) return;
        _session = session;
        ResetMetricsFromSession();
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(ReasoningMode));
        RaiseChanged();
    }

    // --- text, attachments and references -----------------------------------------------------------------

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSend));
            if (!_suppressTextEcho) UpdateSurfaces(value.Length);
            RaiseChanged();
        }
    }

    /// <summary>True when there is something to send: text or attachments (the web's <c>canSend</c>).</summary>
    public bool CanSend => _text.Trim().Length > 0 || Attachments.Count > 0;

    public List<ComposerAttachment> Attachments { get; } = [];

    public List<ComposerReference> References { get; } = [];

    public string? Error => _error;

    /// <summary>Sets the text without driving the palette/mention watchers (used when loading a draft).</summary>
    private void SetTextSilently(string text)
    {
        _suppressTextEcho = true;
        try
        {
            Text = text;
        }
        finally
        {
            _suppressTextEcho = false;
        }
    }

    /// <summary>Queues one attachment for the next prompt (the strip shows it at once, upload happens on submit).</summary>
    public ComposerAttachment AddAttachment(string path, string? name = null, string? mime = null, long? bytes = null)
    {
        var fileName = name ?? Path.GetFileName(path);
        var mimeType = mime ?? GuessMime(fileName);
        long size = bytes ?? (File.Exists(path) ? new FileInfo(path).Length : 0);
        var attachment = new ComposerAttachment
        {
            Name = fileName,
            Mime = mimeType,
            Bytes = size,
            Kind = ComposerFormat.KindOf(mimeType),
            Path = path,
            PreviewUrl = ComposerFormat.KindOf(mimeType) is "image" or "video" ? path : null,
        };
        Attachments.Add(attachment);
        ComposerChanged();
        return attachment;
    }

    /// <summary>Browser head seam (044): queue bytes handed over by an <c>input[type=file]</c>.</summary>
    public ComposerAttachment AddAttachmentBytes(string name, string mime, byte[] content)
    {
        var attachment = new ComposerAttachment
        {
            Name = name,
            Mime = mime,
            Bytes = content.LongLength,
            Kind = ComposerFormat.KindOf(mime),
            Content = content,
        };
        Attachments.Add(attachment);
        ComposerChanged();
        return attachment;
    }

    public void RemoveAttachment(string localId)
    {
        var index = Attachments.FindIndex(a => a.LocalId == localId);
        if (index < 0) return;
        Attachments.RemoveAt(index);
        ComposerChanged();
    }

    public void AddReference(ComposerReference reference)
    {
        if (References.Any(r => r.Id == reference.Id)) return;
        References.Add(reference);
        ComposerChanged();
    }

    public void RemoveReference(string id)
    {
        if (References.RemoveAll(r => r.Id == id) > 0) ComposerChanged();
    }

    // --- chip values (server-owned) -----------------------------------------------------------------------

    public string Agent => _session?.Agent is { Length: > 0 } agent ? agent : DefaultAgentId;

    public string ModelId => _session?.ModelId ?? "";

    /// <summary>
    /// Permission mode: the session's value wins; the locally remembered chip only fills the gap when the session
    /// carries none (a brand-new chat) so the UI never invents a server state.
    /// </summary>
    public string PermissionMode => NonEmpty(_session?.PermissionMode) ?? LocalChip("permission") ?? "ask";

    public string ReasoningMode => NonEmpty(_session?.ReasoningMode) ?? LocalChip("reasoning") ?? "medium";

    public string Delivery
    {
        get => _delivery;
        set
        {
            if (_delivery == value) return;
            _delivery = value == "queue" ? "queue" : "steer";
            _local.Set(LocalState.DeliveryKey, _delivery);
            // The delivery mode must survive a restart: write it now instead of waiting for the debounce.
            _local.SaveNow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SendIcon));
            OnPropertyChanged(nameof(SendTitle));
            RaiseChanged();
        }
    }

    public bool Steering
    {
        get => _steering;
        private set
        {
            if (!SetProperty(ref _steering, value)) return;
            OnPropertyChanged(nameof(CanSend));
        }
    }

    /// <summary>Icon of the submit button: send normally, the follow-up mode's glyph while a run is active.</summary>
    public string SendIcon => !IsReplying ? "send" : Delivery == "steer" ? "send" : "clock";

    public string SendTitle => !IsReplying
        ? "Send message"
        : Delivery == "steer" ? "Send steer" : "Send queued";

    /// <summary>The four chips of the hint row, left to right (web order: branch, permission, agent, model, reasoning).</summary>
    public string PermissionLabel => PermissionOf(PermissionMode).Label;

    public string ReasoningLabel => ReasoningOf(ReasoningMode).Label;

    public string AgentLabel => _agents.FirstOrDefault(a => a.Id == Agent)?.Name ?? Agent;

    public string ModelLabel => _models.FirstOrDefault(m => m.Id == ModelId)?.Label ?? ShortModel(ModelId);

    public string ModelIcon => _models.FirstOrDefault(m => m.Id == ModelId)?.Icon ?? "sparkles";

    public ComposerTone PermissionTone => PermissionOf(PermissionMode).Tone ?? ComposerTone.Primary;

    public IReadOnlyList<ComposerOption> PermissionOptions => PermissionModes;

    public IReadOnlyList<ComposerOption> ReasoningOptions => ReasoningModes;

    public IReadOnlyList<ComposerModel> Models => _models;

    public IReadOnlyList<ComposerAgent> Agents => _agents;

    /// <summary>Selectable agents of the agent dropdown: primary only (the web filters <c>mode !== 'subagent'</c>).</summary>
    public IReadOnlyList<ComposerAgent> PrimaryAgents => _agents.Where(a => !a.IsSubagent).ToList();

    public async Task SetPermissionAsync(string mode)
    {
        if (!PermissionModes.Any(p => p.Value == mode)) return;
        RememberChip("permission", mode);
        await PatchSessionAsync(new UpdateSessionRequest(null, null, null, mode, null));
    }

    public async Task SetReasoningAsync(string mode)
    {
        if (!ReasoningModes.Any(r => r.Value == mode)) return;
        RememberChip("reasoning", mode);
        await PatchSessionAsync(new UpdateSessionRequest(null, null, null, null, mode));
    }

    public async Task SetAgentAsync(string agentId)
    {
        if (agentId.Length == 0) return;
        await PatchSessionAsync(new UpdateSessionRequest(null, null, agentId, null, null));
    }

    public async Task SetModelAsync(string modelId)
    {
        if (modelId.Length == 0) return;
        await PatchSessionAsync(new UpdateSessionRequest(null, modelId, null, null, null));
    }

    private async Task PatchSessionAsync(UpdateSessionRequest request)
    {
        if (_session is null || _backend is null)
        {
            ApplyLocally(request);
            return;
        }
        try
        {
            var session = await _backend.UpdateSessionAsync(_session.Id, request);
            if (session is not null) _session = session;
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        ResetMetricsFromSession();
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(ReasoningMode));
        RaiseChanged();
    }

    private void ApplyLocally(UpdateSessionRequest request)
    {
        if (_session is null) return;
        if (request.Agent is { Length: > 0 } agent) _session.Agent = agent;
        if (request.ModelId is { Length: > 0 } model) _session.ModelId = model;
        if (request.PermissionMode is { Length: > 0 } permission) _session.PermissionMode = permission;
        if (request.ReasoningMode is { Length: > 0 } reasoning) _session.ReasoningMode = reasoning;
    }

    // --- popovers (single-open lock) ----------------------------------------------------------------------

    /// <summary>The one popover the composer holds open (the web's shared popover lock).</summary>
    public ComposerMenu Menu
    {
        get => _menu;
        private set
        {
            if (_menu == value) return;
            _menu = value;
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    /// <summary>Opens a popover, closing whichever other one was open; a second call on the same chip closes it.</summary>
    public void ToggleMenu(ComposerMenu menu)
    {
        if (menu == ComposerMenu.None)
        {
            Menu = ComposerMenu.None;
            return;
        }
        var opening = _menu != menu;
        Menu = opening ? menu : ComposerMenu.None;
        if (!opening) return;
        if (menu == ComposerMenu.Agent) _ = RefreshAgentsAsync();
        if (menu == ComposerMenu.Model)
        {
            RefreshModels();
            RaiseChanged();
        }
        SoundService.Current.Play(SoundKey.Click);
    }

    public void OpenMenu(ComposerMenu menu) => Menu = menu;

    public void CloseMenu() => Menu = ComposerMenu.None;

    // --- command palette ----------------------------------------------------------------------------------

    public bool PaletteOpen => _paletteOpen;

    public bool PaletteVisible => _paletteOpen && !MentionVisible;

    public int PaletteIndex
    {
        get => _paletteIndex;
        set => SetProperty(ref _paletteIndex, value);
    }

    public string PaletteQuery => ComposerKeys.PaletteQuery(_text);

    public IReadOnlyList<ComposerCommand> FilteredCommands
    {
        get
        {
            var query = PaletteQuery.ToLowerInvariant();
            return _commands.Where(c => c.Name.ToLowerInvariant().Contains(query, StringComparison.Ordinal)).ToList();
        }
    }

    public bool CommandsLoading => _commandsLoading;

    /// <summary>Closes the palette without touching the text (Escape, like the web's <c>closePalette</c>).</summary>
    public void ClosePalette()
    {
        if (!_paletteOpen) return;
        _paletteOpen = false;
        _paletteIndex = 0;
        RaiseChanged();
    }

    public IReadOnlyList<ComposerCommand> Commands => _commands;

    /// <summary>
    /// Recomputes the two typing surfaces after every text/caret change: the <c>/</c> palette opens while the whole
    /// text is a command word, the <c>@</c> popup while the caret sits after an <c>@word</c> that starts a word.
    /// </summary>
    public void UpdateSurfaces(int? caret = null)
    {
        var position = Math.Clamp(caret ?? _text.Length, 0, _text.Length);
        var range = ComposerKeys.MentionRange(_text, position);
        if (range is { } found && !ComposerKeys.PaletteAllowed(_text, IsReplying))
        {
            var wasOpen = _mentionOpen;
            _mentionOpen = true;
            _mentionStart = found.Start;
            _mentionEnd = found.End;
            if (_mentionQuery != found.Query)
            {
                _mentionQuery = found.Query;
                _mentionIndex = FirstItemRow();
                _ = ScheduleSessionSearchAsync();
            }
            if (!wasOpen)
            {
                _mentionIndex = FirstItemRow();
                _ = RefreshAgentsAsync();
                _ = RefreshFilesAsync();
                RefreshRecentSessions();
            }
            _paletteOpen = false;
            RaiseChanged();
            return;
        }
        CloseMention();
        if (ComposerKeys.PaletteAllowed(_text, IsReplying))
        {
            var wasOpen = _paletteOpen;
            _paletteOpen = true;
            if (!wasOpen) _paletteIndex = 0;
            _ = LoadCommandsAsync(force: !wasOpen);
            RaiseChanged();
            return;
        }
        if (_paletteOpen)
        {
            _paletteOpen = false;
            _paletteIndex = 0;
        }
        RaiseChanged();
    }

    /// <summary>Command picked from the palette: inserts <c>/name</c> and keeps whatever arguments were typed.</summary>
    public void PickCommand(ComposerCommand command)
    {
        var rest = Text;
        var cut = rest.IndexOfAny([' ', '\t', '\n', '\r']);
        var arguments = cut < 0 ? "" : rest[cut..].Trim();
        SetTextSilently("");
        Text = arguments.Length > 0 ? $"/{command.Name} {arguments}" : $"/{command.Name} ";
        _paletteOpen = false;
        _paletteIndex = 0;
        RaiseChanged();
        FocusRequested?.Invoke();
    }

    /// <summary>Sends <c>/name args</c> as the command it stands for; false when the text is not a known command.</summary>
    public async Task<bool> TryRunCommandAsync()
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            _text.Trim(),
            @"^/(\S+)(?:\s+([\s\S]*))?$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(200));
        if (!match.Success) return false;
        await LoadCommandsAsync();
        var name = match.Groups[1].Value.ToLowerInvariant();
        var command = _commands.FirstOrDefault(c => c.Name.ToLowerInvariant() == name);
        if (command is null)
        {
            // The cached list can predate a command file written since: re-read once before treating it as input.
            await LoadCommandsAsync(force: true);
            command = _commands.FirstOrDefault(c => c.Name.ToLowerInvariant() == name);
        }
        if (command is null) return false;

        var arguments = match.Groups[2].Value.Trim();
        var pending = Attachments.ToList();
        var text = _text.Trim();
        ClearComposer();
        SoundService.Current.Play(SoundKey.Send);
        if (_backend is null || _sessionId.Length == 0) return true;
        try
        {
            var ids = await UploadAsync(pending);
            await _backend.CommandAsync(_sessionId, new CommandRequest(
                command.Name,
                arguments.Length > 0 ? arguments : null,
                ids.Count > 0 ? ids : null));
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            RestoreDraft(_sessionId, text, pending, []);
            RaiseChanged();
            return true;
        }
    }

    /// <summary>Reads the command list; <paramref name="force"/> re-reads even when it was fetched before.</summary>
    public async Task LoadCommandsAsync(bool force = false)
    {
        if (_commandsLoaded && !force) return;
        if (_backend is null) return;
        _commandsLoading = true;
        RaiseChanged();
        try
        {
            var json = await _backend.CommandsAsync(_directory.Length > 0 ? _directory : null);
            _commands.Clear();
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in json.EnumerateArray())
                {
                    var name = Str(item, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    _commands.Add(new ComposerCommand(
                        name,
                        Str(item, "description") ?? "",
                        Str(item, "source") ?? "builtin"));
                }
            }
            _commandsLoaded = true;
        }
        catch
        {
            // The palette still opens: it shows the empty state, like the web.
        }
        finally
        {
            _commandsLoading = false;
            RaiseChanged();
        }
    }

    // --- mention popup ------------------------------------------------------------------------------------

    public bool MentionOpen => _mentionOpen;

    public bool MentionVisible => _mentionOpen && MentionRows.Count > 0;

    public int MentionIndex
    {
        get => _mentionIndex;
        set
        {
            if (_mentionIndex == value) return;
            _mentionIndex = value;
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    public string MentionQuery => _mentionQuery;

    public bool SessionsLoading => _sessionsLoading;

    public IReadOnlyList<ComposerAgent>? FilteredMentions =>
        _agents.Where(a => a.IsSubagent &&
                           (a.Id.Contains(_mentionQuery, StringComparison.OrdinalIgnoreCase) ||
                            a.Name.Contains(_mentionQuery, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public IReadOnlyList<ComposerReference> SessionMentions =>
        _mentionQuery.Trim().Length > 0
            ? (_sessionsLoading ? [] : _matchedSessions)
            : _recentSessions;

    public IReadOnlyList<string> FileMentions
    {
        get
        {
            var list = _mentionQuery.Length > 0
                ? _files.Where(f => f.Contains(_mentionQuery, StringComparison.OrdinalIgnoreCase))
                : _files;
            return list.Take(40).ToList();
        }
    }

    /// <summary>The popup rows: a section header before the first row of each non-empty group, then the items.</summary>
    public IReadOnlyList<ComposerMentionRow> MentionRows
    {
        get
        {
            if (!_mentionOpen) return [];
            var rows = new List<ComposerMentionRow>();
            void Group(string label, IEnumerable<ComposerMentionRow> items)
            {
                var list = items.ToList();
                if (list.Count == 0) return;
                rows.Add(ComposerMentionRow.Section(label));
                rows.AddRange(list);
            }
            Group("composer.mentionAgents", (FilteredMentions ?? []).Select(ComposerMentionRow.ForAgent));
            Group("composer.mentionSessions", SessionMentions.Select(ComposerMentionRow.ForSession));
            Group("composer.mentionFiles", FileMentions.Select(ComposerMentionRow.ForFile));
            return rows;
        }
    }

    public void CloseMention()
    {
        var wasOpen = _mentionOpen;
        _mentionOpen = false;
        _mentionQuery = "";
        _mentionStart = 0;
        _mentionEnd = 0;
        _sessionTimer?.Stop();
        if (wasOpen) RaiseChanged();
    }

    /// <summary>Moves the highlight, skipping section headers.</summary>
    public void MoveMention(int direction)
    {
        var rows = MentionRows;
        if (rows.Count == 0) return;
        var index = _mentionIndex;
        for (var i = index + direction; i >= 0 && i < rows.Count; i += direction)
        {
            if (rows[i].IsHeader) continue;
            MentionIndex = i;
            return;
        }
    }

    public void MovePalette(int direction)
    {
        var count = FilteredCommands.Count;
        if (count == 0) return;
        PaletteIndex = Math.Clamp(_paletteIndex + direction, 0, count - 1);
    }

    /// <summary>Picks the highlighted command (Enter in the palette).</summary>
    public bool PickPaletteCommand()
    {
        var command = FilteredCommands.ElementAtOrDefault(_paletteIndex);
        if (command is null) return false;
        PickCommand(command);
        return true;
    }

    /// <summary>
    /// Applies the highlighted mention row: an agent or file is typed into the text, a session becomes a reference
    /// attached to the next prompt (the server injects the hint and reads it with <c>session_read</c>).
    /// </summary>
    public void PickMention(ComposerMentionRow row)
    {
        if (row.IsHeader) return;
        var start = _mentionStart;
        var end = _mentionEnd;
        if (row.Session is { } session)
        {
            AddReference(session);
            CloseMention();
            FocusRequested?.Invoke();
            return;
        }
        var insertion = row.Insertion;
        if (insertion is null)
        {
            CloseMention();
            return;
        }
        var text = _text;
        if (start < 0 || end > text.Length) return;
        SetTextSilently(text[..start] + insertion + text[end..]);
        CloseMention();
        UpdateSurfaces(start + insertion.Length);
        FocusRequested?.Invoke();
    }

    /// <summary>Picks the highlighted row (Enter/Tab in the mention popup).</summary>
    public bool PickHighlightedMention()
    {
        var rows = MentionRows;
        if (_mentionIndex < 0 || _mentionIndex >= rows.Count) return false;
        var row = rows[_mentionIndex];
        if (row.IsHeader) return false;
        PickMention(row);
        return true;
    }

    /// <summary>The first selectable row index (rows may start with a section header).</summary>
    private int FirstItemRow()
    {
        var rows = MentionRows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsHeader) return i;
        }
        return 0;
    }

    private void RefreshRecentSessions()
    {
        _recentSessions.Clear();
        if (_store is null) return;
        var sessions = _store.Workspaces
            .SelectMany(w => w.Conversations ?? [])
            .Where(s => s.ParentId is null && s.Id != _sessionId)
            .OrderByDescending(s => s.UpdatedAt)
            .Take(6);
        foreach (var session in sessions) _recentSessions.Add(new ComposerReference(session.Id, session.Title));
    }

    private async Task ScheduleSessionSearchAsync()
    {
        var query = _mentionQuery.Trim();
        if (query.Length == 0)
        {
            _searchSeq++;
            _sessionsLoading = false;
            _matchedSessions.Clear();
            RefreshRecentSessions();
            RaiseChanged();
            return;
        }
        if (_backend is null) return;
        _sessionsLoading = true;
        RaiseChanged();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await SearchSessionsAsync(query);
            return;
        }
        _sessionTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(180),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _sessionTimer!.Stop();
                _ = SearchSessionsAsync(_mentionQuery.Trim());
            });
        _sessionTimer.Stop();
        _sessionTimer.Start();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Searches the open server for sessions matching the mention query (the 180ms debounce the popup uses calls the
    /// same path; tests and the harness can await it directly).
    /// </summary>
    public Task RefreshSessionMentionsAsync() => SearchSessionsAsync(_mentionQuery.Trim());

    private async Task SearchSessionsAsync(string query)
    {
        if (_backend is null) return;
        var seq = ++_searchSeq;
        try
        {
            var json = await _backend.SearchAsync(query, null, includeArchived: true, limit: 6);
            if (seq != _searchSeq) return;
            _matchedSessions.Clear();
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in results.EnumerateArray())
                {
                    var id = Str(result, "sessionId");
                    var title = Str(result, "title");
                    if (string.IsNullOrEmpty(id) || id == _sessionId) continue;
                    _matchedSessions.Add(new ComposerReference(id, title ?? id));
                }
            }
        }
        catch
        {
            if (seq == _searchSeq) _matchedSessions.Clear();
        }
        finally
        {
            if (seq == _searchSeq)
            {
                _sessionsLoading = false;
                _mentionIndex = FirstItemRow();
                RaiseChanged();
            }
        }
    }

    // --- catalogs (agents, files, branch, models) ---------------------------------------------------------

    public async Task RefreshAsync()
    {
        RefreshModels();
        RefreshRecentSessions();
        if (_backend is null) return;
        await Task.WhenAll(RefreshAgentsAsync(), RefreshFilesAsync(), RefreshBranchAsync());
    }

    public async Task RefreshAgentsAsync()
    {
        if (_backend is null) return;
        try
        {
            var json = await _backend.AgentsAsync(_directory.Length > 0 ? _directory : null);
            _agents.Clear();
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in json.EnumerateArray())
                {
                    var id = Str(item, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    _agents.Add(new ComposerAgent(
                        id,
                        Str(item, "name") ?? id,
                        Str(item, "description") ?? "",
                        Str(item, "mode") ?? "primary",
                        Str(item, "source") ?? "builtin"));
                }
            }
        }
        catch
        {
            _agents.Clear();
        }
        RaiseChanged();
    }

    public async Task RefreshFilesAsync()
    {
        if (_backend is null || _directory.Length == 0)
        {
            _files.Clear();
            return;
        }
        try
        {
            var files = await _backend.FilesAsync(_directory);
            _files.Clear();
            _files.AddRange(files.Where(f => f.Length > 0));
        }
        catch
        {
            _files.Clear();
        }
        RaiseChanged();
    }

    public async Task RefreshBranchAsync()
    {
        if (_backend is null || _directory.Length == 0)
        {
            _branch = "";
            RaiseChanged();
            return;
        }
        try
        {
            var json = await _backend.VcsAsync(_directory);
            if (json.ValueKind == JsonValueKind.Object &&
                json.TryGetProperty("isRepo", out var isRepo) && isRepo.ValueKind == JsonValueKind.True)
            {
                _branch = Str(json, "branch") ?? "";
                if (_branch.Length == 0 && json.TryGetProperty("branches", out var branches) &&
                    branches.ValueKind == JsonValueKind.Array && branches.GetArrayLength() > 0)
                {
                    _branch = branches[0].GetString() ?? "";
                }
            }
            else
            {
                _branch = "";
            }
        }
        catch
        {
            _branch = "";
        }
        RaiseChanged();
    }

    public string Branch => _branch;

    private void RefreshModels()
    {
        _models.Clear();
        var json = _store?.Models;
        if (json is not { ValueKind: JsonValueKind.Array }) return;
        foreach (var model in json.Value.EnumerateArray())
        {
            var id = Str(model, "id");
            if (string.IsNullOrEmpty(id)) continue;
            if (model.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
            var parts = new List<string>();
            if (Str(model, "provider") is { Length: > 0 } provider) parts.Add(provider);
            if (Str(model, "context") is { Length: > 0 } context) parts.Add(context);
            if (model.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.False)
                parts.Add("offline");
            var local = model.TryGetProperty("local", out var isLocal) && isLocal.ValueKind == JsonValueKind.True;
            _models.Add(new ComposerModel(
                id,
                Str(model, "label") ?? id,
                string.Join(" Â· ", parts),
                Str(model, "providerIcon") ?? "cpu",
                local ? "local" : null));
        }
        if (_session?.ModelId is { Length: > 0 } current && _models.All(m => m.Id != current))
        {
            _models.Insert(0, new ComposerModel(current, current, "Not in the catalog", "globe"));
        }
    }

    // --- metrics and the context meter --------------------------------------------------------------------

    public string DurationLabel => ComposerFormat.Duration(IsReplying
        ? _durationMs
        : _session?.LastTurnMs is > 0 ? _session.LastTurnMs : _durationMs);

    public string CostLabel => "$" + _cost.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    public bool CostAvailable => _costAvailable;

    public string TokensPerSecLabel => $"{_tokensPerSec} tok/s";

    public string CacheHitLabel => ComposerFormat.HitPercentLabel(UsageCacheRead, UsageInput);

    public string CacheTitle
    {
        get
        {
            var lines = new List<string> { "Prompt cache hit rate (last request / whole session)" };
            if (UsageInput > 0)
            {
                lines.Add($"Session: {CacheHitLabel}% ({UsageCacheRead:N0} / {UsageInput:N0} tok)");
            }
            return string.Join('\n', lines);
        }
    }

    public long UsageInput => _session?.Usage.Input ?? 0;

    public long UsageCacheRead => _session?.Usage.CacheRead ?? 0;

    public ContextMeterView? Context => ContextMeter.Build(_contextUsed, _contextLimit, _contextSystem, _contextTools);

    /// <summary>Tokens used in the last request (popup <c>~used / limit</c> readout).</summary>
    public long ContextUsed => _contextUsed;

    /// <summary>Model window of the active session (0 when the server sent none).</summary>
    public long ContextLimit => _contextLimit;

    public bool ContextPinned
    {
        get => _contextPinned;
        set
        {
            if (_contextPinned == value) return;
            _contextPinned = value;
            _local.SetFlag(LocalState.ContextPopoverKey, "open", value);
            OnPropertyChanged();
        }
    }

    private void ResetMetricsFromSession()
    {
        if (_session is null) return;
        _durationMs = _session.LastTurnMs;
        _cost = _session.Usage.Cost;
        _costAvailable = _session.Usage.Cost > 0;
        _contextUsed = _session.Context.Used;
        _contextLimit = _session.Context.Limit;
        _contextSystem = _session.Context.SystemTokens;
        _contextTools = _session.Context.ToolTokens;
    }

    private void ApplyMetrics(JsonElement data)
    {
        _durationMs = Num(data, "durationMs") ?? _durationMs;
        _tokensPerSec = Num(data, "tokensPerSec") ?? _tokensPerSec;
        if (data.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number) _cost = cost.GetDouble();
        _costAvailable = data.TryGetProperty("costAvailable", out var available) &&
                         available.ValueKind == JsonValueKind.True;
        if (data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _cost = Num(usage, "cost") ?? _cost;
            if (_session is not null)
            {
                _session.Usage.Input = Num(usage, "input") ?? _session.Usage.Input;
                _session.Usage.CacheRead = Num(usage, "cacheRead") ?? _session.Usage.CacheRead;
            }
        }
        if (data.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
        {
            _contextUsed = Num(context, "used") ?? _contextUsed;
            _contextLimit = Num(context, "limit") ?? _contextLimit;
            _contextSystem = Num(context, "systemTokens") ?? _contextSystem;
            _contextTools = Num(context, "toolTokens") ?? _contextTools;
        }
        RaiseChanged();
    }

    // --- events -------------------------------------------------------------------------------------------

    private void OnApplied(SofiaEventDto evt)
    {
        switch (evt.Type)
        {
            case EventTypes.SessionMetrics when evt.SessionId == _sessionId:
                ApplyMetrics(evt.Data);
                break;
            case EventTypes.ConfigChanged:
                _commandsLoaded = false;
                _ = RefreshAgentsAsync();
                if (_paletteOpen) _ = LoadCommandsAsync(force: true);
                break;
            case EventTypes.SessionUpdated when evt.SessionId == _sessionId:
                try
                {
                    var session = evt.Data.Deserialize<Session>(JsonOptions);
                    if (session is not null) UpdateSession(session);
                }
                catch
                {
                    // A partial session event: the next snapshot fixes it.
                }
                break;
        }
    }

    // --- send / steer / queue / stop ----------------------------------------------------------------------

    /// <summary>
    /// Enter: runs a <c>/</c> command, prompts, or (while a run is active) delivers a steer/queue follow-up.
    /// Returns an error message, or null.
    /// </summary>
    public async Task<string?> SubmitAsync()
    {
        if (_steering) return null;
        if (!CanSend) return null;
        if (IsReplying) return await SendFollowUpAsync();
        if (await TryRunCommandAsync()) return null;
        return await SendMessageAsync();
    }

    private async Task<string?> SendMessageAsync()
    {
        var text = _text.Trim();
        var pending = Attachments.ToList();
        if (text.Length == 0 && pending.Count == 0) return null;
        var references = References.ToList();
        var sessionId = _sessionId;
        var payload = new PromptInput(
            text,
            null,
            "prompt",
            NonEmpty(ModelId),
            NonEmpty(Agent),
            PermissionMode,
            ReasoningMode,
            Localization.Current,
            references.Count > 0 ? references.Select(r => new SessionReference(r.Id, r.Title)).ToList() : null);
        ClearComposer();
        FlushDraft();
        SoundService.Current.Play(SoundKey.Send);
        if (_backend is null || sessionId.Length == 0) return null;
        try
        {
            var ids = await UploadAsync(pending);
            await _backend.PromptAsync(sessionId, payload with { Attachments = ids.Count > 0 ? ids : null });
            _error = null;
            RaiseChanged();
            return null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            RestoreDraft(sessionId, text, pending, references);
            RaiseChanged();
            return ex.Message;
        }
    }

    private async Task<string?> SendFollowUpAsync()
    {
        var text = _text.Trim();
        var pending = Attachments.ToList();
        if (text.Length == 0 && pending.Count == 0) return null;
        var before = _text;
        var sessionId = _sessionId;
        Steering = true;
        SoundService.Current.Play(SoundKey.Send);
        try
        {
            var ids = await UploadAsync(pending);
            if (_backend is not null && sessionId.Length > 0)
            {
                await _backend.PromptAsync(sessionId, new PromptInput(
                    text,
                    ids.Count > 0 ? ids : null,
                    Delivery,
                    NonEmpty(ModelId),
                    NonEmpty(Agent),
                    PermissionMode,
                    ReasoningMode,
                    Localization.Current));
            }
            foreach (var attachment in pending) Attachments.Remove(attachment);
            // Keep whatever the user typed while the steer was in flight (web behaviour).
            if (_text == before) SetTextSilently("");
            FlushDraft();
            _error = null;
            return null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            RaiseChanged();
            return ex.Message;
        }
        finally
        {
            Steering = false;
            OnPropertyChanged(nameof(CanSend));
            RaiseChanged();
        }
    }

    /// <summary>Stop (Ctrl+. or the stop button): aborts the running turn.</summary>
    public async Task StopAsync()
    {
        if (_backend is null || _sessionId.Length == 0) return;
        try
        {
            await _backend.AbortAsync(_sessionId);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            RaiseChanged();
        }
    }

    /// <summary>Removes a follow-up that is still waiting in the server inbox.</summary>
    public async Task<string?> RemoveQueuedAsync(string messageId)
    {
        if (_backend is null || _sessionId.Length == 0) return "No active conversation.";
        try
        {
            await _backend.RemoveQueuedAsync(_sessionId, messageId);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<List<string>> UploadAsync(IReadOnlyList<ComposerAttachment> pending)
    {
        var ids = new List<string>();
        foreach (var attachment in pending)
        {
            if (!string.IsNullOrEmpty(attachment.UploadedId))
            {
                ids.Add(attachment.UploadedId!);
                continue;
            }
            if (_backend is null) continue;
            Stream stream;
            try
            {
                stream = attachment.OpenRead();
            }
            catch (Exception ex)
            {
                attachment.Error = ex.Message;
                continue;
            }
            await using (stream)
            {
                var uploaded = await _backend.UploadAsync(stream, attachment.Name, attachment.Mime);
                attachment.UploadedId = uploaded.Id;
                attachment.Error = null;
                ids.Add(uploaded.Id);
            }
        }
        return ids;
    }

    // --- drafts -------------------------------------------------------------------------------------------

    /// <summary>
    /// Parks the open conversation's draft (text, attachment chips and references) in the local state. The shell
    /// calls this before switching conversations, and the setter path does it again on every submit.
    /// </summary>
    public void FlushDraft() => _drafts.Save(_sessionId, _text, Attachments, References);

    /// <summary>Marks a message reverted/retried as the composer content (web <c>restoreToComposer</c>).</summary>
    public void RestorePrompt(string text, IEnumerable<Attachment> attachments, Func<string, string>? urlOf = null)
    {
        SetTextSilently(text);
        Attachments.Clear();
        foreach (var attachment in attachments)
        {
            Attachments.Add(ComposerAttachment.FromMessage(
                attachment,
                ComposerFormat.KindOf(attachment.Mime) is "image" or "video"
                    ? urlOf?.Invoke(attachment.Id) ?? attachment.Url
                    : null));
        }
        UpdateSurfaces();
        FocusRequested?.Invoke();
        RaiseChanged();
    }

    public void RestoreDraft(string sessionId, string text, IEnumerable<ComposerAttachment> attachments,
        IEnumerable<ComposerReference> references)
    {
        if (sessionId == _sessionId)
        {
            if (_text.Length == 0) SetTextSilently(text);
            if (Attachments.Count == 0) Attachments.AddRange(attachments);
            foreach (var reference in references) AddReference(reference);
            UpdateSurfaces();
            RaiseChanged();
            return;
        }
        _drafts.Save(sessionId, text, attachments, references);
    }

    private void LoadDraft(string sessionId)
    {
        SetTextSilently("");
        Attachments.Clear();
        References.Clear();
        var draft = _drafts.Get(sessionId);
        if (draft is null) return;
        SetTextSilently(draft.Text);
        Attachments.AddRange(ComposerDraftStore.ToAttachments(draft));
        foreach (var reference in draft.References)
        {
            References.Add(new ComposerReference(reference.Id, reference.Title));
        }
    }

    private void ClearComposer()
    {
        SetTextSilently("");
        Attachments.Clear();
        References.Clear();
        _paletteOpen = false;
        _paletteIndex = 0;
        CloseMention();
        OnPropertyChanged(nameof(CanSend));
        RaiseChanged();
    }

    // --- local chip memory ---------------------------------------------------------------------------------

    private string? LocalChip(string key)
    {
        var chips = _local.Get<Dictionary<string, string>>(LocalState.ChipsKey);
        return chips is not null && chips.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }

    private void RememberChip(string key, string value)
    {
        var chips = _local.Get<Dictionary<string, string>>(LocalState.ChipsKey) ??
                    new Dictionary<string, string>(StringComparer.Ordinal);
        chips[key] = value;
        _local.Set(LocalState.ChipsKey, chips);
        // Chip memory is tiny and rare: write it at once so the next switch/restart reads the same fallback.
        _local.SaveNow();
    }

    // --- fixture preview states (screenshot gate) ---------------------------------------------------------

    /// <summary>
    /// Deterministic states for the screenshot gate: idle, focus, attachments, model, agent, permission, reasoning,
    /// running, steer, queue, palette and mention. Fills the catalogs from <see cref="Fixture"/> data so no backend or
    /// <c>~/.sofia</c> read is involved.
    /// </summary>
    public void PreviewState(string state)
    {
        CloseMenu();
        CloseMention();
        _paletteOpen = false;
        _error = null;
        Attachments.Clear();
        References.Clear();
        SetTextSilently("");
        SeedFixtureCatalogs();
        _status = SessionStatus.Idle;
        _tokensPerSec = 0;
        _durationMs = 0;

        switch (state)
        {
            case "focus":
                SetTextSilently("Port the composer from ChatComposer.vue, px for px.");
                break;
            case "attachments":
                AddPreviewAttachment("composer-shot.png", "image/png", 184_320, "image");
                AddPreviewAttachment("transcript-notes.md", "text/markdown", 4_096, "file");
                AddPreviewAttachment("walkthrough-accent-font-lg.webm", "video/webm", 2_411_724, "video");
                AddReference(new ComposerReference("s_3", "Working border motion parity"));
                SetTextSilently("Compare the composer with the web at 1080p.");
                break;
            case "model":
                Menu = ComposerMenu.Model;
                break;
            case "agent":
                Menu = ComposerMenu.Agent;
                break;
            case "permission":
                Menu = ComposerMenu.Permission;
                break;
            case "reasoning":
                Menu = ComposerMenu.Reasoning;
                break;
            case "running":
                SetTextSilently("Add the context meter legend.");
                SetStatus(SessionStatus.Working);
                break;
            case "steer":
                Delivery = "steer";
                Menu = ComposerMenu.Mode;
                SetTextSilently("Also cover the IME guard.");
                SetStatus(SessionStatus.Working);
                break;
            case "queue":
                Delivery = "queue";
                Menu = ComposerMenu.Mode;
                SetTextSilently("Then rerun the screenshot gate.");
                SetStatus(SessionStatus.Working);
                break;
            case "palette":
                SetTextSilently("/");
                UpdateSurfaces();
                break;
            case "mention":
                SetTextSilently("ask @");
                UpdateSurfaces();
                break;
        }
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(Delivery));
        OnPropertyChanged(nameof(SendIcon));
        OnPropertyChanged(nameof(SendTitle));
        ResetMetricsFromSession();
        RaiseChanged();
    }

    /// <summary>Fixture session for the screenshot gate (server-shaped values, no backend).</summary>
    public void SeedFixtureSession(Session session)
    {
        _session = session;
        _sessionId = session.Id;
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Agent));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(ReasoningMode));
    }

    /// <summary>Fixture catalogs for the screenshot gate.</summary>
    public void SeedFixtureCatalogs()
    {
        _models.Clear();
        _models.AddRange(Fixture.ComposerModels());
        _agents.Clear();
        _agents.AddRange(Fixture.ComposerAgents());
        _commands.Clear();
        _commands.AddRange(Fixture.ComposerCommands());
        _files.Clear();
        _files.AddRange(Fixture.ComposerFiles());
        _recentSessions.Clear();
        _recentSessions.AddRange(Fixture.ComposerSessions());
        _commandsLoaded = true;
        _branch = "feature/new_crossplatform";
        RaiseChanged();
    }

    private void AddPreviewAttachment(string name, string mime, long bytes, string kind) =>
        Attachments.Add(new ComposerAttachment
        {
            Name = name,
            Mime = mime,
            Bytes = bytes,
            Kind = kind,
            // The screenshot fixture shows the real 24x24 thumbnail path with an embedded asset (no filesystem read).
            PreviewUrl = kind == "image" ? "avares://Sofia.App/Assets/Logos/logo-42.png" : null,
        });

    /// <summary>Estimated context fixture for the screenshot gate (system/tools/messages split).</summary>
    public void SeedFixtureMetrics()
    {
        _durationMs = 8_140;
        _tokensPerSec = 42;
        _cost = 0.0187;
        _costAvailable = true;
        _contextUsed = 41_800;
        _contextLimit = 131_072;
        _contextSystem = 3_900;
        _contextTools = 7_420;
        RaiseChanged();
    }

    // --- helpers ------------------------------------------------------------------------------------------

    /// <summary>Rebuilds the surface after an in-place mutation of <see cref="Attachments"/>/<see cref="References"/>.</summary>
    public void ComposerChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        RaiseChanged();
    }

    public void RequestFocus() => FocusRequested?.Invoke();

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(PaletteVisible));
        OnPropertyChanged(nameof(PaletteQuery));
        OnPropertyChanged(nameof(FilteredCommands));
        OnPropertyChanged(nameof(MentionVisible));
        OnPropertyChanged(nameof(MentionRows));
        OnPropertyChanged(nameof(SessionMentions));
        OnPropertyChanged(nameof(FilteredMentions));
        OnPropertyChanged(nameof(FileMentions));
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(CostLabel));
        OnPropertyChanged(nameof(CostAvailable));
        OnPropertyChanged(nameof(TokensPerSecLabel));
        OnPropertyChanged(nameof(CacheHitLabel));
        OnPropertyChanged(nameof(CacheTitle));
        OnPropertyChanged(nameof(Context));
        Changed?.Invoke();
    }

    private static ComposerOption PermissionOf(string mode) =>
        PermissionModes.FirstOrDefault(p => p.Value == mode) ?? PermissionModes[2];

    private static ComposerOption ReasoningOf(string mode) =>
        ReasoningModes.FirstOrDefault(r => r.Value == mode) ?? ReasoningModes[2];

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string ShortModel(string id)
    {
        if (id.Length == 0) return "model";
        var name = id.Split('/').Last();
        return name.Length > 18 ? name[..17] + "..." : name;
    }

    private static string GuessMime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}
