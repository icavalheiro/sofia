using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Core.Events;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Services;

/// <summary>
/// Workspace lifecycle: create (with de-duplication by folder), patch, delete cascade and ordering. This used to live
/// inside the ASP.NET endpoints; the in-process host needs exactly the same rules, so it lives here instead.
/// </summary>
public sealed class WorkspaceService(SofiaDb db, SessionStore store, AgentService agents, IEventBus bus)
{
    public List<WorkspaceView> List() => store.Workspaces();

    public WorkspaceView Create(CreateWorkspaceRequest body)
    {
        var kind = body.Kind == "chat" ? "chat" : "folder";
        var path = "";
        if (kind == "folder")
        {
            if (string.IsNullOrWhiteSpace(body.Path)) throw new ArgumentException("A folder workspace needs a path.");
            path = PathUtil.Normalize(body.Path.Trim().Trim('"'));
            if (!Directory.Exists(path)) throw new ArgumentException($"Folder not found: {path}");
            var existing = db.Workspaces.FindAll().FirstOrDefault(w => w.Kind == "folder" && PathUtil.Same(w.Path, path));
            if (existing is not null)
                return WorkspaceView.From(existing, db.Sessions.Find(s => s.WorkspaceId == existing.Id).OrderByDescending(s => s.UpdatedAt).ToList());
        }
        var all = db.Workspaces.FindAll().ToList();
        var workspace = new Workspace
        {
            Id = Ids.New("ws"),
            Kind = kind,
            Path = path,
            Name = !string.IsNullOrWhiteSpace(body.Name) ? body.Name.Trim() : kind == "folder" ? System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path : "New group",
            Icon = body.Icon ?? (kind == "chat" ? "chat" : "folder"),
            ModelId = body.ModelId ?? "",
            Expanded = true,
            Order = all.Count == 0 ? 0 : kind == "folder" ? all.Min(w => w.Order) - 1 : all.Max(w => w.Order) + 1,
        };
        db.Workspaces.Insert(workspace);
        store.PublishWorkspace(workspace);
        var session = store.CreateSession(workspace);
        return WorkspaceView.From(workspace, [session]);
    }

    public WorkspaceView Update(string id, UpdateWorkspaceRequest body)
    {
        var workspace = db.Workspaces.FindById(id) ?? throw new NotFoundException("Workspace not found.");
        if (body.Name is { } name && name.Trim().Length > 0) workspace.Name = name.Trim();
        if (body.Icon is { } icon) workspace.Icon = icon;
        if (body.ModelId is { } model) workspace.ModelId = model;
        if (body.Expanded is { } expanded) workspace.Expanded = expanded;
        if (body.Order is { } order) workspace.Order = order;
        if (body.Sandbox is { } sandbox) workspace.Sandbox = sandbox;
        db.Workspaces.Update(workspace);
        store.PublishWorkspace(workspace);
        return WorkspaceView.From(workspace, null);
    }

    /// <summary>Deletes a workspace, its conversations (stopping live runs first) and everything scoped to it.</summary>
    public void Delete(string id)
    {
        var workspace = db.Workspaces.FindById(id) ?? throw new NotFoundException("Workspace not found.");
        foreach (var session in db.Sessions.Find(s => s.WorkspaceId == id).ToList())
        {
            agents.Abort(session.Id);
            db.DeleteSession(session.Id);
        }
        db.Workspaces.Delete(id);
        db.Knowledge.DeleteMany(k => k.Scope == KnowledgeScope.Workspace && k.WorkspaceId == id);
        db.DeleteWorkspaceMemory(id);
        db.DeleteWorkspaceTasks(id);
        db.Compact();
        bus.Publish(EventTypes.WorkspaceDeleted, new { id }, workspaceId: id);
    }

    public Session CreateSession(string workspaceId, CreateSessionRequest? body)
    {
        var workspace = db.Workspaces.FindById(workspaceId) ?? throw new NotFoundException("Workspace not found.");
        // Modes still come from the workspace's most recent chat; the model does not: a new chat starts on the
        // workspace's base model (see SessionStore.CreateSession).
        var template = db.Sessions.Find(s => s.WorkspaceId == workspaceId && s.ParentId == null).OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        return store.CreateSession(workspace, body?.Title, template, workspaceModelFirst: true, origin: body?.Origin);
    }

    public void Reorder(ReorderRequest body) => store.ReorderWorkspaces(body.Ids ?? []);

    public void ReorderSessions(string workspaceId, ReorderRequest body)
    {
        _ = db.Workspaces.FindById(workspaceId) ?? throw new NotFoundException("Workspace not found.");
        store.Reorder(workspaceId, body.Ids ?? []);
    }

    /// <summary>A session with its transcript, serialized the way the API returns it.</summary>
    public static JsonObject WithMessages(Session session, List<Message> messages)
    {
        var node = JsonSerializer.SerializeToNode(session, Json.Options)!.AsObject();
        node["messages"] = JsonSerializer.SerializeToNode(messages, Json.Options);
        return node;
    }
}
