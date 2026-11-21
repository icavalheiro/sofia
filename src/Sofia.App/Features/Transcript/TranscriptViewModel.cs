using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Features.Transcript.CardViewModels;
using Sofia.App.Services;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;
using Sofia.Contracts.Infrastructure;

namespace Sofia.App.Features.Transcript;

/// <summary>A rendered transcript entry: a message row or an elided "(…)" block.</summary>
public sealed record TranscriptRow(MessageViewModel? Message, int HiddenCount = 0)
{
    public bool IsGap => Message is null;
}

/// <summary>
/// The transcript's state (web <c>ChatMessages.vue</c> + the message handling of <c>useHarness.ts</c>): the loaded
/// messages in order, the 70-message window with the "(…)" gap, the queued steer/follow-ups, the session status that
/// drives the single spinner, the coalesced stream appends, the prompt-cache annotations and the composer height the
/// scroll host reserves at the bottom.
/// </summary>
public sealed class TranscriptViewModel : ObservableObject
{
    /// <summary>How many transcript messages stay rendered (web <c>MESSAGE_WINDOW</c>).</summary>
    public const int MessageWindow = 70;

    private static readonly JsonSerializerOptions JsonOptions = Json.Create();

    private readonly List<MessageViewModel> _messages = [];
    private readonly Dictionary<string, MessageViewModel> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageViewModel> _pending = new(StringComparer.Ordinal);
    private string _status = "idle";
    private double _composerHeight = 130;
    private bool _loaded;

    public TranscriptViewModel()
    {
        Actions = new TranscriptActions();
    }

    /// <summary>What the cards call to mutate server state (permission, question, revert, branch, retry, knowledge).</summary>
    public TranscriptActions Actions { get; }

    /// <summary>Draft picks of the multi-question carousel, kept across message updates.</summary>
    public QuestionDraftStore Drafts { get; } = new();

    /// <summary>Follow-ups accepted by the server but not yet sent to the model.</summary>
    public ObservableCollection<MessageViewModel> Pending { get; } = [];

    public IReadOnlyList<MessageViewModel> Messages => _messages;

    public string? SessionId { get; private set; }

    public bool IsLoaded => _loaded;

    /// <summary>Server status of the open session: idle, working, waiting, permission, question or error.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(Waiting));
            OnPropertyChanged(nameof(ShowSpinner));
            Changed?.Invoke();
        }
    }

    public bool IsWorking => _status is "working";

    public bool Waiting => _status == "waiting";

    /// <summary>True once the model started answering (text, reasoning, tools or a prompt for the user).</summary>
    public bool Answering
    {
        get
        {
            if (_messages.Count == 0) return false;
            var last = _messages[^1];
            if (!last.IsAssistant) return false;
            return !string.IsNullOrWhiteSpace(last.Text) || !string.IsNullOrWhiteSpace(last.Reasoning) ||
                   last.Tools.Count > 0 || last.Cards.Permission is not null || last.Cards.Question is not null ||
                   last.Cards.QuestionSet is not null;
        }
    }

    /// <summary>The spinner only covers the wait for the first output of a run.</summary>
    public bool ShowSpinner => (IsWorking || Waiting) && !Answering;

    /// <summary>
    /// Height of the overlay composer, published by the shell (web <c>--sofia-composer-height</c>): the scroll host pads
    /// the content by this much plus 1.25rem so the last row is never covered.
    /// </summary>
    public double ComposerHeight
    {
        get => _composerHeight;
        set
        {
            if (Math.Abs(_composerHeight - value) < 0.5) return;
            _composerHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BottomPadding));
        }
    }

    /// <summary>Transcript padding: 1.25rem top/sides (in the view) and the composer height + 1.25rem at the bottom.</summary>
    public double BottomPadding => ComposerHeight + 20;

    /// <summary>The row window: the newest 70 messages plus every user prompt and the first reply, gaps in between.</summary>
    public IReadOnlyList<TranscriptRow> Rows => BuildRows();

    /// <summary>Raised when the structural state changed (load, update, status, queued rows) and the view must rebuild.</summary>
    public event Action? Changed;

    /// <summary>Raised when a row was appended/removed and the view should scroll to the bottom while following.</summary>
    public event Action? Grew;

    // --- loading -------------------------------------------------------------------------------------------

    /// <summary>Replaces the transcript with a full session snapshot.</summary>
    public void Load(IEnumerable<Message> messages, string? sessionId, string? status = null,
        IEnumerable<QueuedMessage>? queued = null)
    {
        SessionId = sessionId ?? SessionId;
        foreach (var message in _messages) Detach(message);
        _messages.Clear();
        _byId.Clear();
        foreach (var message in messages.Where(m => m.Id.Length > 0)) Add(message, rebuild: false);
        _loaded = true;
        if (status is not null) Status = status;
        RebuildStreaming();
        AnnotateCache();
        ApplyQueued(queued);
        Changed?.Invoke();
        Grew?.Invoke();
    }

    /// <summary>Loads the session transcript over the backend (<c>GET /api/sessions/{id}</c>).</summary>
    public async Task LoadSessionAsync(ISofiaBackend backend, string sessionId, CancellationToken ct = default)
    {
        Actions.Backend = backend;
        SessionId = sessionId;
        try
        {
            var json = await backend.SessionAsync(sessionId, ct);
            var messages = ReadMessages(json);
            var status = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("status", out var value)
                ? value.GetString()
                : null;
            Load(messages, sessionId, status);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("queuedMessages", out var queued))
            {
                ApplyQueued(queued);
            }
        }
        catch (Exception ex)
        {
            SetActionError(ex.Message);
        }
    }

    public void Detach()
    {
        foreach (var message in _messages) Detach(message);
        _messages.Clear();
        _byId.Clear();
        _pending.Clear();
        Pending.Clear();
        SessionId = null;
        _loaded = false;
        _status = "idle";
        Changed?.Invoke();
    }

    public void SetBackend(ISofiaBackend? backend) => Actions.Backend = backend;

    public void SetStatus(string status)
    {
        if (Status != status) Status = status;
    }

    // --- live events ---------------------------------------------------------------------------------------

    /// <summary>Applies one domain event for the open session.</summary>
    public void Apply(SofiaEventDto evt)
    {
        switch (evt.Type)
        {
            case EventTypes.MessageCreated:
            case EventTypes.MessageUpdated:
            {
                if (evt.SessionId is not null && SessionId is not null && evt.SessionId != SessionId) break;
                var message = Deserialize<Message>(evt.Data);
                if (message is null || message.Id.Length == 0) break;
                if (evt.Type == EventTypes.MessageCreated && (_byId.TryGetValue(message.Id, out var existing) &&
                                                              !existing.IsAssistant && message.Text.Length == 0))
                {
                    existing.Apply(message);
                    break;
                }
                if (_byId.TryGetValue(message.Id, out var update))
                {
                    update.Apply(message);
                }
                else
                {
                    Add(message, rebuild: true);
                }
                RebuildStreaming();
                AnnotateCache();
                Changed?.Invoke();
                Grew?.Invoke();
                break;
            }

            case EventTypes.MessageDeleted:
            {
                if (evt.SessionId is not null && SessionId is not null && evt.SessionId != SessionId) break;
                foreach (var id in ReadDeletedIds(evt.Data))
                {
                    if (!_byId.Remove(id, out var message)) continue;
                    Detach(message);
                    _messages.Remove(message);
                }
                RebuildStreaming();
                AnnotateCache();
                Changed?.Invoke();
                break;
            }

            case EventTypes.SessionStatus:
            {
                if (evt.SessionId is not null && SessionId is not null && evt.SessionId != SessionId) break;
                if (evt.Data.ValueKind == JsonValueKind.Object && evt.Data.TryGetProperty("status", out var status) &&
                    status.GetString() is { } value)
                {
                    Status = value;
                }
                break;
            }

            case EventTypes.SessionCreated:
            case EventTypes.SessionUpdated:
            {
                if (evt.SessionId is not null && SessionId is not null && evt.SessionId != SessionId) break;
                var session = Deserialize<Session>(evt.Data);
                if (session is null) break;
                if (evt.SessionId == SessionId) ApplyQueued(session.QueuedMessages);
                break;
            }
        }
    }

    /// <summary>Appends one coalesced stream delta (raised by <see cref="HarnessStore.DeltaFlushed"/>).</summary>
    public void AppendDelta(string messageId, string field, string text)
    {
        if (!_byId.TryGetValue(messageId, out var message)) return;
        message.AppendDelta(field, text);
        OnPropertyChanged(nameof(Answering));
        OnPropertyChanged(nameof(ShowSpinner));
        Grew?.Invoke();
    }

    /// <summary>Re-applies the last status after the row list changed (the spinner depends on the last row).</summary>
    public void Recompute()
    {
        RebuildStreaming();
        OnPropertyChanged(nameof(Answering));
        OnPropertyChanged(nameof(ShowSpinner));
    }

    private void Add(Message message, bool rebuild)
    {
        var model = new MessageViewModel(message, Actions, Drafts);
        model.Changed += () =>
        {
            Recompute();
            Changed?.Invoke();
        };
        model.Streamed += () =>
        {
            Recompute();
            Grew?.Invoke();
        };
        _messages.Add(model);
        _byId[message.Id] = model;
        if (rebuild) Recompute();
    }

    private void Detach(MessageViewModel message)
    {
        // Events are subscribed with lambdas; the row list is rebuilt wholesale, so dropping the reference is enough.
        _byId.Remove(message.Id);
    }

    private void RebuildStreaming()
    {
        for (var i = 0; i < _messages.Count; i++)
        {
            _messages[i].SetStreaming(IsWorking && i == _messages.Count - 1 && _messages[i].IsAssistant);
        }
    }

    private void AnnotateCache()
    {
        var breaks = CacheHealth.Annotate(_messages.Select(m => m.Model).ToList());
        for (var i = 0; i < _messages.Count && i < breaks.Count; i++) _messages[i].CacheBreak = breaks[i];
    }

    private IReadOnlyList<TranscriptRow> BuildRows()
    {
        if (_messages.Count == 0) return [];
        if (_messages.Count <= MessageWindow)
        {
            return _messages.Select(m => new TranscriptRow(m)).ToList();
        }

        // Always readable: what the user asked, plus the first reply the model gave.
        var firstReply = _messages.FindIndex(m => m.IsAssistant && !m.IsCompaction);
        var windowStart = _messages.Count - MessageWindow;
        var rows = new List<TranscriptRow>();
        var hidden = 0;
        for (var i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var visible = i >= windowStart || message.Role == "user" || i == firstReply;
            if (visible)
            {
                if (hidden > 0)
                {
                    rows.Add(new TranscriptRow(null, hidden));
                    hidden = 0;
                }
                rows.Add(new TranscriptRow(message));
                continue;
            }
            hidden++;
        }
        if (hidden > 0) rows.Add(new TranscriptRow(null, hidden));
        return rows;
    }

    private void ApplyQueued(IEnumerable<QueuedMessage>? queued)
    {
        if (queued is null) return;
        var shown = new HashSet<string>(_messages.Select(m => m.Id), StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in queued)
        {
            if (shown.Contains(item.Id)) continue;
            keep.Add(item.Id);
            if (_pending.ContainsKey(item.Id)) continue;
            var message = new Message
            {
                Id = item.Id,
                SessionId = SessionId ?? "",
                Role = "user",
                Text = item.Text,
                Attachments = item.Attachments,
            };
            var model = new MessageViewModel(message, Actions, Drafts) { PendingDelivery = item.Delivery };
            model.Changed += () =>
            {
                Changed?.Invoke();
            };
            _pending[item.Id] = model;
            Pending.Add(model);
        }

        // Drop previews the server no longer holds (sent, removed or replayed as a real message).
        foreach (var stale in _pending.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            var model = _pending[stale];
            _pending.Remove(stale);
            Pending.Remove(model);
        }
        Changed?.Invoke();
    }

    private void ApplyQueued(JsonElement queued) => ApplyQueued(Deserialize<List<QueuedMessage>>(queued));

    private void SetActionError(string error)
    {
        // No row to hang it on (the load itself failed): surface it as an error row.
        var model = new Message
        {
            Id = "local_error_" + Guid.NewGuid().ToString("N")[..8],
            SessionId = SessionId ?? "",
            Role = "assistant",
            Text = "",
            Error = error,
        };
        Add(model, rebuild: true);
        Changed?.Invoke();
    }

    private static List<Message> ReadMessages(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("messages", out var messages))
        {
            return Deserialize<List<Message>>(messages) ?? [];
        }
        return json.ValueKind == JsonValueKind.Array ? Deserialize<List<Message>>(json) ?? [] : [];
    }

    private static IEnumerable<string> ReadDeletedIds(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) yield break;
        if (data.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.GetString() is { Length: > 0 } value) yield return value;
            }
        }
        if (data.TryGetProperty("id", out var single) && single.GetString() is { Length: > 0 } one) yield return one;
    }

    private static T? Deserialize<T>(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        try
        {
            return element.Deserialize<T>(JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}
