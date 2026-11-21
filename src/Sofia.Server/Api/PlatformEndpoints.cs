using Microsoft.AspNetCore.Mvc;
using Sofia.Core.Agent;
using Sofia.Core.Context;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;
using Sofia.Core.Security;
using Sofia.Core.Services;
using Sofia.Core.Vcs;
using Sofia.Server.Security;

namespace Sofia.Server.Api;

/// <summary>
/// HTTP binding for the platform surface (models, providers, MCP, LSP, VCS, attachments, config, pairing). Every rule
/// behind a route — validation, cascades, file listing — lives in <c>Sofia.Core.Services</c>, so the in-process host
/// behaves identically.
/// </summary>
public static class PlatformEndpoints
{
    public static void MapPlatformApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // --- models and providers ----------------------------------------------

        api.MapGet("/models", (ProviderAdminService providers) => Results.Ok(providers.Models()));
        api.MapPost("/models/enabled", (SetModelEnabledRequest body, ProviderAdminService providers) =>
        {
            var ids = IdsOf(body);
            providers.SetModelsEnabled(ids, body.Enabled);
            return Results.NoContent();
        });
        api.MapPost("/models/single-client", (SetModelEnabledRequest body, ProviderAdminService providers) =>
        {
            var ids = IdsOf(body);
            providers.SetSingleClient(ids, body.Enabled);
            return Results.NoContent();
        });
        api.MapGet("/providers", (ProviderAdminService providers) => Results.Ok(providers.All()));
        api.MapGet("/providers/presets", () => Results.Ok(ProviderAdminService.Presets()));

        // GitHub Copilot signs in with the OAuth device flow instead of a pasted key.
        api.MapPost("/providers/github-copilot/login", async (ProviderAdminService providers, CancellationToken ct) =>
            Results.Ok(await providers.CopilotLoginAsync(ct)));

        api.MapPost("/providers/github-copilot/login/poll", async (CopilotPollRequest body, ProviderAdminService providers, CancellationToken ct) =>
            Results.Ok(await providers.CopilotPollAsync(body.DeviceCode, ct)));

        api.MapPost("/providers", async (CreateProviderRequest body, ProviderAdminService providers, CancellationToken ct) =>
            Results.Ok(await providers.CreateAsync(body, ct)));

        api.MapPatch("/providers/{id}", async (string id, UpdateProviderRequest body, ProviderAdminService providers, CancellationToken ct) =>
            Results.Ok(await providers.UpdateAsync(id, body, ct)));

        api.MapDelete("/providers/{id}", async (string id, ProviderAdminService providers, CancellationToken ct) =>
        {
            await providers.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        api.MapPost("/providers/refresh", async (ProviderAdminService providers, ModelCatalog catalog, CancellationToken ct) =>
            Results.Ok(await providers.RefreshAsync(catalog, ct)));

        api.MapPost("/providers/probe", async (ProbeRequest body, ProviderAdminService providers, CancellationToken ct) =>
            Results.Ok(await providers.ProbeAsync(body, ct)));

        // --- MCP ------------------------------------------------------------------

        api.MapGet("/mcp", (McpAdminService mcp) => Results.Ok(mcp.List()));
        api.MapPost("/mcp", async (CreateMcpRequest body, McpAdminService mcp, CancellationToken ct) =>
            Results.Ok(await mcp.AddAsync(body, ct)));
        api.MapPost("/mcp/{id}/connect", async (string id, McpAdminService mcp, CancellationToken ct) =>
            Results.Ok(await mcp.ConnectAsync(id, ct)));
        api.MapPost("/mcp/{id}/disconnect", async (string id, McpAdminService mcp) =>
            Results.Ok(await mcp.DisconnectAsync(id)));
        api.MapDelete("/mcp/{id}", async (string id, McpAdminService mcp) =>
        {
            await mcp.RemoveAsync(id);
            return Results.NoContent();
        });

        // --- language servers (LSP) ----------------------------------------------------

        api.MapGet("/lsp", (LspAdminService lsp) => Results.Ok(lsp.List()));
        api.MapPost("/lsp/{id}/restart", async (string id, LspAdminService lsp) => Results.Ok(await lsp.RestartAsync(id)));
        api.MapPost("/lsp/{id}/install", async (string id, LspAdminService lsp, CancellationToken ct) => Results.Ok(await lsp.InstallAsync(id, ct)));

        // --- agents, commands, skills ------------------------------------------------

        api.MapGet("/agents", (string? directory, AgentCatalog agents) => Results.Ok(agents.List(directory)
            .OrderBy(a => a.Source == "builtin" ? 0 : 1).ThenBy(a => a.Source == "builtin" ? "" : a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(a => new { a.Id, a.Name, a.Description, a.Mode, a.Source, a.Path })));
        api.MapGet("/commands", (string? directory, CommandService commands) => Results.Ok(commands.List(directory)));
        api.MapGet("/skills", (string? directory, SkillService skills) => Results.Ok(skills.List(directory)));
        api.MapGet("/files", (string? directory, FileService files) => Results.Ok(files.List(directory)));

        // --- VCS ------------------------------------------------------------------------

        api.MapGet("/vcs", async (string? directory, GitService git, CancellationToken ct) =>
            Results.Ok(string.IsNullOrEmpty(directory) ? new VcsInfo(false, "", []) : await git.InfoAsync(directory, ct)));
        api.MapGet("/vcs/diff", async (string? directory, GitService git, CancellationToken ct) =>
            Results.Ok(string.IsNullOrEmpty(directory) ? [] : await git.DiffAsync(directory, ct)));

        api.MapGet("/worktrees", async (string? directory, GitService git, CancellationToken ct) =>
        {
            var info = string.IsNullOrEmpty(directory) ? null : await git.WorktreesAsync(directory, ct);
            return Results.Ok(new { root = info?.Root ?? "", worktrees = info?.Worktrees ?? [] });
        });
        api.MapPost("/worktrees", async (CreateWorktreeRequest body, GitService git, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await git.CreateWorktreeAsync(body.Directory, body.Name, ct));
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message);
            }
        });
        api.MapDelete("/worktrees", async ([FromBody] DeleteWorktreeRequest body, GitService git, CancellationToken ct) =>
        {
            try
            {
                await git.RemoveWorktreeAsync(body.Directory, body.Worktree, body.Force ?? false, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message);
            }
        });

        // --- attachments -------------------------------------------------------------

        api.MapPost("/attachments", async (IFormFile file, AttachmentService attachments, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return Results.Ok(await attachments.SaveAsync(stream, file.FileName, file.ContentType, file.Length, ct));
        }).DisableAntiforgery();

        api.MapGet("/attachments/{id}", (string id, AttachmentService attachments) =>
        {
            var attachment = attachments.Get(id);
            if (!File.Exists(attachment.StoragePath)) throw new NotFoundException("Attachment file is missing.");
            return Results.File(attachment.StoragePath, attachment.Mime, attachment.Name, enableRangeProcessing: true);
        });

        // --- open in OS ----------------------------------------------------------------

        api.MapPost("/open", (OpenRequest body, HostLauncher launcher, HttpContext http) =>
        {
            if (!AccessMiddleware.IsLoopback(AccessMiddleware.ClientAddress(http))) return Results.Json(new { error = "Only available locally." }, statusCode: 403);
            launcher.Open(body.Path, body.App);
            return Results.NoContent();
        });

        // --- config ----------------------------------------------------------------------

        api.MapGet("/config", (ConfigService config) => Results.Ok(config.View()));
        api.MapPatch("/config", async (ConfigPatch body, ConfigService config, CancellationToken ct) =>
            Results.Ok(await config.PatchAsync(body, ct)));

        // --- knowledge notes ---------------------------------------------------------------

        api.MapGet("/knowledge", (string? workspaceId, string? agent, KnowledgeService knowledge) =>
            Results.Ok(knowledge.List(workspaceId, agent)));

        api.MapDelete("/knowledge/{id}", (string id, KnowledgeService knowledge) =>
            knowledge.Delete(id) ? Results.NoContent() : throw new NotFoundException("Knowledge note not found."));

        // --- pairing ----------------------------------------------------------------------

        api.MapPost("/pair", (HttpContext http, PairingService pairing) =>
        {
            if (!AccessMiddleware.IsLoopback(AccessMiddleware.ClientAddress(http))) return Results.Json(new { error = "Pairing codes can only be created locally." }, statusCode: 403);
            var (code, expiresIn) = pairing.CreateCode();
            return Results.Ok(new { code, expiresIn });
        });

        api.MapPost("/pair/connect", (PairConnectRequest body, PairingService pairing) =>
        {
            var token = pairing.Redeem(body.Code ?? "");
            return token is null
                ? Results.Json(new { error = "Invalid or expired pairing code." }, statusCode: 401)
                : Results.Ok(new { token });
        });
    }

    /// <summary>The id and/or ids of a bulk model switch, validated once for both model endpoints.</summary>
    private static List<string> IdsOf(SetModelEnabledRequest body)
    {
        var ids = (body.Ids ?? []).Append(body.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0) throw new ArgumentException("A model id is required.");
        return ids;
    }
}
