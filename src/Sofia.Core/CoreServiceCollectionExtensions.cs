using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sofia.Core.Agent;
using Sofia.Core.Browser;
using Sofia.Core.Changes;
using Sofia.Core.Context;
using Sofia.Core.Data;
using Sofia.Core.Events;
using Sofia.Core.Infrastructure;
using Sofia.Core.Lsp;
using Sofia.Core.Mcp;
using Sofia.Core.Providers;
using Sofia.Core.Search;
using Sofia.Core.Security;
using Sofia.Core.Services;
using Sofia.Core.Vcs;

namespace Sofia.Core;

/// <summary>Host switches that are not user-editable settings.</summary>
public sealed class SofiaCoreOptions
{
    /// <summary>
    /// The host owns <c>~/.sofia</c>: taking the data-directory lock is mandatory and startup fails when another
    /// process holds it. The ASP.NET server leaves this off and only warns (two servers must keep working).
    /// </summary>
    public bool ExclusiveDataDir { get; set; }
}

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole engine: store, providers, agent loop, tools, MCP, LSP, browser, processes and the event
    /// pipeline. Every host (the ASP.NET server today, the in-process desktop head later) uses this one method, so
    /// the two cannot drift apart.
    /// </summary>
    public static IServiceCollection AddSofiaCore(this IServiceCollection services, SofiaOptions options, Action<SofiaCoreOptions>? configure = null)
    {
        var core = new SofiaCoreOptions();
        configure?.Invoke(core);
        services.AddSingleton(core);
        services.AddSingleton(options);
        services.TryAddSingleton<IBaseUrlProvider>(ConstantBaseUrl.None);

        // Providers that gate subscription traffic (OpenCode Go) reject a generic or missing user agent, so both LLM
        // clients identify the harness; a per-request override in a provider client wins over these defaults.
        // The names are part of the contract: tests replace handlers by name ("llm", "probe", "fetch").
        services.AddHttpClient("llm", c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderIdentity.UserAgent);
        });
        services.AddHttpClient("probe", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderIdentity.UserAgent);
        });
        services.AddHttpClient("fetch", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(40);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SofiaHarness/0.2)");
        });

        services.AddSingleton<SofiaDb>();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<ProviderService>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ChangeTracker>();
        services.AddSingleton<GitService>();
        services.AddSingleton<SkillService>();
        services.AddSingleton<AgentCatalog>();
        services.AddSingleton<KnowledgeService>();
        services.AddSingleton<KeepAwakeService>();
        services.AddSingleton<CommandService>();
        services.AddSingleton<ContextFiles>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<McpManager>();
        services.AddSingleton<LspManager>();
        services.AddSingleton<BrowserService>();
        services.AddSingleton<ProcessManager>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessManager>());
        services.AddSingleton<IEventHandler, ProcessCleanupHandler>();
        services.AddSingleton<IEventHandler, BrowserCleanupHandler>();
        services.AddHostedService<BrowserStartupService>();
        services.AddHostedService(sp => sp.GetRequiredService<McpManager>());
        services.AddHostedService(sp => sp.GetRequiredService<LspManager>());
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ModelGate>();
        services.AddSingleton<ImageAnalysisService>();
        services.AddSingleton<RunServices>();
        services.AddSingleton<AgentService>();

        // Endpoint behaviours that an in-process host needs too: the HTTP endpoints below only bind and translate.
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<DiffService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<AttachmentService>();
        services.AddSingleton<HostLauncher>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<ProviderAdminService>();
        services.AddSingleton<BrowserAdminService>();
        services.AddSingleton<McpAdminService>();
        services.AddSingleton<LspAdminService>();

        // Event-driven pieces: the dispatcher feeds handlers; a host that exposes SignalR adds the broadcaster.
        services.AddSingleton<ISessionSearch, SessionSearchIndex>();
        services.AddSingleton<IEventHandler, TitleHandler>();
        services.AddSingleton<IEventHandler, SearchIndexHandler>();
        services.AddHostedService<SearchIndexRebuildService>();
        services.AddHostedService<EventDispatcher>();
        services.AddHostedService<MaintenanceService>();
        // Pushes `config.changed` when commands, skills, agents or instruction files are edited on disk.
        services.AddHostedService<ConfigWatcher>();
        // Advisory data-directory ownership (exclusive when the host owns the store, e.g. the desktop head).
        services.AddHostedService<DataDirectoryOwnership>();

        return services;
    }
}
