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
        var sessions = db.Sessions.FindAll().ToList().GroupBy(s => s.WorkspaceId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Pinned).ThenBy(s => s.Order).ThenByDescending(s => s.UpdatedAt).ToList());
        return db.Workspaces.FindAll().OrderBy(w => w.Order).ThenBy(w => w.CreatedAt)
            .Select(w => WorkspaceView.From(w, sessions.GetValueOrDefault(w.Id) ?? []))
            .ToList();
    }

    public void PublishWorkspace(Workspace w) =>
        bus.Publish(EventTypes.WorkspaceUpdated, WorkspaceView.From(w, null), workspaceId: w.Id);

    /// <summary>
    /// Creates a session. The model comes from the template when one is given (branching, cloning, importing keep the
    /// source's model), otherwise from the workspace's base model; `workspaceModelFirst` makes a brand new conversation
    /// start on the workspace model even when the workspace has other chats. A model that is gone or hidden in Settings
    /// never survives: the server default takes over.
    /// </summary>
    public Session CreateSession(Workspace workspace, string? title = null, Session? template = null, string? parentId = null, string? agent = null, bool workspaceModelFirst = false)
    {
        var modelId = workspaceModelFirst
            ? FirstNonEmpty(workspace.ModelId, template?.ModelId)
            : FirstNonEmpty(template?.ModelId, workspace.ModelId);
        if (!IsUsable(modelId)) modelId = providers.DefaultModelId();
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

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>A model is only kept when the catalog knows it and Settings has not switched it off.</summary>
    private bool IsUsable(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && providers.Resolve(modelId) is not null && !Db.GetConfig().DisabledModels.Contains(modelId);

    /// <summary>
    /// Forks a conversation: a new session with a copy of the history up to and including
    /// <paramref name="target"/> (subagent conversations and file snapshots travel with it).
    /// The source is left untouched; workspace files are shared, not forked.
    /// </summary>
    public Session BranchSession(Session source, Workspace workspace, Message target)
    {
        var branch = CreateSession(workspace, $"{source.Title} (branch)", source);
        branch.ActiveSkills = [.. source.ActiveSkills];
        branch.AllowedTools = [.. source.AllowedTools];
        var copied = CopyHistory(source.Id, branch, target.Seq);
        foreach (var m in copied) if (m.Usage is { } usage) branch.Usage.Add(usage);
        branch.KnowledgeSeq = copied.Count == 0 ? 0 : copied.Max(m => m.Seq);
        SaveSession(branch);
        return branch;
    }

    private List<Message> CopyHistory(string fromId, Session to, long maxSeq)
    {
        var copies = new List<Message>();
        var newIds = new Dictionary<string, Message>();
        foreach (var m in db.SessionMessages(fromId).Where(m => m.Seq <= maxSeq))
        {
            var oldId = m.Id;
            m.Id = Ids.New("msg");
            m.SessionId = to.Id;
            m.Seq = db.NextSeq();
            foreach (var call in m.ToolCalls ?? [])
                if (call.ChildSessionId is { } childId && db.Sessions.FindById(childId) is { } child)
                    call.ChildSessionId = CloneChild(child, to).Id;
            db.Messages.Insert(m);
            copies.Add(m);
            newIds[oldId] = m;
        }
        foreach (var snapshot in db.Snapshots.Find(s => s.SessionId == fromId).ToList())
        {
            if (!newIds.TryGetValue(snapshot.MessageId, out var message)) continue;
            snapshot.Id = Ids.New("snp");
            snapshot.SessionId = to.Id;
            snapshot.MessageId = message.Id;
            snapshot.MessageSeq = message.Seq;
            db.Snapshots.Insert(snapshot);
        }
        return copies;
    }

    private Session CloneChild(Session child, Session parent)
    {
        var workspace = db.Workspaces.FindById(child.WorkspaceId) ?? throw new NotFoundException("Workspace not found.");
        var clone = CreateSession(workspace, child.Title, child, parent.Id, child.Agent);
        clone.Usage = child.Usage.Clone();
        clone.Context.Used = 0;
        CopyHistory(child.Id, clone, long.MaxValue);
        SaveSession(clone);
        return clone;
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

    /// <summary>Pins a conversation to the top of its list (first among the pinned) or releases it.</summary>
    public void SetPinned(Session session, bool pinned)
    {
        session.Pinned = pinned;
        session.Order = pinned
            ? Math.Min(0, db.Sessions.Find(s => s.WorkspaceId == session.WorkspaceId).Min(s => s.Order)) - 1
            : 0;
        db.Sessions.Update(session);
        bus.Publish(EventTypes.SessionUpdated, session, session.Id, session.WorkspaceId);
    }

    /// <summary>Stores the manual order of a workspace's conversations (first id = top of the list).</summary>
    public void Reorder(string workspaceId, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            var session = db.Sessions.FindById(ids[i]);
            if (session is null || session.WorkspaceId != workspaceId || session.Order == i + 1) continue;
            session.Order = i + 1;
            db.Sessions.Update(session);
        }
    }

    /// <summary>
    /// Stores the manual order of the sidebar tree (first id = top). The UI sends
    /// the folder workspaces first and the chat groups after them; workspaces the
    /// client does not know (created meanwhile by another tab) keep their place at
    /// the end instead of being lost.
    /// </summary>
    public void ReorderWorkspaces(IReadOnlyList<string> ids)
    {
        var all = db.Workspaces.FindAll().OrderBy(w => w.Order).ThenBy(w => w.CreatedAt).ToList();
        var byId = all.ToDictionary(w => w.Id);
        var ordered = ids.Select(id => byId.GetValueOrDefault(id)).OfType<Workspace>().ToList();
        var known = ordered.Select(w => w.Id).ToHashSet();
        ordered.AddRange(all.Where(w => !known.Contains(w.Id)));
        for (var i = 0; i < ordered.Count; i++)
        {
            var workspace = ordered[i];
            if (workspace.Order == i + 1) continue;
            workspace.Order = i + 1;
            db.Workspaces.Update(workspace);
            PublishWorkspace(workspace);
        }
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
        db.Compact();
        foreach (var child in children)
            bus.Publish(EventTypes.SessionDeleted, new { id = child.Id, workspaceId = child.WorkspaceId }, child.Id, child.WorkspaceId);
        bus.Publish(EventTypes.SessionDeleted, new { id = session.Id, workspaceId = session.WorkspaceId }, session.Id, session.WorkspaceId);
    }
}
