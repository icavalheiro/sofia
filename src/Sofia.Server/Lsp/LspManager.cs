using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;

namespace Sofia.Server.Lsp;

public static class LspEvents
{
    /// <summary>Event type published on the bus when a language server's status changes.</summary>
    public const string Status = "lsp.status";
}

/// <summary>Status of one built-in language server, aggregated over all workspaces.</summary>
public sealed class LspServerView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Effective command line (user override or built-in).</summary>
    public string Command { get; set; } = "";
    /// <summary>Resolved executable path, or null when not found on PATH.</summary>
    public string? Executable { get; set; }
    public bool Enabled { get; set; }
    /// <summary>'missing' | 'disabled' | 'stopped' | 'starting' | 'running' | 'error'.</summary>
    public string Status { get; set; } = "stopped";
    public string? Error { get; set; }
    public string InstallCommand { get; set; } = "";
    public string InstallHint { get; set; } = "";
    public List<string> Extensions { get; set; } = [];
    /// <summary>Workspace roots that currently have a live client for this server.</summary>
    public List<string> RunningIn { get; set; } = [];
}

/// <summary>
/// Owns one LSP client per (workspace root, server id), started lazily on first use and shut down after
/// ~10 idle minutes. Publishes <c>lsp.status</c> events so the UI updates live; kills every process tree
/// on shutdown. Crashes flip the status to 'error' and the next use restarts (with a short backoff).
/// </summary>
public sealed class LspManager(SofiaDb db, IEventBus bus, ILoggerFactory loggers) : IHostedService, IAsyncDisposable
{
    /// <summary>Idle time before a per-workspace client is shut down (tests may shorten this).</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    private const int CrashBackoffBaseMs = 2_000;
    private const int CrashBackoffMaxMs = 30_000;
    /// <summary>How long to wait for a fresh publishDiagnostics after syncing (tests may shorten this).</summary>
    internal TimeSpan DiagnosticsSettle { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>How often the idle sweep runs (tests may shorten this).</summary>
    internal TimeSpan IdleTick { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Overall time budget for auto diagnostics after a write (covers cold starts + settle).</summary>
    internal TimeSpan AutoDiagnosticsBudget { get; set; } = TimeSpan.FromSeconds(25);
    private const int MaxAutoDiagnostics = 20;
    private const int AutoDiagnosticsMaxChars = 2048;

    private sealed class Entry
    {
        public required LspServerDef Def;
        public required string Root;
        public LspClient? Client;
        public string Status = "stopped";
        public string? Error;
        public DateTime LastUseUtc = DateTime.UtcNow;
        public int FailCount;
        public DateTime NextAllowedUtc;
    }

    private readonly ILogger<LspManager> _log = loggers.CreateLogger<LspManager>();
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    /// <summary>Latest publishDiagnostics per document uri, with arrival time (server versions are not trusted).</summary>
    private readonly ConcurrentDictionary<string, (JsonArray Diags, DateTime AtUtc)> _diagnostics = new(StringComparer.Ordinal);
    private CancellationTokenSource? _idleCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        _ = Task.Run(() => IdleLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Test hook: attach an externally created client (e.g. over in-memory streams) to a workspace/server.</summary>
    internal void AttachClientForTest(string root, string serverId, LspClient client)
    {
        var def = LspServerCatalog.Find(serverId) ?? throw new ArgumentException($"Unknown language server '{serverId}'.");
        var entry = _entries.GetOrAdd(Key(root, serverId), _ => new Entry { Def = def, Root = PathUtil.Normalize(root) });
        if (entry.Client is not null && !ReferenceEquals(entry.Client, client))
            _ = entry.Client.DisposeAsync();
        entry.Client = client;
        entry.Status = "running";
        entry.Error = null;
        entry.LastUseUtc = DateTime.UtcNow;
        client.Exited += code => OnClientExited(entry, code);
        client.Diagnostics += (uri, diags) => _diagnostics[uri] = (diags, DateTime.UtcNow);
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            _idleCts?.Cancel();
        }
        catch
        {
            // Already disposed.
        }
        foreach (var entry in _entries.Values)
            await ShutdownEntryAsync(entry, publish: false);
        _entries.Clear();
    }

    // --- status -------------------------------------------------------------------

    /// <summary>One view per built-in server, aggregated over every workspace.</summary>
    public List<LspServerView> ListViews()
    {
        var config = db.GetConfig().Lsp;
        return LspServerCatalog.All.Select(def =>
        {
            var entries = _entries.Values.Where(e => e.Def.Id == def.Id).ToList();
            var runningIn = entries.Where(e => e.Status == "running").Select(e => e.Root).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            string status;
            string? error = null;
            if (!LspServerCatalog.IsEnabled(def, config))
                status = "disabled";
            else if (entries.Any(e => e.Status == "running"))
                status = "running";
            else if (entries.Any(e => e.Status == "error"))
            {
                // A recent crash is more informative than the static executable check.
                status = "error";
                error = entries.Where(e => e.Error is not null).Select(e => e.Error!).First();
            }
            else if (entries.Any(e => e.Status == "starting"))
                status = "starting";
            else if (LspServerCatalog.ResolveCommand(LspServerCatalog.CommandFor(def, config)) is null)
                status = "missing";
            else
                status = "stopped";

            return new LspServerView
            {
                Id = def.Id,
                Name = def.Name,
                Command = LspServerCatalog.Display(def, config),
                Executable = LspServerCatalog.ResolveCommand(LspServerCatalog.CommandFor(def, config)),
                Enabled = LspServerCatalog.IsEnabled(def, config),
                Status = status,
                Error = error,
                InstallCommand = def.InstallCommand,
                InstallHint = def.InstallHint,
                Extensions = [.. def.Extensions],
                RunningIn = runningIn,
            };
        }).ToList();
    }

    public LspServerView? View(string serverId) => ListViews().FirstOrDefault(v => v.Id == serverId);

    private void Publish(LspServerView view) => bus.Publish(LspEvents.Status, view);

    private void PublishAll()
    {
        foreach (var view in ListViews()) Publish(view);
    }

    // --- client lifecycle -----------------------------------------------------------

    /// <summary>
    /// The live client for (root, serverId), started lazily. Returns null when the language is disabled,
    /// the executable is missing or a recent crash is still in backoff — callers degrade gracefully.
    /// </summary>
    public async Task<LspClient?> GetClientAsync(string root, string serverId, CancellationToken ct = default)
    {
        var def = LspServerCatalog.Find(serverId);
        if (def is null) return null;
        var config = db.GetConfig().Lsp;
        if (!LspServerCatalog.IsEnabled(def, config)) return null;

        var key = Key(root, serverId);
        var entry = _entries.GetOrAdd(key, _ => new Entry { Def = def, Root = PathUtil.Normalize(root) });
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (entry.Client is { } alive && alive.IsAlive)
            {
                entry.LastUseUtc = DateTime.UtcNow;
                return alive;
            }

            var resolved = LspServerCatalog.ResolveCommand(LspServerCatalog.CommandFor(def, config));
            if (resolved is null)
            {
                SetStatus(entry, "missing", $"'{def.Id}' executable not found on PATH. Install it with: {def.InstallCommand}");
                return null;
            }

            // Crash backoff: after repeated failures wait before trying again.
            var now = DateTime.UtcNow;
            if (entry.NextAllowedUtc > now)
            {
                SetStatus(entry, "error", $"'{def.Id}' crashed recently; retrying in {(int)Math.Ceiling((entry.NextAllowedUtc - now).TotalSeconds)}s");
                return null;
            }

            await StartEntryAsync(entry, resolved, config);
            return entry.Client;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StartEntryAsync(Entry entry, string resolvedCommand, LspConfig config)
    {
        SetStatus(entry, "starting", null);
        await ShutdownEntryAsync(entry, publish: false); // drop a dead previous client first

        LspClient client;
        try
        {
            // The first token is already resolved (absolute path, PATHEXT-aware); the rest are its arguments.
            var argv = McpManager.SplitCommandLine(LspServerCatalog.CommandFor(entry.Def, config));
            argv[0] = resolvedCommand;
            client = LspClient.Start(argv, entry.Root, _log);
        }
        catch (Exception ex)
        {
            FailEntry(entry, $"could not start: {Text.FirstLine(ex.Message, 200)}");
            return;
        }

        entry.Client = client;
        entry.LastUseUtc = DateTime.UtcNow;
        client.Exited += code => OnClientExited(entry, code);
        client.Diagnostics += (uri, diags) => _diagnostics[uri] = (diags, DateTime.UtcNow);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            await client.InitializeAsync(entry.Root, cts.Token);
            entry.FailCount = 0;
            entry.NextAllowedUtc = DateTime.MinValue;
            SetStatus(entry, "running", null);
        }
        catch (Exception ex)
        {
            FailEntry(entry, $"initialize failed: {Text.FirstLine(ex.Message, 200)}");
        }
    }

    private void OnClientExited(Entry entry, int? code)
    {
        if (entry.Client is not null && !entry.Client.IsAlive)
        {
            // A clean shutdown sets the status itself; only an unexpected exit is a crash.
            if (entry.Status is "running" or "starting")
                FailEntry(entry, $"the server process exited unexpectedly (code {code?.ToString() ?? "unknown"})");
        }
    }

    private void FailEntry(Entry entry, string error)
    {
        entry.FailCount++;
        var backoff = Math.Min(CrashBackoffMaxMs, CrashBackoffBaseMs * (1 << Math.Min(entry.FailCount, 4)));
        entry.NextAllowedUtc = DateTime.UtcNow.AddMilliseconds(backoff);
        SetStatus(entry, "error", error);
    }

    private void SetStatus(Entry entry, string status, string? error)
    {
        if (entry.Status == status && entry.Error == error) return;
        entry.Status = status;
        entry.Error = error;
        Publish(View(entry.Def.Id)!);
    }

    /// <summary>Restarts a server in every workspace (drops the live clients; they come back on next use).</summary>
    public async Task RestartAsync(string serverId)
    {
        foreach (var key in _entries.Keys.Where(k => k.EndsWith("|" + serverId, StringComparison.Ordinal)).ToList())
            if (_entries.TryRemove(key, out var entry))
                await ShutdownEntryAsync(entry, publish: false);
        PublishAll();
    }

    /// <summary>Runs the install command of a built-in server through the user's shell.</summary>
    public async Task<(bool Ok, string Output)> InstallAsync(string serverId, CancellationToken ct)
    {
        var def = LspServerCatalog.Find(serverId) ?? throw new ArgumentException($"Unknown language server '{serverId}'.");
        var shell = ProcessRunner.ResolveShell(db.GetConfig().Shell);
        _log.LogInformation("Installing LSP server {Id} with: {Command}", def.Id, def.InstallCommand);
        var result = await ProcessRunner.RunShellAsync(shell, def.InstallCommand, null, TimeSpan.FromMinutes(5), onOutput: null, maxChars: 20_000, ct);
        if (result.Cancelled) throw new OperationCanceledException();
        return (result.ExitCode == 0, result.Output.Length > 0 ? result.Output : "(no output)");
    }

    // --- documents and diagnostics ------------------------------------------------------

    /// <summary>
    /// Ensures the given files are synced to their servers (full didOpen/didChange) and waits up to
    /// ~1.5s for fresh diagnostics. Returns only errors of the touched files, capped, or null when there
    /// is nothing to report. Never fails: a missing server, timeout or crash yields null. An overall
    /// budget bounds cold server starts, so the agent loop is never held up by LSP.
    /// </summary>
    public async Task<string?> DiagnosticsAfterWriteAsync(string root, IEnumerable<string> paths, CancellationToken ct)
    {
        var config = db.GetConfig().Lsp;
        if (!config.Enabled || !config.AutoDiagnostics) return null;

        var targets = new List<(string ServerId, string Path)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var def = LspServerCatalog.ForPath(path);
            if (def is null || !LspServerCatalog.IsEnabled(def, config)) continue;
            targets.Add((def.Id, PathUtil.Normalize(path)));
        }
        if (targets.Count == 0) return null;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(AutoDiagnosticsBudget);
        try
        {
            var fresh = await SyncAndCollectAsync(root, targets, budget.Token);
            if (fresh.Count == 0) return null;

            var lines = new List<string>();
            foreach (var (uri, diags) in fresh)
                foreach (var diag in diags)
                    if (diag is JsonObject o && SeverityOf(o) == 1) // errors only
                        lines.Add(FormatDiagnostic(PathFromUri(uri), o));

            if (lines.Count == 0) return null;
            var sb = new System.Text.StringBuilder("LSP errors:");
            foreach (var l in lines.Take(MaxAutoDiagnostics)) sb.Append('\n').Append(l);
            var output = sb.ToString();
            return output.Length > AutoDiagnosticsMaxChars ? output[..AutoDiagnosticsMaxChars] + "\n… (truncated)" : output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // the server was still starting up — do not block the write
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Auto diagnostics failed");
            return null;
        }
    }

    /// <summary>
    /// Syncs one file and waits for fresh diagnostics; returns every severity formatted, or null when the
    /// server has not published anything yet (still loading) or is unavailable.
    /// </summary>
    public async Task<string?> DiagnosticsForFileAsync(string root, string path, CancellationToken ct = default)
    {
        var config = db.GetConfig().Lsp;
        if (!config.Enabled) return null;
        var def = LspServerCatalog.ForPath(path);
        if (def is null || !LspServerCatalog.IsEnabled(def, config)) return null;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(AutoDiagnosticsBudget);
        try
        {
            var fresh = await SyncAndCollectAsync(root, [(def.Id, PathUtil.Normalize(path))], budget.Token);
            if (fresh.Count == 0) return null;
            var lines = new List<string>();
            foreach (var (uri, diags) in fresh)
                foreach (var diag in diags)
                    if (diag is JsonObject o) lines.Add(FormatDiagnostic(PathFromUri(uri), o));
            if (lines.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines.Take(50)) sb.Append(l).Append('\n');
            var output = sb.ToString().TrimEnd();
            return output.Length > 8_000 ? output[..8_000] + "\n… (truncated)" : output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Diagnostics query failed");
            return null;
        }
    }

    /// <summary>Syncs the targets to their servers and waits for a fresh publish per file (timestamp-based).</summary>
    private async Task<Dictionary<string, JsonArray>> SyncAndCollectAsync(string root, IReadOnlyList<(string ServerId, string Path)> targets, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        foreach (var group in targets.GroupBy(t => t.ServerId, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var client = await GetClientAsync(root, group.Key, ct);
                if (client is null) continue;
                foreach (var (_, file) in group)
                {
                    if (!File.Exists(file)) continue;
                    var text = Text.NormalizeNewlines(await File.ReadAllTextAsync(file, ct));
                    SyncFile(client, LspServerCatalog.Find(group.Key)!, file, text);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "LSP sync for {Server} failed", group.Key);
            }
        }

        // Settle: wait for a publish that arrived after our didChange (timestamp-based; some servers omit versions).
        var uris = targets.Select(t => PathToUri(t.Path)).Distinct(StringComparer.Ordinal).ToList();
        var deadline = DateTime.UtcNow + DiagnosticsSettle;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (uris.All(u => _diagnostics.TryGetValue(u, out var d) && d.AtUtc >= startedAt)) break;
            await Task.Delay(50, ct);
        }

        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var u in uris)
            if (_diagnostics.TryGetValue(u, out var d) && d.AtUtc >= startedAt) result[u] = d.Diags;
        return result;
    }

    private static string FormatDiagnostic(string path, JsonObject o)
    {
        var severity = SeverityOf(o);
        var line = LongOf(o["range"]?["start"]?["line"]);
        var col = LongOf(o["range"]?["start"]?["character"]);
        var message = Text.FirstLine(o["message"]?.ToString() ?? "", 200);
        return severity switch
        {
            1 => $"{path}({line + 1},{col + 1}): error: {message}",
            2 => $"{path}({line + 1},{col + 1}): warning: {message}",
            3 => $"{path}({line + 1},{col + 1}): info: {message}",
            _ => $"{path}({line + 1},{col + 1}): hint: {message}",
        };
    }

    private static int SeverityOf(JsonObject o) =>
        o["severity"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 1;

    private static long LongOf(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    /// <summary>Full-syncs one file to its server (didOpen when new, didChange + didSave otherwise).</summary>
    public static void SyncFile(LspClient client, LspServerDef def, string path, string text)
    {
        var uri = PathToUri(path);
        if (!client.IsOpen(uri))
            client.DidOpen(uri, LspServerCatalog.LanguageIdFor(def, path), text);
        else
        {
            client.DidChangeFull(uri, text);
            client.DidSave(uri);
        }
    }

    internal static string PathToUri(string path) => new Uri(PathUtil.Normalize(path)).AbsoluteUri;

    internal static string PathFromUri(string uri)
    {
        var u = new Uri(uri);
        return OperatingSystem.IsWindows() ? u.LocalPath.Replace('/', Path.DirectorySeparatorChar) : u.LocalPath;
    }

    // --- idle shutdown ------------------------------------------------------------------

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(IdleTick, ct);
                var now = DateTime.UtcNow;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Client is not null && now - entry.LastUseUtc >= IdleTimeout)
                        await ShutdownEntryAsync(entry, publish: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ShutdownEntryAsync(Entry entry, bool publish)
    {
        var client = entry.Client;
        entry.Client = null;
        if (client is not null)
        {
            try
            {
                await client.ShutdownAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "LSP shutdown of {Server} in {Root} failed", entry.Def.Id, entry.Root);
            }
        }
        if (publish)
        {
            entry.Status = "stopped";
            entry.Error = null;
            Publish(View(entry.Def.Id)!);
        }
    }

    private static string Key(string root, string serverId) => PathUtil.Normalize(root) + "|" + serverId;
}
