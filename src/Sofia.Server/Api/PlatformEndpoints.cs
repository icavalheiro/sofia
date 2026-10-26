using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Sofia.Server.Agent;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;
using Sofia.Server.Providers;
using Sofia.Server.Security;
using Sofia.Server.Vcs;

namespace Sofia.Server.Api;

public sealed record CreateProviderRequest(string Name, string? Type, string BaseUrl, string? ApiKey, List<string>? Models, string? PresetId);
public sealed record UpdateProviderRequest(bool? Enabled, string? ApiKey, string? BaseUrl, string? Name, bool? PreserveThinking = null);
public sealed record ProbeRequest(string? Type, string BaseUrl, string? ApiKey);
public sealed record CopilotPollRequest(string DeviceCode);
public sealed record SetModelEnabledRequest(string? Id, List<string>? Ids, bool Enabled);
public sealed record CreateMcpRequest(string Name, string? Transport, string? Command, string? Url, string? Token, Dictionary<string, string>? Env);
public sealed record CreateWorktreeRequest(string Directory, string Name);
public sealed record DeleteWorktreeRequest(string Directory, string Worktree, bool? Force);
public sealed record OpenRequest(string Path, string? App);
public sealed record PairConnectRequest(string Code);
public sealed record ConfigPatch(
    string? DefaultModel,
    string? Shell,
    bool? AutoCompact,
    double? CompactThreshold,
    bool? Discovery,
    int? MaxToolOutput,
    bool? KeepScreenAwake = null,
    bool? Knowledge = null,
    string? SearchProvider = null,
    string? SearxngUrl = null);

public static class PlatformEndpoints
{
    public static void MapPlatformApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // --- models and providers ----------------------------------------------

        api.MapGet("/models", (ProviderService providers) => Results.Ok(providers.Models()));
        api.MapPost("/models/enabled", (SetModelEnabledRequest body, ProviderService providers) =>
        {
            var ids = (body.Ids ?? []).Append(body.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList();
            if (ids.Count == 0) throw new ArgumentException("A model id is required.");
            providers.SetModelsEnabled(ids, body.Enabled);
            return Results.NoContent();
        });
        api.MapGet("/providers", (ProviderService providers) => Results.Ok(providers.Providers()));
        api.MapGet("/providers/presets", () => Results.Ok(ProviderService.Presets.Select(p => new { p.Id, p.Name, p.Type, p.BaseUrl, p.EnvKey, p.Auth, p.Note })));

        // GitHub Copilot signs in with the OAuth device flow instead of a pasted key.
        api.MapPost("/providers/github-copilot/login", async (ProviderService providers, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await providers.Copilot.StartDeviceFlowAsync(ct));
            }
            catch (ProviderException ex)
            {
                throw new ArgumentException(ex.Message);
            }
        });

        api.MapPost("/providers/github-copilot/login/poll", async (CopilotPollRequest body, SofiaDb db, ProviderService providers, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.DeviceCode)) throw new ArgumentException("deviceCode is required.");
            DevicePoll poll;
            try
            {
                poll = await providers.Copilot.PollDeviceFlowAsync(body.DeviceCode, ct);
            }
            catch (ProviderException ex)
            {
                throw new ArgumentException(ex.Message);
            }
            if (poll.Status != "success") return Results.Ok(new { status = poll.Status, interval = poll.Interval, provider = (ProviderView?)null });

            var preset = ProviderService.Presets.First(p => p.Id == "github-copilot");
            var config = db.Providers.FindById(preset.Id) ?? new ProviderConfig
            {
                Id = preset.Id,
                Name = preset.Name,
                Type = preset.Type,
                Kind = "cloud",
                BaseUrl = preset.BaseUrl,
                ApiKeyEnv = preset.EnvKey,
                PresetId = preset.Id,
                Flavor = preset.Flavor,
                Custom = true,
            };
            config.ApiKey = poll.Token;
            config.Enabled = true;
            db.Providers.Upsert(config);
            await providers.RefreshAsync(discover: false, ct);
            return Results.Ok(new { status = "success", interval = (int?)null, provider = providers.Provider(preset.Id) });
        });

        api.MapPost("/providers", async (CreateProviderRequest body, SofiaDb db, ProviderService providers, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.BaseUrl)) throw new ArgumentException("Base URL is required.");
            var preset = ProviderService.Presets.FirstOrDefault(p => p.Id == body.PresetId);
            var type = body.Type == "anthropic" ? "anthropic" : preset?.Type ?? "openai";
            var id = preset is not null && db.Providers.FindById(preset.Id) is null ? preset.Id : Ids.New("prv");
            var config = new ProviderConfig
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(body.Name) ? preset?.Name ?? body.BaseUrl : body.Name.Trim(),
                Type = type,
                Kind = ProviderService.IsLoopback(body.BaseUrl) ? "local" : "cloud",
                BaseUrl = body.BaseUrl.Trim().TrimEnd('/'),
                ApiKey = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey.Trim(),
                Custom = true,
                PresetId = preset?.Id,
                Flavor = preset?.Flavor ?? ProviderService.GuessFlavor(body.BaseUrl, type),
                Models = body.Models?.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList() ?? [],
            };
            db.Providers.Insert(config);
            await providers.RefreshAsync(discover: false, ct);
            return Results.Ok(providers.Provider(config.Id));
        });

        api.MapPatch("/providers/{id}", async (string id, UpdateProviderRequest body, SofiaDb db, ProviderService providers, CancellationToken ct) =>
        {
            var config = db.Providers.FindById(id) ?? throw new NotFoundException("Provider not found.");
            if (body.Enabled is { } enabled) config.Enabled = enabled;
            if (body.ApiKey is { } key) config.ApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            if (!string.IsNullOrWhiteSpace(body.BaseUrl)) config.BaseUrl = body.BaseUrl.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(body.Name)) config.Name = body.Name.Trim();
            if (body.PreserveThinking is { } preserve) config.PreserveThinking = preserve;
            db.Providers.Update(config);
            await providers.RefreshAsync(discover: false, ct);
            return Results.Ok(providers.Provider(id));
        });

        api.MapDelete("/providers/{id}", async (string id, SofiaDb db, ProviderService providers, CancellationToken ct) =>
        {
            var config = db.Providers.FindById(id) ?? throw new NotFoundException("Provider not found.");
            if (config.Discovered)
            {
                // Deleting a discovered runtime would bring it right back: disable it instead.
                config.Enabled = false;
                db.Providers.Update(config);
            }
            else
            {
                db.Providers.Delete(id);
            }
            await providers.RefreshAsync(discover: false, ct);
            return Results.NoContent();
        });

        api.MapPost("/providers/refresh", async (ProviderService providers, SofiaDb db, CancellationToken ct) =>
        {
            await providers.RefreshAsync(discover: true, ct);
            return Results.Ok(providers.Providers());
        });

        api.MapPost("/providers/probe", async (ProbeRequest body, ProviderService providers, CancellationToken ct) =>
        {
            try
            {
                var models = await providers.ProbeAsync(body.Type == "anthropic" ? "anthropic" : "openai", body.BaseUrl.Trim(), body.ApiKey, ct);
                return Results.Ok(new { models });
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException)
            {
                throw new ArgumentException($"Could not list models: {ex.Message}");
            }
        });

        // --- MCP ------------------------------------------------------------------

        api.MapGet("/mcp", (McpManager mcp) => Results.Ok(mcp.List()));

        api.MapPost("/mcp", async (CreateMcpRequest body, McpManager mcp, CancellationToken ct) =>
        {
            var transport = body.Transport == "web" ? "web" : "command";
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Name is required.");
            if (transport == "command" && string.IsNullOrWhiteSpace(body.Command)) throw new ArgumentException("Command is required.");
            if (transport == "web" && !Uri.TryCreate(body.Url, UriKind.Absolute, out _)) throw new ArgumentException("A valid URL is required.");
            var view = await mcp.AddAsync(new McpServerConfig
            {
                Name = body.Name.Trim(),
                Transport = transport,
                Command = body.Command?.Trim() ?? "",
                Url = body.Url?.Trim() ?? "",
                Token = string.IsNullOrWhiteSpace(body.Token) ? null : body.Token.Trim(),
                Env = body.Env ?? [],
            }, ct);
            return Results.Ok(view);
        });

        api.MapPost("/mcp/{id}/connect", async (string id, McpManager mcp, CancellationToken ct) =>
            Results.Ok(await mcp.ConnectAsync(id, ct) ?? throw new NotFoundException("MCP server not found.")));
        api.MapPost("/mcp/{id}/disconnect", async (string id, McpManager mcp) =>
            Results.Ok(await mcp.DisconnectAsync(id) ?? throw new NotFoundException("MCP server not found.")));
        api.MapDelete("/mcp/{id}", async (string id, McpManager mcp) =>
        {
            await mcp.RemoveAsync(id);
            return Results.NoContent();
        });

        // --- agents, commands, skills ------------------------------------------------

        api.MapGet("/agents", (string? directory, AgentCatalog agents) => Results.Ok(agents.List(directory)
            .OrderBy(a => a.Source == "builtin" ? 0 : 1).ThenBy(a => a.Source == "builtin" ? "" : a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(a => new { a.Id, a.Name, a.Description, a.Mode, a.Source, a.Path })));
        api.MapGet("/commands", (string? directory, CommandService commands) => Results.Ok(commands.List(directory)));
        api.MapGet("/skills", (string? directory, SkillService skills) => Results.Ok(skills.List(directory)));

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

        api.MapPost("/attachments", async (IFormFile file, SofiaDb db, SofiaOptions options, CancellationToken ct) =>
        {
            var id = Ids.New("att");
            var ext = Path.GetExtension(file.FileName);
            var storage = Path.Combine(options.AttachmentsDir, id + ext);
            await using (var stream = File.Create(storage)) await file.CopyToAsync(stream, ct);
            var mime = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            var attachment = new Attachment
            {
                Id = id,
                Name = Path.GetFileName(file.FileName),
                Mime = mime,
                Kind = mime.StartsWith("image/") ? "image" : mime.StartsWith("video/") ? "video" : mime.StartsWith("audio/") ? "audio" : "file",
                Bytes = file.Length,
                Size = Text.HumanSize(file.Length),
                Url = $"/api/attachments/{id}",
                StoragePath = storage,
            };
            db.Attachments.Insert(attachment);
            return Results.Ok(attachment);
        }).DisableAntiforgery();

        api.MapGet("/attachments/{id}", (string id, SofiaDb db) =>
        {
            var attachment = db.Attachments.FindById(id) ?? throw new NotFoundException("Attachment not found.");
            if (!File.Exists(attachment.StoragePath)) throw new NotFoundException("Attachment file is missing.");
            return Results.File(attachment.StoragePath, attachment.Mime, attachment.Name, enableRangeProcessing: true);
        });

        // --- open in OS ----------------------------------------------------------------

        api.MapPost("/open", (OpenRequest body, HttpContext http) =>
        {
            if (!AccessMiddleware.IsLoopback(AccessMiddleware.ClientAddress(http))) return Results.Json(new { error = "Only available locally." }, statusCode: 403);
            if (!Directory.Exists(body.Path) && !File.Exists(body.Path)) throw new ArgumentException($"Path not found: {body.Path}");
            if (body.App == "terminal")
            {
                NativeTerminal.Open(body.Path);
                return Results.NoContent();
            }
            var psi = body.App == "vscode"
                ? OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", ["/c", "code", body.Path])
                    : new ProcessStartInfo("code", [body.Path])
                : OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", [body.Path])
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [body.Path])
                : new ProcessStartInfo("xdg-open", [body.Path]);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi)?.Dispose();
            return Results.NoContent();
        });

        // --- config ----------------------------------------------------------------------

        api.MapGet("/config", (SofiaDb db, SofiaOptions options) => Results.Ok(ConfigView(db.GetConfig(), options)));

        api.MapPatch("/config", async (ConfigPatch body, SofiaDb db, SofiaOptions options, ProviderService providers, KeepAwakeService awake, CancellationToken ct) =>
        {
            var config = db.GetConfig();
            var rediscover = false;
            if (body.DefaultModel is { } model) config.DefaultModel = model;
            if (body.Shell is { } shell) config.Shell = shell.Trim();
            if (body.AutoCompact is { } auto) config.AutoCompact = auto;
            if (body.CompactThreshold is { } threshold) config.CompactThreshold = Math.Clamp(threshold, 0.3, 0.98);
            if (body.Discovery is { } discovery)
            {
                rediscover = discovery && !config.Discovery;
                config.Discovery = discovery;
            }
            if (body.MaxToolOutput is { } max) config.MaxToolOutput = Math.Clamp(max, 2_000, 1_000_000);
            if (body.KeepScreenAwake is { } awakeSetting) config.KeepScreenAwake = awakeSetting;
            if (body.Knowledge is { } knowledge) config.Knowledge = knowledge;
            if (body.SearchProvider is { } searchProvider)
            {
                if (searchProvider is not ("duckduckgo" or "searxng")) throw new ArgumentException("searchProvider must be 'duckduckgo' or 'searxng'.");
                config.SearchProvider = searchProvider;
            }
            if (body.SearxngUrl is { } searxng)
            {
                searxng = searxng.Trim();
                if (searxng.Length > 0 && !(Uri.TryCreate(searxng, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                    throw new ArgumentException("searxngUrl must be an absolute http(s) URL.");
                config.SearxngUrl = searxng;
            }
            db.SaveConfig(config);
            awake.Refresh();
            if (rediscover) await providers.RefreshAsync(discover: true, ct);
            return Results.Ok(ConfigView(config, options));
        });

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

    private static object ConfigView(ServerConfig c, SofiaOptions options) => new
    {
        c.DefaultModel,
        c.Shell,
        c.AutoCompact,
        c.CompactThreshold,
        c.Discovery,
        c.MaxToolOutput,
        c.KeepScreenAwake,
        c.Knowledge,
        c.SearchProvider,
        c.SearxngUrl,
        dataDir = options.DataDir,
        configPath = options.DbPath,
    };
}
