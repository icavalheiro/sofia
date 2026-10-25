using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Providers;

namespace Sofia.Server.Agent;

public sealed class WorkspaceView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "folder";
    public string Icon { get; set; } = "folder";
    public string ModelId { get; set; } = "";
    public bool Expanded { get; set; }
    public int Order { get; set; }
    public List<Session>? Conversations { get; set; }

    public static WorkspaceView From(Workspace w, List<Session>? sessions) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Path = w.Path,
        Kind = w.Kind,
        Icon = w.Icon,
        ModelId = w.ModelId,
        Expanded = w.Expanded,
        Order = w.Order,
        Conversations = sessions,
    };
}

public sealed class SessionWithMessages
{
    public required Session Session { get; init; }
    public required List<Message> Messages { get; init; }
}

/// <summary>Single entry point for persisted state changes that the UI must hear about.</summary>
public sealed class SessionStore(SofiaDb db, IEventBus bus, ProviderService providers)
{
    public SofiaDb Db => db;

    public List<WorkspaceView> Workspaces()
    {
        var sessions = db.Sessions.FindAll().ToList().GroupBy(s => s.WorkspaceId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.UpdatedAt).ToList());
        return db.Workspaces.FindAll().OrderBy(w => w.Order).ThenBy(w => w.CreatedAt)
            .Select(w => WorkspaceView.From(w, sessions.GetValueOrDefault(w.Id) ?? []))
            .ToList();
    }

    public void PublishWorkspace(Workspace w) =>
        bus.Publish(EventTypes.WorkspaceUpdated, WorkspaceView.From(w, null), workspaceId: w.Id);

    public Session CreateSession(Workspace workspace, string? title = null, Session? template = null, string? parentId = null, string? agent = null)
    {
        var modelId = template?.ModelId;
        if (string.IsNullOrEmpty(modelId)) modelId = workspace.ModelId;
        if (string.IsNullOrEmpty(modelId) || providers.Resolve(modelId) is null) modelId = providers.DefaultModelId();
        var session = new Session
        {
            Id = Ids.New("ses"),
            WorkspaceId = workspace.Id,
            ParentId = parentId,
            Title = string.IsNullOrWhiteSpace(title) ? Session.Defaults.Title : title.Trim(),
            ModelId = modelId ?? "",
            Agent = agent ?? template?.Agent ?? "build",
            PermissionMode = template?.PermissionMode ?? "ask",
            ReasoningMode = template?.ReasoningMode ?? "medium",
            Language = template?.Language,
        };
        if (providers.Resolve(session.ModelId) is { } resolved) session.Context.Limit = resolved.Model.ContextLength;
        db.Sessions.Insert(session);
        bus.Publish(EventTypes.SessionCreated, session, session.Id, workspace.Id);
        return session;
    }

    public void SaveSession(Session session, bool publish = true)
    {
        db.Sessions.Update(session);
        if (publish) bus.Publish(EventTypes.SessionUpdated, session, session.Id, session.WorkspaceId);
    }

    /// <summary>Archives (or restores) a conversation and tells every client: the UI parks it in the workspace's 'Archived' folder.</summary>
    public void SetArchived(Session session, bool archived)
    {
        session.Archived = archived;
        session.ArchivedAt = archived ? DateTime.UtcNow : null;
        db.Sessions.Update(session);
        bus.Publish(EventTypes.SessionUpdated, session, session.Id, session.WorkspaceId);
    }

    public void SetStatus(Session session, string status, string? error = null)
    {
        session.Status = status;
        db.Sessions.Update(session);
        bus.Publish(EventTypes.SessionStatus, new { status, error }, session.Id, session.WorkspaceId);
    }

    public Message AddMessage(Session session, Message message)
    {
        if (string.IsNullOrEmpty(message.Id)) message.Id = Ids.New("msg");
        message.SessionId = session.Id;
        message.Seq = db.NextSeq();
        message.CreatedAt = DateTime.UtcNow;
        lock (message)
        {
            db.Messages.Insert(message);
            bus.Publish(EventTypes.MessageCreated, message, session.Id, session.WorkspaceId);
        }
        session.UpdatedAt = DateTime.UtcNow;
        SaveSession(session, publish: message.Role == "user");
        return message;
    }

    /// <summary>Persists (optionally) and broadcasts a message snapshot. Callers mutate under lock(message).</summary>
    public void UpdateMessage(Session session, Message message, bool persist = true)
    {
        lock (message)
        {
            if (persist) db.Messages.Update(message);
            bus.Publish(EventTypes.MessageUpdated, message, session.Id, session.WorkspaceId);
        }
    }

    public void Delta(Session session, Message message, string field, string delta) =>
        bus.Publish(EventTypes.MessageDelta, new { messageId = message.Id, field, delta }, session.Id, session.WorkspaceId);

    public void DeleteMessages(Session session, List<string> ids)
    {
        foreach (var id in ids) db.Messages.Delete(id);
        bus.Publish(EventTypes.MessageDeleted, new { ids }, session.Id, session.WorkspaceId);
    }

    public void PublishMetrics(Session session, object metrics) =>
        bus.Publish(EventTypes.SessionMetrics, metrics, session.Id, session.WorkspaceId);

    public void DeleteSession(Session session)
    {
        var children = db.Sessions.Find(s => s.ParentId == session.Id).ToList();
        db.DeleteSession(session.Id);
        foreach (var child in children)
            bus.Publish(EventTypes.SessionDeleted, new { id = child.Id, workspaceId = child.WorkspaceId }, child.Id, child.WorkspaceId);
        bus.Publish(EventTypes.SessionDeleted, new { id = session.Id, workspaceId = session.WorkspaceId }, session.Id, session.WorkspaceId);
    }
}
