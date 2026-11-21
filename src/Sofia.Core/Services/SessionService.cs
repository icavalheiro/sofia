using Sofia.Contracts.Domain;
using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Core.Events;
using Sofia.Core.Infrastructure;
using Sofia.Core.Mcp;

namespace Sofia.Core.Services;

/// <summary>One task list entry, as published on <c>tasks.updated</c>.</summary>
public sealed record TaskView(string Id, string Title, string Status, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Session lifecycle beyond the agent loop itself: delete (which also tears down session-scoped MCP servers), the
/// task list, export/import and the stats totals. Shared by every host.
/// </summary>
public sealed class SessionService(SofiaDb db, SessionStore store, AgentService agents, McpManager mcp, IEventBus bus)
{
    public Dictionary<string, string> Statuses() => agents.Statuses();

    public object Export(string id)
    {
        var (session, _) = agents.Load(id);
        return new { version = 1, info = session, messages = db.SessionMessages(id) };
    }

    /// <summary>
    /// Deleting a conversation also stops its run and drops the MCP servers the editor bridge attached to it.
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        var (session, _) = agents.Load(id);
        agents.Abort(id);
        await mcp.RemoveSessionAsync(id);
        store.DeleteSession(session);
    }

    public async Task<McpServerView> AddSessionMcpAsync(string sessionId, SessionMcpRequest body, CancellationToken ct)
    {
        agents.Load(sessionId);
        var transport = body.Transport == "web" ? "web" : "command";
        if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Name is required.");
        if (transport == "command" && string.IsNullOrWhiteSpace(body.Command) && (body.Args is null || body.Args.Count == 0))
            throw new ArgumentException("Command is required.");
        if (transport == "web" && !Uri.TryCreate(body.Url, UriKind.Absolute, out _)) throw new ArgumentException("A valid URL is required.");
        var command = body.Command?.Trim() ?? "";
        if (body.Args is { Count: > 0 })
            command = (command + " " + string.Join(" ", body.Args)).Trim();
        return await mcp.AddEphemeralAsync(sessionId, new McpServerConfig
        {
            Name = body.Name.Trim(),
            Transport = transport,
            Command = command,
            Url = body.Url?.Trim() ?? "",
            Token = string.IsNullOrWhiteSpace(body.Token) ? null : body.Token.Trim(),
            Env = body.Env ?? [],
        }, ct);
    }

    public Task RemoveSessionMcpAsync(string sessionId) => mcp.RemoveSessionAsync(sessionId);

    public List<TaskItem> Tasks(string sessionId) =>
        db.Tasks.Find(t => t.SessionId == sessionId).OrderBy(t => t.Order).ThenBy(t => t.CreatedAt).ToList();

    /// <summary>Clears the session's task list and publishes the empty list so every client drops it.</summary>
    public void DeleteTasks(string sessionId)
    {
        var (_, workspace) = agents.Load(sessionId);
        db.Tasks.DeleteMany(t => t.SessionId == sessionId);
        var remaining = Tasks(sessionId)
            .Select(t => new TaskView(t.Id, t.Title, t.Status, t.CreatedAt, t.UpdatedAt))
            .ToList();
        bus.Publish(EventTypes.TasksUpdated, new { sessionId, tasks = remaining }, sessionId, workspace.Id);
    }

    /// <summary>
    /// Imports an exported conversation into a workspace: ids and sequences are renumbered, tool calls are closed out
    /// (a running call cannot be resumed) and knowledge cards are dropped because they point at local notes.
    /// </summary>
    public Session Import(ImportRequest body)
    {
        var workspace = (body.WorkspaceId is { } wid ? db.Workspaces.FindById(wid) : null)
            ?? db.Workspaces.FindAll().OrderBy(w => w.Order).FirstOrDefault()
            ?? throw new ArgumentException("Create a workspace first.");
        var session = store.CreateSession(workspace, body.Info.Title, body.Info);
        session.Usage = body.Info.Usage ?? new Usage();
        store.SaveSession(session);
        foreach (var m in body.Messages ?? [])
        {
            // Knowledge cards point at notes that live in this server's database; they do not travel.
            if (m.Knowledge is not null && string.IsNullOrEmpty(m.Text) && (m.ToolCalls?.Count ?? 0) == 0) continue;
            m.Knowledge = null;
            m.Id = Ids.New("msg");
            m.Seq = db.NextSeq();
            m.SessionId = session.Id;
            foreach (var call in m.ToolCalls ?? [])
            {
                call.Arguments = call.Input ?? "{}";
                call.Result = call.Status == "error" ? call.Error : call.Output;
                call.Completed = true;
                if (call.Status == "running") call.Status = "error";
            }
            // Card-only prompts are UI-only; a message that also carries text or tool calls
            // (a question card hosted by the reply) must stay in the model history.
            m.Synthetic = (m.Permission is not null || m.Question is not null || m.QuestionSet is not null)
                && string.IsNullOrEmpty(m.Text) && (m.ToolCalls?.Count ?? 0) == 0;
            db.Messages.Insert(m);
        }
        return session;
    }

    public Session Archive(string id)
    {
        var (session, _) = agents.Load(id);
        agents.Abort(id);
        store.SetArchived(session, true);
        return session;
    }

    public Session Unarchive(string id)
    {
        var (session, _) = agents.Load(id);
        store.SetArchived(session, false);
        return session;
    }

    public Session Pin(string id, bool pinned)
    {
        var (session, _) = agents.Load(id);
        store.SetPinned(session, pinned);
        return session;
    }

    public StatsView Stats()
    {
        var sessions = db.Sessions.FindAll().ToList();
        var prompts = db.Messages.Count(m => m.Role == "user");
        var steps = db.Messages.Count(m => m.Role == "assistant" && m.Usage != null);
        var days = db.Messages.FindAll().Select(m => m.CreatedAt.ToLocalTime().Date).Distinct().OrderByDescending(d => d).ToList();
        var streak = 0;
        var cursor = DateTime.Today;
        foreach (var day in days)
        {
            if (day == cursor) { streak++; cursor = cursor.AddDays(-1); }
            else if (day < cursor) break;
        }
        return new StatsView(
            sessions.Count,
            prompts,
            steps,
            new StatsTokens(sessions.Sum(s => s.Usage.Input), sessions.Sum(s => s.Usage.Output)),
            sessions.Sum(s => s.Usage.Cost),
            days.Count,
            streak);
    }
}
