using System.Diagnostics;
using System.Net.Http;
using Microsoft.Playwright;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Browser;

/// <summary>
/// A live browser window plus the Playwright handles used to drive it. <see cref="OwnsProcess"/> is true when Sofia
/// started a Chromium binary itself (and attached over CDP): closing then means disconnecting and killing it.
/// </summary>
public sealed record BrowserHandle(
    string EngineId,
    string EnginePath,
    bool IsChromium,
    bool Headless,
    int Port,
    Process? Process,
    IBrowser? Browser,
    IBrowserContext Context,
    string ProfileDir)
{
    public bool OwnsProcess => Process is not null;
}

/// <summary>
/// Turns an installed engine into a live window. Chromium-family engines are started by Sofia itself with their own
/// profile dir and a loopback debug port, then driven over CDP — no Playwright automation flags, so the window the
/// user gets is an ordinary browser. Firefox is driven by Playwright over the <c>moz-firefox</c> WebDriver BiDi
/// channel, which can only launch (not re-attach).
/// </summary>
public sealed class BrowserLauncher(IPlaywright playwright, ILogger log)
{
    /// <summary>
    /// Headless when explicitly asked (<c>SOFIA_BROWSER_HEADLESS=1</c>) or when no display can exist (Docker/CI, a
    /// Linux box without X11/Wayland). Windows and macOS always have a desktop.
    /// </summary>
    public static bool IsHeadlessDefault()
    {
        var env = Environment.GetEnvironmentVariable("SOFIA_BROWSER_HEADLESS")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(env)) return env is "1" or "true" or "yes" or "on";
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return false;
        if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv")) return true;
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    /// <summary>Chromium command line: a private profile, a loopback debug port and no first-run/translate noise.</summary>
    public static string[] ChromiumArguments(string profileDir, int port, bool headless)
    {
        var args = new List<string>
        {
            $"--user-data-dir={profileDir}",
            $"--remote-debugging-port={port}",
            // Chrome 111+ rejects debugger WebSocket connections that carry an Origin header; the port is loopback-only.
            "--remote-allow-origins=*",
            "--no-first-run",
            "--no-default-browser-check",
            "--no-service-autorun",
            "--disable-features=Translate",
        };
        if (headless) args.Add("--headless=new");
        return [.. args];
    }

    /// <summary>A free loopback port for one browser's debug endpoint.</summary>
    public static int FreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Starts the engine as a real window (or headless) and returns the handle Sofia drives.</summary>
    public async Task<BrowserHandle> LaunchAsync(BrowserEngine engine, string sessionId, string profileDir, bool headless, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        log.LogInformation("Starting {Engine} for session {Session} ({Display}) with profile {Profile}",
            engine.Id, sessionId, headless ? "headless" : "windowed", profileDir);
        if (!engine.IsChromium) return await LaunchFirefoxAsync(engine, profileDir, headless, ct);

        var port = FreeLoopbackPort();
        var process = StartChromium(engine.Path, ChromiumArguments(profileDir, port, headless));
        try
        {
            await WaitForDevToolsAsync(process, port, ct);
            var browser = await ConnectChromiumAsync(port, profileDir, ct);
            var context = browser.Contexts.Count > 0
                ? browser.Contexts[0]
                : throw new InvalidOperationException(
                    $"{engine.Id} was started but exposes no default browser context to attach to. Close it and try again.");
            // The window is the point of this feature: raise it so the user actually sees the page open.
            if (!headless) WindowFocus.TryFocus(process);
            return new BrowserHandle(engine.Id, engine.Path, true, headless, port, process, browser, context, profileDir);
        }
        catch
        {
            // A launch that never became usable must not leave the window running behind the user.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
            process.Dispose();
            throw;
        }
    }

    /// <summary>Last resort in the headless path: Playwright's own bundled Chromium (downloaded on first use).</summary>
    public async Task<BrowserHandle> LaunchBundledChromiumAsync(string profileDir, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = null });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("No installed browser and no Playwright Chromium; downloading it (one time)");
            var code = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]), ct);
            if (code != 0) throw new InvalidOperationException("No browser is installed and Playwright's Chromium download failed.", ex);
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = null });
        }
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = null });
        return new BrowserHandle("chromium", "", true, true, 0, null, browser, context, profileDir);
    }

    /// <summary>Attaches to a Chromium that is already running (the window a restart found still open).</summary>
    public async Task<BrowserHandle> AttachChromiumAsync(BrowserEngine engine, BrowserInstance instance, string profileDir, CancellationToken ct)
    {
        var browser = await ConnectChromiumAsync(instance.Port, profileDir, ct);
        var context = browser.Contexts.Count > 0
            ? browser.Contexts[0]
            : throw new InvalidOperationException("The running browser exposes no default context.");
        Process? process = null;
        try
        {
            process = Process.GetProcessById(instance.Pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The pid is gone; the window is still reachable, Sofia just cannot close it later.
        }
        return new BrowserHandle(engine.Id, engine.Path, true, BrowserLauncher.IsHeadlessDefault(), instance.Port, process, browser, context, profileDir);
    }

    private static Process StartChromium(string executable, string[] arguments)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
    }

    private static async Task WaitForDevToolsAsync(Process process, int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"The browser exited while starting (exit code {process.ExitCode}). " +
                    "Another window may still be using its profile; close it and try again.");
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/version", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                // Not listening yet.
            }
            await Task.Delay(150, ct);
        }
        throw new InvalidOperationException($"The browser did not expose its debug endpoint on 127.0.0.1:{port} within 20s.");
    }

    private async Task<IBrowser> ConnectChromiumAsync(int port, string profileDir, CancellationToken ct) =>
        await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}", new BrowserTypeConnectOverCDPOptions
        {
            // No Playwright overrides on the context: focus/media/font emulation would make the window stop behaving
            // like the browser the user knows.
            NoDefaults = true,
            IsLocal = true,
            ArtifactsDir = Path.Combine(profileDir, "artifacts"),
            Timeout = 20_000,
        });

    private async Task<BrowserHandle> LaunchFirefoxAsync(BrowserEngine engine, string profileDir, bool headless, CancellationToken ct)
    {
        try
        {
            var context = await playwright.Firefox.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
            {
                // Stock Firefox over WebDriver BiDi; Playwright resolves the platform install path.
                Channel = "moz-firefox",
                Headless = headless,
                // Let the page use the real window instead of a fixed emulated viewport.
                ViewportSize = null,
            });
            var browser = context.Browser;
            return new BrowserHandle(engine.Id, engine.Path, false, headless, 0, null, browser, context, profileDir);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"Firefox could not be started over the moz-firefox (WebDriver BiDi) channel: {Text.FirstLine(ex.Message, 400)}", ex);
        }
    }
}
