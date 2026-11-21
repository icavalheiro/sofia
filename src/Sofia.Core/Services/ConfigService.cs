using Sofia.Contracts.Domain;
using Sofia.Core.Browser;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;

namespace Sofia.Core.Services;

/// <summary>Settings as the UI reads them, plus the sandbox capability of this machine.</summary>
public sealed record ConfigView(
    string DefaultModel,
    string ImageFallbackModel,
    string Shell,
    bool AutoCompact,
    double CompactThreshold,
    bool PruneToolResults,
    double PruneThreshold,
    int PruneKeepSteps,
    int PruneMinChars,
    bool Discovery,
    int MaxToolOutput,
    bool KeepScreenAwake,
    bool Knowledge,
    string SearchProvider,
    string SearxngUrl,
    bool Sandbox,
    bool SandboxNetwork,
    string ApplyPatch,
    bool ReadBeforeEdit,
    string DefaultBrowser,
    LspConfigView Lsp,
    SandboxStatusView SandboxStatus,
    string DataDir,
    string ConfigPath);

public sealed record LspConfigView(bool Enabled, bool AutoDiagnostics, Dictionary<string, LspServerSetting> Servers);

public sealed record SandboxStatusView(bool Available, string Backend, string? Reason);

/// <summary>
/// Reads and patches the server settings. The validation lives here (not in the endpoint) because both hosts must
/// reject exactly the same values.
/// </summary>
public sealed class ConfigService(SofiaDb db, SofiaOptions options, ProviderService providers, KeepAwakeService awake)
{
    public ConfigView View()
    {
        var config = db.GetConfig();
        var status = SandboxFactory.Create().Status;
        return new ConfigView(
            config.DefaultModel,
            config.ImageFallbackModel,
            config.Shell,
            config.AutoCompact,
            config.CompactThreshold,
            config.PruneToolResults,
            config.PruneThreshold,
            config.PruneKeepSteps,
            config.PruneMinChars,
            config.Discovery,
            config.MaxToolOutput,
            config.KeepScreenAwake,
            config.Knowledge,
            config.SearchProvider,
            config.SearxngUrl,
            config.Sandbox,
            config.SandboxNetwork,
            config.ApplyPatch,
            config.ReadBeforeEdit,
            config.DefaultBrowser,
            new LspConfigView(config.Lsp.Enabled, config.Lsp.AutoDiagnostics, config.Lsp.Servers),
            new SandboxStatusView(status.Available, status.Backend, status.Reason),
            options.DataDir,
            options.DbPath);
    }

    /// <summary>Applies a partial update, then keeps the awake lock and local discovery in step with it.</summary>
    public async Task<ConfigView> PatchAsync(ConfigPatch body, CancellationToken ct)
    {
        var config = db.GetConfig();
        var rediscover = false;
        if (body.DefaultModel is { } model) config.DefaultModel = model;
        if (body.ImageFallbackModel is { } fallback)
        {
            fallback = fallback.Trim();
            // An empty value switches the feature off; anything else has to be a real model that could see images.
            if (fallback.Length > 0)
            {
                var resolvedFallback = providers.Resolve(fallback);
                if (resolvedFallback is null || !resolvedFallback.Value.Model.CanSeeImages)
                    throw new ArgumentException("imageFallbackModel must be an available vision model ('providerId/modelId'), or '' to disable it.");
            }
            config.ImageFallbackModel = fallback;
        }
        if (body.Shell is { } shell) config.Shell = shell.Trim();
        if (body.AutoCompact is { } auto) config.AutoCompact = auto;
        if (body.CompactThreshold is { } threshold) config.CompactThreshold = Math.Clamp(threshold, 0.3, 0.98);
        if (body.PruneToolResults is { } pruneEnabled) config.PruneToolResults = pruneEnabled;
        // Pruning must trigger before compaction: clamp to a band that always stays below the compact threshold.
        var effectiveCompact = body.CompactThreshold is { } t ? Math.Clamp(t, 0.3, 0.98) : config.CompactThreshold;
        if (body.PruneThreshold is { } pruneThreshold) config.PruneThreshold = Math.Clamp(pruneThreshold, 0.2, effectiveCompact - 0.05);
        if (body.PruneKeepSteps is { } keepSteps) config.PruneKeepSteps = Math.Clamp(keepSteps, 1, 50);
        if (body.PruneMinChars is { } minChars) config.PruneMinChars = Math.Clamp(minChars, 200, 100_000);
        // A compact threshold that drops below the pruning one invalidates the band: keep the invariant.
        if (config.PruneThreshold > effectiveCompact - 0.05) config.PruneThreshold = Math.Max(0.2, effectiveCompact - 0.05);
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
        if (body.Sandbox is { } sandboxSetting) config.Sandbox = sandboxSetting;
        if (body.SandboxNetwork is { } sandboxNetwork) config.SandboxNetwork = sandboxNetwork;
        if (body.ApplyPatch is { } applyPatch)
        {
            if (applyPatch is not ("auto" or "always" or "never")) throw new ArgumentException("applyPatch must be 'auto', 'always' or 'never'.");
            config.ApplyPatch = applyPatch;
        }
        if (body.ReadBeforeEdit is { } readBeforeEdit) config.ReadBeforeEdit = readBeforeEdit;
        if (body.DefaultBrowser is { } defaultBrowser)
        {
            var browser = defaultBrowser.Trim().ToLowerInvariant();
            if (browser != BrowserCatalog.Auto && !BrowserCatalog.IsKnown(browser))
                throw new ArgumentException($"defaultBrowser must be 'auto' or one of: {string.Join(", ", BrowserCatalog.Ids)}.");
            config.DefaultBrowser = browser;
        }
        if (body.Lsp is { } lspPatch)
        {
            if (lspPatch.Enabled is { } lspEnabled) config.Lsp.Enabled = lspEnabled;
            if (lspPatch.AutoDiagnostics is { } lspAuto) config.Lsp.AutoDiagnostics = lspAuto;
            if (lspPatch.Servers is not null)
                foreach (var (id, server) in lspPatch.Servers)
                {
                    var setting = config.Lsp.Servers.TryGetValue(id, out var existing) && existing is not null
                        ? existing
                        : config.Lsp.Servers[id] = new LspServerSetting();
                    if (server.Enabled is { } sEnabled) setting.Enabled = sEnabled;
                    if (server.Command is { } command) setting.Command = string.IsNullOrWhiteSpace(command) ? null : command.Trim();
                }
        }
        db.SaveConfig(config);
        awake.Refresh();
        if (rediscover) await providers.RefreshAsync(discover: true, ct);
        return View();
    }
}
