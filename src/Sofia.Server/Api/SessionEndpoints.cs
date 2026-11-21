using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;
using Sofia.Core.Services;

namespace Sofia.Server.Api;

/// <summary>
/// HTTP binding only: every route below turns a request into one call on a Core service and a <c>Results.*</c>.
/// The behaviours themselves (cascades, ordering, imports, diff filtering) live in <c>Sofia.Core.Services</c> so the
/// in-process host runs the same code.
/// </summary>
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

        api.MapGet("/workspaces", (WorkspaceService workspaces) => Results.Ok(workspaces.List()));
        api.MapPost("/workspaces", (CreateWorkspaceRequest body, WorkspaceService workspaces) => Results.Ok(workspaces.Create(body)));
        api.MapPatch("/workspaces/{id}", (string id, UpdateWorkspaceRequest body, WorkspaceService workspaces) => Results.Ok(workspaces.Update(id, body)));
        api.MapDelete("/workspaces/{id}", (string id, WorkspaceService workspaces) =>
        {
            workspaces.Delete(id);
            return Results.NoContent();
        });
        api.MapPost("/workspaces/{id}/sessions", (string id, CreateSessionRequest? body, WorkspaceService workspaces) =>
            Results.Ok(workspaces.CreateSession(id, body)));
        api.MapPut("/workspaces/order", (ReorderRequest body, WorkspaceService workspaces) =>
        {
            workspaces.Reorder(body);
            return Results.NoContent();
        });
        api.MapPut("/workspaces/{id}/sessions/order", (string id, ReorderRequest body, WorkspaceService workspaces) =>
        {
            workspaces.ReorderSessions(id, body);
            return Results.NoContent();
        });

        // --- sessions -----------------------------------------------------------

        api.MapGet("/sessions/status", (SessionService sessions) => Results.Ok(sessions.Statuses()));

        api.MapGet("/sessions/{id}", (string id, AgentService agents, SofiaDb db) =>
        {
            var (session, _) = agents.Load(id);
            return Results.Ok(WorkspaceService.WithMessages(session, db.SessionMessages(id)));
        });

        api.MapPatch("/sessions/{id}", (string id, UpdateSessionRequest body, AgentService agents) =>
        {
            var (session, _) = agents.Load(id);
            return Results.Ok(agents.ApplySettings(session, body.ModelId, body.Agent, body.PermissionMode, body.ReasoningMode, body.Title, body.Sandbox));
        });

        api.MapDelete("/sessions/{id}", async (string id, SessionService sessions) =>
        {
            await sessions.DeleteAsync(id);
            return Results.NoContent();
        });

        // Parking a conversation in its workspace's "Archived" folder. A live run is stopped
        // first so nothing keeps writing into a conversation the user put away.
        api.MapPost("/sessions/{id}/archive", (string id, SessionService sessions) => Results.Ok(sessions.Archive(id)));
        api.MapPost("/sessions/{id}/unarchive", (string id, SessionService sessions) => Results.Ok(sessions.Unarchive(id)));
        api.MapPost("/sessions/{id}/pin", (string id, SessionService sessions) => Results.Ok(sessions.Pin(id, true)));
        api.MapPost("/sessions/{id}/unpin", (string id, SessionService sessions) => Results.Ok(sessions.Pin(id, false)));

        api.MapPost("/sessions/{id}/prompt", (string id, PromptInput body, AgentService agents) =>
            Results.Ok(new { messageId = agents.Prompt(id, body) }));

        api.MapPost("/sessions/{id}/command", async (string id, CommandRequest body, AgentService agents, CancellationToken ct) =>
            Results.Ok(new { messageId = await agents.CommandAsync(id, body.Name.TrimStart('/'), body.Arguments, body.Attachments, ct) }));

        api.MapPost("/sessions/{id}/abort", (string id, AgentService agents) =>
        {
            agents.Abort(id);
            return Results.NoContent();
        });

        api.MapDelete("/sessions/{id}/queued/{messageId}", (string id, string messageId, AgentService agents) =>
        {
            agents.RemoveQueuedMessage(id, messageId);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/compact", (string id, AgentService agents) =>
        {
            agents.Compact(id);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/continue", (string id, AgentService agents) =>
            Results.Ok(new { messageId = agents.Continue(id) }));

        api.MapPost("/sessions/{id}/retry", (string id, RevertRequest body, AgentService agents) =>
            Results.Ok(new { messageId = agents.Retry(id, body.MessageId) }));

        api.MapPost("/sessions/{id}/revert", (string id, RevertRequest body, AgentService agents) =>
        {
            var (removed, restored) = agents.Revert(id, body.MessageId);
            return Results.Ok(new { removed, restoredFiles = restored });
        });

        api.MapPost("/sessions/{id}/branch", (string id, RevertRequest body, AgentService agents, SofiaDb db) =>
        {
            var branch = agents.Branch(id, body.MessageId);
            return Results.Ok(WorkspaceService.WithMessages(branch, db.SessionMessages(branch.Id)));
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

        api.MapGet("/sessions/{id}/diff", async (string id, DiffService diff, CancellationToken ct) =>
            Results.Ok(await diff.ForSessionAsync(id, ct)));

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

        api.MapGet("/sessions/{id}/tasks", (string id, SessionService sessions) => Results.Ok(sessions.Tasks(id)));

        api.MapDelete("/sessions/{id}/tasks", (string id, SessionService sessions) =>
        {
            sessions.DeleteTasks(id);
            return Results.NoContent();
        });

        // Session-scoped (ephemeral) MCP servers supplied by an editor bridge (ACP).
        api.MapPost("/sessions/{id}/mcp", async (string id, SessionMcpRequest body, SessionService sessions, CancellationToken ct) =>
            Results.Ok(await sessions.AddSessionMcpAsync(id, body, ct)));

        api.MapDelete("/sessions/{id}/mcp", async (string id, SessionService sessions) =>
        {
            await sessions.RemoveSessionMcpAsync(id);
            return Results.NoContent();
        });

        api.MapGet("/sessions/{id}/export", (string id, SessionService sessions) => Results.Ok(sessions.Export(id)));

        api.MapPost("/sessions/import", (ImportRequest body, SessionService sessions) => Results.Ok(sessions.Import(body)));

        api.MapGet("/stats", (SessionService sessions) => Results.Ok(sessions.Stats()));
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
