using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Sofia.Server.Agent;
using Sofia.Server.Changes;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Providers;
using Sofia.Server.Security;

namespace Sofia.Server.Api;

public sealed record CreateWorkspaceRequest(string? Kind, string? Path, string? Name, string? Icon, string? ModelId);
public sealed record UpdateWorkspaceRequest(string? Name, string? Icon, string? ModelId, bool? Expanded, int? Order);
public sealed record CreateSessionRequest(string? Title);
public sealed record ReorderRequest(List<string>? Ids);
public sealed record UpdateSessionRequest(string? Title, string? ModelId, string? Agent, string? PermissionMode, string? ReasoningMode);
public sealed record CommandRequest(string Name, string? Arguments, List<string>? Attachments = null);
public sealed record RevertRequest(string MessageId);
public sealed record PermissionReplyRequest(string Decision);
public sealed record AnswerRequest(List<JsonElement?> Answers);
public sealed record SkillRequest(string SkillId);
public sealed record ImportRequest(string? WorkspaceId, Session Info, List<Message>? Messages);

public static class SessionEndpoints
{
    public static string Version => ProviderIdentity.Version;

    public static void MapSessionApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", name = "sofia", version = Version }));

        api.MapGet("/server", (IServer server, SofiaOptions options) =>
        {
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
            var urls = new List<string>();
            var reachable = false;
            foreach (var address in addresses)
            {
                var wildcard = address.Contains("0.0.0.0") || address.Contains("[::]") || address.Contains("+") || address.Contains("*");
                var uri = new Uri(address.Replace("+", "localhost").Replace("*", "localhost").Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"));
                var loopback = !wildcard && (uri.IsLoopback || uri.Host == "localhost");
                if (!loopback) reachable = true;
                // Always advertise the machine's network addresses, never the loopback one.
                if (wildcard || loopback) urls.AddRange(LocalAddresses().Select(ip => $"http://{ip}:{uri.Port}"));
                else urls.Add(address);
            }
            if (urls.Count == 0) urls.AddRange(addresses);
            return Results.Ok(new { version = Version, urls = urls.Distinct().ToList(), dataDir = options.DataDir, remoteAuth = reachable, networkReachable = reachable });
        });

        // --- workspaces ---------------------------------------------------------

        api.MapGet("/workspaces", (SessionStore store) => Results.Ok(store.Workspaces()));

        api.MapPost("/workspaces", (CreateWorkspaceRequest body, SofiaDb db, SessionStore store) =>
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
                {
                    return Results.Ok(WorkspaceView.From(existing, db.Sessions.Find(s => s.WorkspaceId == existing.Id).OrderByDescending(s => s.UpdatedAt).ToList()));
                }
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
            return Results.Ok(WorkspaceView.From(workspace, [session]));
        });

        api.MapPatch("/workspaces/{id}", (string id, UpdateWorkspaceRequest body, SofiaDb db, SessionStore store) =>
        {
            var workspace = db.Workspaces.FindById(id) ?? throw new NotFoundException("Workspace not found.");
            if (body.Name is { } name && name.Trim().Length > 0) workspace.Name = name.Trim();
            if (body.Icon is { } icon) workspace.Icon = icon;
            if (body.ModelId is { } model) workspace.ModelId = model;
            if (body.Expanded is { } expanded) workspace.Expanded = expanded;
            if (body.Order is { } order) workspace.Order = order;
            db.Workspaces.Update(workspace);
            store.PublishWorkspace(workspace);
            return Results.Ok(WorkspaceView.From(workspace, null));
        });

        api.MapDelete("/workspaces/{id}", (string id, SofiaDb db, SessionStore store, AgentService agents, Events.IEventBus bus) =>
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
            db.Compact();
            bus.Publish(Events.EventTypes.WorkspaceDeleted, new { id }, workspaceId: id);
            return Results.NoContent();
        });

        api.MapPost("/workspaces/{id}/sessions", (string id, CreateSessionRequest? body, SofiaDb db, SessionStore store) =>
        {
            var workspace = db.Workspaces.FindById(id) ?? throw new NotFoundException("Workspace not found.");
            // Modes still come from the workspace's most recent chat; the model does not: a new chat starts on the
            // workspace's base model (see SessionStore.CreateSession).
            var template = db.Sessions.Find(s => s.WorkspaceId == id && s.ParentId == null).OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
            return Results.Ok(store.CreateSession(workspace, body?.Title, template, workspaceModelFirst: true));
        });

        api.MapPut("/workspaces/order", (ReorderRequest body, SessionStore store) =>
        {
            store.ReorderWorkspaces(body.Ids ?? []);
            return Results.NoContent();
        });

        api.MapPut("/workspaces/{id}/sessions/order", (string id, ReorderRequest body, SofiaDb db, SessionStore store) =>
        {
            _ = db.Workspaces.FindById(id) ?? throw new NotFoundException("Workspace not found.");
            store.Reorder(id, body.Ids ?? []);
            return Results.NoContent();
        });

        // --- sessions -----------------------------------------------------------

        api.MapGet("/sessions/status", (AgentService agents) => Results.Ok(agents.Statuses()));

        api.MapGet("/sessions/{id}", (string id, AgentService agents, SofiaDb db) =>
        {
            var (session, _) = agents.Load(id);
            return Results.Ok(WithMessages(session, db.SessionMessages(id)));
        });

        api.MapPatch("/sessions/{id}", (string id, UpdateSessionRequest body, AgentService agents) =>
        {
            var (session, _) = agents.Load(id);
            return Results.Ok(agents.ApplySettings(session, body.ModelId, body.Agent, body.PermissionMode, body.ReasoningMode, body.Title));
        });

        api.MapDelete("/sessions/{id}", (string id, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            agents.Abort(id);
            store.DeleteSession(session);
            return Results.NoContent();
        });

        // Parking a conversation in its workspace's "Archived" folder. A live run is stopped
        // first so nothing keeps writing into a conversation the user put away.
        api.MapPost("/sessions/{id}/archive", (string id, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            agents.Abort(id);
            store.SetArchived(session, true);
            return Results.Ok(session);
        });

        api.MapPost("/sessions/{id}/unarchive", (string id, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            store.SetArchived(session, false);
            return Results.Ok(session);
        });

        api.MapPost("/sessions/{id}/pin", (string id, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            store.SetPinned(session, true);
            return Results.Ok(session);
        });

        api.MapPost("/sessions/{id}/unpin", (string id, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            store.SetPinned(session, false);
            return Results.Ok(session);
        });

        api.MapPost("/sessions/{id}/prompt", (string id, PromptInput body, AgentService agents) =>
            Results.Ok(new { messageId = agents.Prompt(id, body) }));

        api.MapPost("/sessions/{id}/command", async (string id, CommandRequest body, AgentService agents, CancellationToken ct) =>
            Results.Ok(new { messageId = await agents.CommandAsync(id, body.Name.TrimStart('/'), body.Arguments, body.Attachments, ct) }));

        api.MapPost("/sessions/{id}/abort", (string id, AgentService agents) =>
        {
            agents.Abort(id);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/compact", (string id, AgentService agents) =>
        {
            agents.Compact(id);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/revert", (string id, RevertRequest body, AgentService agents) =>
        {
            var (removed, restored) = agents.Revert(id, body.MessageId);
            return Results.Ok(new { removed, restoredFiles = restored });
        });

        api.MapPost("/sessions/{id}/branch", (string id, RevertRequest body, AgentService agents, SofiaDb db) =>
        {
            var branch = agents.Branch(id, body.MessageId);
            return Results.Ok(WithMessages(branch, db.SessionMessages(branch.Id)));
        });

        api.MapPost("/sessions/{id}/permissions/{requestId}", (string id, string requestId, PermissionReplyRequest body, AgentService agents) =>
        {
            agents.ReplyPermission(id, requestId, body.Decision);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/questions/{formId}", (string id, string formId, AnswerRequest body, AgentService agents) =>
        {
            agents.Answer(id, formId, body.Answers ?? []);
            return Results.NoContent();
        });

        api.MapGet("/sessions/{id}/diff", (string id, AgentService agents, ChangeTracker changes, SofiaDb db) =>
        {
            var (_, workspace) = agents.Load(id);
            var ids = new List<string> { id };
            ids.AddRange(Descendants(db, id));
            var diffs = ids.SelectMany(sid => changes.SessionDiff(sid, workspace.Path))
                .GroupBy(d => d.File).Select(g => g.Last()).ToList();
            return Results.Ok(diffs);
        });

        api.MapPost("/sessions/{id}/skills", (string id, SkillRequest body, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            if (!session.ActiveSkills.Contains(body.SkillId)) session.ActiveSkills.Add(body.SkillId);
            store.SaveSession(session);
            return Results.Ok(session);
        });

        api.MapDelete("/sessions/{id}/skills/{skillId}", (string id, string skillId, AgentService agents, SessionStore store) =>
        {
            var (session, _) = agents.Load(id);
            session.ActiveSkills.Remove(skillId);
            store.SaveSession(session);
            return Results.Ok(session);
        });

        api.MapGet("/sessions/{id}/export", (string id, AgentService agents, SofiaDb db) =>
        {
            var (session, _) = agents.Load(id);
            return Results.Ok(new { version = 1, info = session, messages = db.SessionMessages(id) });
        });

        api.MapPost("/sessions/import", (ImportRequest body, SofiaDb db, SessionStore store) =>
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
            return Results.Ok(session);
        });

        api.MapGet("/stats", (SofiaDb db) => Results.Ok(Stats(db)));
    }

    private static IEnumerable<string> Descendants(SofiaDb db, string id)
    {
        foreach (var child in db.Sessions.Find(s => s.ParentId == id).ToList())
        {
            yield return child.Id;
            foreach (var grandchild in Descendants(db, child.Id)) yield return grandchild;
        }
    }

    internal static JsonObject WithMessages(Session session, List<Message> messages)
    {
        var node = JsonSerializer.SerializeToNode(session, Json.Options)!.AsObject();
        node["messages"] = JsonSerializer.SerializeToNode(messages, Json.Options);
        return node;
    }

    private static object Stats(SofiaDb db)
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
        return new
        {
            sessions = sessions.Count,
            prompts,
            steps,
            tokens = new { input = sessions.Sum(s => s.Usage.Input), output = sessions.Sum(s => s.Usage.Output) },
            cost = sessions.Sum(s => s.Usage.Cost),
            activeDays = days.Count,
            streak,
        };
    }

    /// <summary>Private-network IPv4 addresses of this machine (loopback and link-local excluded), gateway-routed NICs first.</summary>
    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(a)
                    && !(a.GetAddressBytes() is [169, 254, ..]))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
