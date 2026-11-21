using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Services;

public enum BackendPhase
{
    Idle,
    Probing,
    Ready,
    /// <summary>A server answered but wants a paired token: the gate shows the pairing section.</summary>
    Pairing,
    Failed,
}

/// <summary>
/// Platform-specific backend discovery. The desktop head resolves it as: data-directory lock available â†’ in-process
/// Sofia.Core; else a server answering on <c>127.0.0.1:5199</c> â†’ HTTP; else nothing (the gate stays visible). The
/// browser head always resolves HTTP (spec 044).
/// </summary>
public interface IBackendResolver
{
    /// <summary>Short description shown under the gate title (e.g. "in-process engine" or "http://127.0.0.1:5199").</summary>
    string Description { get; }

    Task<ISofiaBackend?> ResolveAsync(Action<string> log, CancellationToken ct = default);
}

/// <summary>
/// A server answered but requires a paired token (a non-loopback client without one). The resolver hands the gate an
/// unauthenticated backend for the same URL so <c>PairAsync</c> can redeem the code, plus the URL to display.
/// </summary>
public sealed class PairingRequiredException(string serverUrl, ISofiaBackend pairingBackend, string message) : Exception(message)
{
    public string ServerUrl { get; } = serverUrl;

    /// <summary>Backend without a token: only <c>POST /api/pair/connect</c> works through it.</summary>
    public ISofiaBackend PairingBackend { get; } = pairingBackend;
}

/// <summary>
/// Connection state behind <c>BackendGate</c>: probe phase, the resolved backend, the probe log and the pairing call.
/// Nothing here touches the network itself â€” the head's <see cref="IBackendResolver"/> does.
/// </summary>
public sealed class BackendConnection : ObservableObject
{
    private BackendPhase _phase = BackendPhase.Idle;
    private ISofiaBackend? _backend;
    private string? _lastError;
    private string _serverUrl = HeadOptions.Current.DefaultServerUrl ?? "http://127.0.0.1:5199";
    private string? _accessToken;

    public BackendPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    /// <summary>The backend the store talks to, or null while the gate is up.</summary>
    public ISofiaBackend? Backend
    {
        get => _backend;
        private set => SetProperty(ref _backend, value);
    }

    public ObservableCollection<string> Log { get; } = [];

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string? AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public bool IsReady => Phase == BackendPhase.Ready;

    /// <summary>Runs the resolver once and records every step in <see cref="Log"/> for the gate.</summary>
    public async Task ConnectAsync(IBackendResolver resolver, CancellationToken ct = default)
    {
        Phase = BackendPhase.Probing;
        LastError = null;
        Append($"probing: {resolver.Description}");
        try
        {
            var backend = await resolver.ResolveAsync(Append, ct);
            if (backend is null)
            {
                Phase = BackendPhase.Failed;
                LastError = Localization.T("gate.failed");
                Append("failed: no backend answered");
                return;
            }
            Backend = backend;
            Phase = BackendPhase.Ready;
            Append("ready");
        }
        catch (OperationCanceledException)
        {
            Phase = BackendPhase.Idle;
        }
        catch (PairingRequiredException ex)
        {
            // The server is there but the browser/remote client has no paired token yet.
            Backend = ex.PairingBackend;
            ServerUrl = ex.ServerUrl;
            Phase = BackendPhase.Pairing;
            LastError = ex.Message;
            Append("pairing required");
        }
        catch (Exception ex)
        {
            Phase = BackendPhase.Failed;
            LastError = ex.Message;
            Append("failed: " + ex.Message);
        }
    }

    /// <summary>Marks a backend ready without a probe (fixture mode and tests).</summary>
    public void UseReady(ISofiaBackend backend, string? description = null)
    {
        Backend = backend;
        Phase = BackendPhase.Ready;
        LastError = null;
        if (description is not null) Append(description);
    }

    /// <summary>Redeems a pairing code on the connected backend; the token is kept for HTTP requests.</summary>
    public async Task<bool> PairAsync(string code, CancellationToken ct = default)
    {
        if (Backend is null) return false;
        Append("pairingâ€¦");
        var token = await Backend.PairAsync(code, ct);
        if (string.IsNullOrEmpty(token))
        {
            Append("pairing failed");
            return false;
        }
        AccessToken = token;
        PersistConnection();
        Append("paired");
        return true;
    }

    /// <summary>
    /// Stores the server URL and token the gate is showing (<c>sofia.connection</c>). The browser resolver reads it on
    /// the next probe, so a manual token or a pairing code survives a reload.
    /// </summary>
    public void PersistConnection() =>
        LocalState.Current.Set(LocalState.ConnectionKey, new { ServerUrl, AccessToken });

    private void Append(string line)
    {
        // The resolver may call back from a background thread (probe), so marshal to the UI thread.
        if (Dispatcher.UIThread.CheckAccess()) Log.Add(line);
        else Dispatcher.UIThread.Post(() => Log.Add(line));
    }
}

/// <summary>
/// UI-facing cache of the engine's data: workspace tree, live session statuses, the model catalogue and the streaming
/// text of the active session. Events are applied on the UI thread; <c>message.delta</c> payloads are coalesced per
/// message id and flushed on a short timer (the web does the same) so a fast stream repaints once per frame batch
/// instead of once per token.
/// </summary>
public sealed class HarnessStore : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = Sofia.Contracts.Infrastructure.Json.Create();

    private readonly Dictionary<(string MessageId, string Field), string> _pendingDeltas = new();
    private readonly DispatcherTimer _deltaTimer;
    private JsonElement? _models;
    private string _activeSessionId = "";
    private int _unreadEvents;

    public HarnessStore()
    {
        _deltaTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => FlushDeltas());
        _deltaTimer.Start();
    }

    public ObservableCollection<WorkspaceView> Workspaces { get; } = [];

    /// <summary>Sessions currently running/waiting, straight from the backend snapshot.</summary>
    public ObservableCollection<Session> LiveSessions { get; } = [];

    /// <summary>Session id â†’ status (<c>working</c>, <c>waiting</c>, <c>permission</c>, <c>question</c>, <c>error</c>â€¦).</summary>
    public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Streamed text accumulated per (message id, field), not yet consumed by the transcript. The server emits
    /// <c>message.delta</c> with <c>field: text | reasoning</c>; the web appends both directly, this store batches them
    /// for one repaint per frame batch.
    /// </summary>
    public IReadOnlyDictionary<(string MessageId, string Field), string> PendingDeltas => _pendingDeltas;

    public string ActiveSessionId
    {
        get => _activeSessionId;
        set => SetProperty(ref _activeSessionId, value);
    }

    public JsonElement? Models
    {
        get => _models;
        private set => SetProperty(ref _models, value);
    }

    /// <summary>Total events applied; the harness asserts coalescing kept the repaint count low.</summary>
    public int AppliedEvents => _unreadEvents;

    /// <summary>Raised when coalesced deltas are flushed (the transcript appends <c>field</c> text to the message).</summary>
    public event Action<string, string, string>? DeltaFlushed;

    /// <summary>Raised after one domain event has been applied to the cache (the sidebar slice listens for it).</summary>
    public event Action<SofiaEventDto>? Applied;

    public async Task LoadAsync(ISofiaBackend backend, CancellationToken ct = default)
    {
        var workspaces = await backend.ListWorkspacesAsync(ct);
        Workspaces.Clear();
        foreach (var workspace in workspaces.OrderBy(w => w.Order)) Workspaces.Add(workspace);

        Statuses.Clear();
        foreach (var (sessionId, status) in await backend.StatusesAsync(ct)) Statuses[sessionId] = status;

        LiveSessions.Clear();
        foreach (var workspace in Workspaces)
        foreach (var session in workspace.Conversations ?? [])
        {
            if (Statuses.ContainsKey(session.Id)) LiveSessions.Add(session);
        }

        try
        {
            Models = await backend.ModelsAsync(ct);
        }
        catch
        {
            Models = null;
        }
    }

    /// <summary>Applies one event. Call on the UI thread (see <see cref="ListenAsync"/>).</summary>
    public void Apply(SofiaEventDto evt)
    {
        _unreadEvents++;
        switch (evt.Type)
        {
            case EventTypes.WorkspaceUpdated:
                UpsertWorkspace(De<WorkspaceView>(evt.Data));
                break;

            case EventTypes.WorkspaceDeleted:
                Remove(Workspaces, w => w.Id == evt.WorkspaceId || w.Id == Str(evt.Data, "id"));
                break;

            case EventTypes.SessionCreated:
            case EventTypes.SessionUpdated:
            {
                var session = De<Session>(evt.Data);
                if (session is null) break;
                UpsertSession(session);
                break;
            }

            case EventTypes.SessionDeleted:
                foreach (var workspace in Workspaces)
                {
                    if (workspace.Conversations is null) continue;
                    for (var i = workspace.Conversations.Count - 1; i >= 0; i--)
                    {
                        if (workspace.Conversations[i].Id == evt.SessionId) workspace.Conversations.RemoveAt(i);
                    }
                }
                Statuses.Remove(evt.SessionId ?? "");
                break;

            case EventTypes.SessionStatus:
            {
                var status = Str(evt.Data, "status");
                if (evt.SessionId is null || status is null) break;
                Statuses[evt.SessionId] = status;
                OnPropertyChanged(nameof(Statuses));
                break;
            }

            case EventTypes.MessageDelta:
            {
                var messageId = Str(evt.Data, "messageId");
                var delta = Str(evt.Data, "delta");
                if (messageId is null || delta is null) break;
                var field = Str(evt.Data, "field") ?? "text";
                var key = (messageId, field);
                _pendingDeltas[key] = _pendingDeltas.TryGetValue(key, out var text) ? text + delta : delta;
                break;
            }

            case EventTypes.MessageCreated:
            case EventTypes.MessageUpdated:
            case EventTypes.MessageDeleted:
            {
                var messageId = Str(evt.Data, "id");
                if (messageId is not null) RemovePending(messageId);
                break;
            }
        }
        Applied?.Invoke(evt);
    }

    /// <summary>Emits and clears the coalesced text of every streaming message/field.</summary>
    public void FlushDeltas()
    {
        if (_pendingDeltas.Count == 0) return;
        var flushed = _pendingDeltas.ToArray();
        _pendingDeltas.Clear();
        foreach (var ((messageId, field), text) in flushed) DeltaFlushed?.Invoke(messageId, field, text);
    }

    /// <summary>Pumps the backend's event stream onto the UI thread until cancelled.</summary>
    public async Task ListenAsync(ISofiaBackend backend, CancellationToken ct = default)
    {
        try
        {
            await foreach (var evt in backend.EventsAsync(ct))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Apply(evt));
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
    }

    /// <summary>Drops every coalesced delta of one message (a full snapshot supersedes them).</summary>
    private void RemovePending(string messageId)
    {
        foreach (var key in _pendingDeltas.Keys.Where(k => k.MessageId == messageId).ToList()) _pendingDeltas.Remove(key);
    }

    private void UpsertWorkspace(WorkspaceView? workspace)
    {
        if (workspace is null) return;
        for (var i = 0; i < Workspaces.Count; i++)
        {
            if (Workspaces[i].Id != workspace.Id) continue;
            // workspace.updated carries no conversations (the event is small); merge the scalar fields into the
            // cached tree instead of dropping the rows the sidebar is rendering.
            var existing = Workspaces[i];
            existing.Name = workspace.Name;
            existing.Path = workspace.Path;
            existing.Kind = workspace.Kind;
            existing.Icon = workspace.Icon;
            existing.ModelId = workspace.ModelId;
            existing.Expanded = workspace.Expanded;
            existing.Order = workspace.Order;
            if (workspace.Conversations is not null) existing.Conversations = workspace.Conversations;
            return;
        }
        Workspaces.Add(workspace);
    }

    private void UpsertSession(Session session)
    {
        foreach (var workspace in Workspaces)
        {
            if (workspace.Id != session.WorkspaceId) continue;
            workspace.Conversations ??= [];
            for (var i = 0; i < workspace.Conversations.Count; i++)
            {
                if (workspace.Conversations[i].Id != session.Id) continue;
                workspace.Conversations[i] = session;
                return;
            }
            workspace.Conversations.Add(session);
            return;
        }
    }

    private static void Remove<T>(ObservableCollection<T> items, Func<T, bool> predicate)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (predicate(items[i])) items.RemoveAt(i);
        }
    }

    private static T? De<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
