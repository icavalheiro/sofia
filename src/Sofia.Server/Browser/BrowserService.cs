using System.Collections.Concurrent;
using Microsoft.Playwright;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Browser;

/// <summary>
/// Owns one real browser window per conversation: the installed Chrome-family browser Sofia starts itself and drives
/// over CDP, or the installed Firefox driven over WebDriver BiDi. Nothing is streamed to the UI — the user works in
/// the window and the agent sees the page through snapshots and screenshots. A window is only closed when the user
/// closes it, the agent asks for it, or its conversation is deleted.
/// </summary>
public sealed class BrowserService(SofiaDb db, SofiaOptions options, IEventBus bus, ILogger<BrowserService> log) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _launch = new(1, 1);
    private IPlaywright? _playwright;
    private BrowserLauncher? _launcher;

    /// <summary>Profile directories live inside SOFIA_HOME, one per session and engine.</summary>
    public string BrowsersRoot => Path.Combine(options.DataDir, "browsers");

    /// <summary>True when Sofia opens browsers without a window (Docker, CI, SOFIA_BROWSER_HEADLESS=1).</summary>
    public static bool Headless => BrowserLauncher.IsHeadlessDefault();

    /// <summary>Engines installed on the machine running the server.</summary>
    public IReadOnlyList<BrowserEngine> Installed() => BrowserCatalog.Detect();

    public BrowserSession? Find(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    /// <summary>
    /// Returns the conversation's window, opening one when needed. <paramref name="engineId"/> overrides the
    /// configured default ("auto" by default) and is validated against what is installed.
    /// </summary>
    public async Task<BrowserSession> EnsureAsync(string sessionId, string? workspaceId, string? engineId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
        await _launch.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing)) return existing;

            var headless = BrowserLauncher.IsHeadlessDefault();
            var installed = BrowserCatalog.Detect();
            var requested = string.IsNullOrWhiteSpace(engineId) ? db.GetConfig().DefaultBrowser : engineId;
            // With no engine installed at all the headless path may still use Playwright's bundled Chromium; a
            // windowed session cannot, so it fails with an actionable message.
            var engine = installed.Count > 0 ? BrowserCatalog.Resolve(requested, installed) : null;
            if (engine is null && !headless) throw new InvalidOperationException(BrowserCatalog.MissingMessage(requested));

            var session = await TryAttachAsync(sessionId, workspaceId, ct);
            if (session is null)
            {
                var launcher = await GetLauncherAsync();
                var profileDir = ProfileDir(sessionId, engine?.Id ?? "chromium");
                Directory.CreateDirectory(profileDir);
                var handle = engine is null
                    ? await launcher.LaunchBundledChromiumAsync(profileDir, ct)
                    : await launcher.LaunchAsync(engine, sessionId, profileDir, headless, ct);
                session = Track(new BrowserSession(handle, sessionId, workspaceId, bus, log));
                try
                {
                    await session.StartAsync();
                }
                catch
                {
                    await session.DisposeAsync();
                    throw;
                }
                // Only a window Sofia started itself can be re-attached after a restart (a CDP debug port).
                if (handle is { Port: > 0, Process: not null, EnginePath.Length: > 0 })
                    BrowserInstanceStore.Write(profileDir, new BrowserInstance
                    {
                        Engine = handle.EngineId,
                        Executable = handle.EnginePath,
                        Pid = handle.Process.Id,
                        Port = handle.Port,
                        SessionId = sessionId,
                        StartedAt = DateTimeOffset.UtcNow,
                    });
            }

            if (!_sessions.TryAdd(sessionId, session))
            {
                await session.DisposeAsync();
                return _sessions[sessionId];
            }
            session.Publish(open: true);
            return session;
        }
        finally
        {
            _launch.Release();
        }
    }

    /// <summary>Closes the conversation's window (and disconnects from it).</summary>
    public async Task CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        session.Publish(open: false);
        await session.DisposeAsync();
    }

    /// <summary>Closes the window of a deleted conversation and removes its profile directories.</summary>
    public async Task ForgetAsync(string sessionId)
    {
        await CloseAsync(sessionId);
        foreach (var (profileDir, instance) in BrowserInstanceStore.Enumerate(BrowsersRoot))
        {
            if (instance.SessionId != sessionId) continue;
            BrowserInstanceStore.Kill(instance, BrowserInstanceStore.ExecutableOfPid);
            BrowserInstanceStore.Delete(profileDir);
        }
        var sessionDir = Path.Combine(BrowsersRoot, sessionId);
        if (!Directory.Exists(sessionDir)) return;
        // Chrome releases its profile locks a moment after the process exits.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(sessionDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
        log.LogDebug("Could not delete the browser profiles of session {Session}", sessionId);
    }

    /// <summary>
    /// Startup reconciliation: re-attach windows the user still has open, kill the ones whose conversation is gone
    /// (verifying the pid first) and forget records that point at nothing.
    /// </summary>
    public async Task<BrowserReconcileResult?> ReconcileAsync(CancellationToken ct)
    {
        List<Session> sessions;
        try
        {
            sessions = [.. db.Sessions.FindAll()];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read sessions to reconcile browser windows");
            return null;
        }
        var live = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var result = BrowserInstanceStore.Reconcile(BrowsersRoot, live);
        foreach (var killed in result.Killed)
            log.LogInformation("Killed the orphaned {Engine} window (pid {Pid}) of deleted session {Session}", killed.Engine, killed.Pid, killed.SessionId);
        foreach (var attach in result.Attach)
        {
            if (_sessions.ContainsKey(attach.SessionId)) continue;
            var workspaceId = sessions.FirstOrDefault(s => s.Id == attach.SessionId)?.WorkspaceId;
            try
            {
                await EnsureAsync(attach.SessionId, workspaceId, attach.Engine, ct);
                log.LogInformation("Re-attached the {Engine} window of session {Session}", attach.Engine, attach.SessionId);
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or TimeoutException)
            {
                log.LogWarning("Could not re-attach the {Engine} window of session {Session}: {Error}", attach.Engine, attach.SessionId, Text.FirstLine(ex.Message, 200));
            }
        }
        return result;
    }

    private BrowserSession Track(BrowserSession session)
    {
        session.Closed += OnSessionClosed;
        return session;
    }

    private void OnSessionClosed(BrowserSession session)
    {
        session.Closed -= OnSessionClosed;
        _sessions.TryRemove(session.Id, out _);
    }

    /// <summary>Connects to a window recorded for this conversation that is still running (server restart).</summary>
    private async Task<BrowserSession?> TryAttachAsync(string sessionId, string? workspaceId, CancellationToken ct)
    {
        foreach (var (profileDir, instance) in BrowserInstanceStore.Enumerate(BrowsersRoot))
        {
            if (instance.SessionId != sessionId || instance.Port <= 0) continue;
            if (!BrowserInstanceStore.IsListening(instance.Port)) continue;
            if (instance.Engine == "firefox")
            {
                // Firefox is launched by Playwright, so it cannot outlive the server; the record is stale.
                BrowserInstanceStore.Delete(profileDir);
                continue;
            }
            var launcher = await GetLauncherAsync();
            var engine = new BrowserEngine(instance.Engine, BrowserEngine.ChromiumKind, instance.Executable);
            try
            {
                var handle = await launcher.AttachChromiumAsync(engine, instance, profileDir, ct);
                var session = Track(new BrowserSession(handle, sessionId, workspaceId, bus, log));
                await session.StartAsync();
                log.LogInformation("Re-attached the {Engine} window of session {Session}", instance.Engine, sessionId);
                return session;
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or TimeoutException)
            {
                // The record lies (a crashed or unrelated port): reap it so a fresh window can use the profile.
                log.LogWarning("Could not attach to the recorded {Engine} window (pid {Pid}): {Error}", instance.Engine, instance.Pid, Text.FirstLine(ex.Message, 200));
                BrowserInstanceStore.Kill(instance, BrowserInstanceStore.ExecutableOfPid);
                BrowserInstanceStore.Delete(profileDir);
            }
        }
        return null;
    }

    private string ProfileDir(string sessionId, string engineId) =>
        Path.Combine(BrowsersRoot, SafeSegment(sessionId), SafeSegment(engineId));

    /// <summary>Ids come from the database, but a profile path is still built from them: keep it one segment.</summary>
    private static string SafeSegment(string value)
    {
        var cleaned = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length > 0 ? cleaned : "session";
    }

    private async Task<BrowserLauncher> GetLauncherAsync()
    {
        _playwright ??= await Playwright.CreateAsync();
        return _launcher ??= new BrowserLauncher(_playwright, log);
    }

    public async ValueTask DisposeAsync()
    {
        // A graceful shutdown leaves windowed browsers open (the user may still be using them); the next run
        // re-attaches through instance.json. Headless ones are killed — nobody can close a windowless browser.
        foreach (var id in _sessions.Keys.ToList())
        {
            if (!_sessions.TryRemove(id, out var session)) continue;
            try
            {
                await session.DetachAsync();
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
            {
                log.LogDebug("Could not detach the browser of session {Session}: {Error}", id, Text.FirstLine(ex.Message, 120));
            }
        }
        _playwright?.Dispose();
        _launch.Dispose();
    }
}

/// <summary>Closes a conversation's browser (and drops its profile) together with the conversation.</summary>
public sealed class BrowserCleanupHandler(BrowserService browser) : IEventHandler
{
    public Task HandleAsync(SofiaEvent e, CancellationToken ct) =>
        e.Type == EventTypes.SessionDeleted && e.SessionId is not null ? browser.ForgetAsync(e.SessionId) : Task.CompletedTask;
}

/// <summary>Re-attaches (or reaps) the browser windows left over from a previous server run.</summary>
public sealed class BrowserStartupService(BrowserService browser, ILogger<BrowserStartupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the server finish starting: this must never delay the first request.
        await Task.Yield();
        try
        {
            await browser.ReconcileAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not reconcile the browser windows left by the previous run");
        }
    }
}
