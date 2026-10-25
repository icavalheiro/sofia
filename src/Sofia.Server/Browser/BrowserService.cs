using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Playwright;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Browser;

/// <summary>
/// Owns one headless Chromium (system Chrome/Edge when present, Playwright's build otherwise) and one isolated
/// browser context per conversation. Nothing is ever shown on the user's desktop: the live view is a screencast
/// streamed to the UI, and the user can drive the same page from the UI.
/// </summary>
public sealed class BrowserService(IEventBus bus, ILogger<BrowserService> log) : IAsyncDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _launch = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Timer? _sweep;

    public BrowserSession? Find(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task<BrowserSession> GetAsync(string sessionId, string? workspaceId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
        var browser = await EnsureBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = BrowserSession.DefaultWidth, Height = BrowserSession.DefaultHeight },
            AcceptDownloads = false,
        });
        var session = new BrowserSession(sessionId, workspaceId, context, bus, log);
        if (!_sessions.TryAdd(sessionId, session))
        {
            await session.DisposeAsync();
            return _sessions[sessionId];
        }
        await session.StartAsync();
        _sweep ??= new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        session.Publish(open: true);
        return session;
    }

    public async Task CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        session.Publish(open: false);
        await session.DisposeAsync();
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        await _launch.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            _playwright ??= await Playwright.CreateAsync();
            var failures = new List<string>();
            var installed = false;
            foreach (var channel in new string?[] { "chrome", "msedge", null })
            {
                try
                {
                    return _browser = await LaunchAsync(channel);
                }
                catch (PlaywrightException ex) when (channel is null && !installed && ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
                {
                    installed = true;
                    log.LogInformation("No Chrome/Edge found; downloading Playwright's Chromium (one time)");
                    var code = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]), ct);
                    if (code != 0)
                    {
                        failures.Add("chromium: the automatic download failed");
                        continue;
                    }
                    try
                    {
                        return _browser = await LaunchAsync(null);
                    }
                    catch (PlaywrightException retry)
                    {
                        failures.Add($"chromium: {Text.FirstLine(retry.Message, 200)}");
                    }
                }
                catch (PlaywrightException ex)
                {
                    failures.Add($"{channel ?? "chromium"}: {Text.FirstLine(ex.Message, 200)}");
                }
            }
            throw new InvalidOperationException($"Could not start a headless browser. {string.Join("; ", failures)}");
        }
        finally
        {
            _launch.Release();
        }
    }

    private Task<IBrowser> LaunchAsync(string? channel) => _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        Channel = channel,
        Args = ["--no-first-run", "--no-default-browser-check"],
    });

    private async Task SweepAsync()
    {
        foreach (var (id, session) in _sessions)
            if (session.IsIdle(IdleTimeout)) await CloseAsync(id);
        if (!_sessions.IsEmpty || _browser is null || !await _launch.WaitAsync(0)) return;
        try
        {
            if (_sessions.IsEmpty)
            {
                var browser = _browser;
                _browser = null;
                await browser.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
            // Already gone.
        }
        finally
        {
            _launch.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sweep?.Dispose();
        foreach (var id in _sessions.Keys.ToList()) await CloseAsync(id);
        try
        {
            if (_browser is not null) await _browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // Already gone.
        }
        _playwright?.Dispose();
    }
}

/// <summary>Closes a conversation's browser together with the conversation.</summary>
public sealed class BrowserCleanupHandler(BrowserService browser) : IEventHandler
{
    public Task HandleAsync(SofiaEvent e, CancellationToken ct) =>
        e.Type == EventTypes.SessionDeleted && e.SessionId is not null ? browser.CloseAsync(e.SessionId) : Task.CompletedTask;
}

/// <summary>One live viewer of a browser session (a WebSocket connection). Frames and state are latest-wins.</summary>
public sealed class BrowserViewer
{
    private readonly object _gate = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Queue<string> _events = new();
    private byte[]? _frame;
    private string? _state;

    public ChannelReader<bool> Wake => _wake.Reader;

    public void PushFrame(byte[] frame)
    {
        lock (_gate) _frame = frame;
        _wake.Writer.TryWrite(true);
    }

    public void PushState(string json)
    {
        lock (_gate) _state = json;
        _wake.Writer.TryWrite(true);
    }

    public void PushEvent(string json)
    {
        lock (_gate)
        {
            if (_events.Count >= 20) _events.Dequeue();
            _events.Enqueue(json);
        }
        _wake.Writer.TryWrite(true);
    }

    public (List<string> Events, string? State, byte[]? Frame) Take()
    {
        lock (_gate)
        {
            var result = (_events.ToList(), _state, _frame);
            _events.Clear();
            _state = null;
            _frame = null;
            return result;
        }
    }

    public void Complete() => _wake.Writer.TryComplete();
}

/// <summary>A conversation's browser: one context, its tabs, the screencast and the viewers watching it.</summary>
public sealed class BrowserSession(string id, string? workspaceId, IBrowserContext context, IEventBus bus, ILogger log) : IAsyncDisposable
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 800;

    private readonly List<IPage> _pages = [];
    private readonly List<BrowserViewer> _viewers = [];
    private readonly List<string> _notes = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _cast = new(1, 1);
    private IPage? _page;
    private ICDPSession? _cdp;
    private byte[]? _lastFrame;
    private string _title = "";
    private bool _loading;
    private bool _disposed;
    private volatile bool _agentBusy;
    private long _lastActivity = Environment.TickCount64;

    /// <summary>Serializes agent actions: subagents of one conversation share its browser.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public int Width { get; private set; } = DefaultWidth;
    public int Height { get; private set; } = DefaultHeight;

    public IPage Page => _page ?? throw new InvalidOperationException("The browser has no open page.");

    public async Task StartAsync()
    {
        context.SetDefaultTimeout(15_000);
        context.SetDefaultNavigationTimeout(30_000);
        context.Page += (_, page) => Adopt(page);
        Adopt(await context.NewPageAsync());
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public bool IsIdle(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_viewers.Count > 0) return false;
        }
        return !_agentBusy && TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity)) > timeout;
    }

    /// <summary>Page events (dialogs) the agent should hear about on its next tool result.</summary>
    public string DrainNotes()
    {
        lock (_notes)
        {
            var text = string.Join("\n", _notes);
            _notes.Clear();
            return text;
        }
    }

    /// <summary>Marks the browser as agent-driven (the UI locks user input) until the scope is disposed.</summary>
    public async Task<IDisposable> BeginAgentAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        _agentBusy = true;
        Touch();
        PushState();
        Publish(open: true, agent: true);
        return new AgentScope(this);
    }

    private sealed class AgentScope(BrowserSession owner) : IDisposable
    {
        public void Dispose()
        {
            owner._agentBusy = false;
            owner.Touch();
            owner.PushState();
            owner.Gate.Release();
        }
    }

    // --- pages -------------------------------------------------------------------

    private void Adopt(IPage page)
    {
        lock (_gate)
        {
            if (_pages.Contains(page)) return;
            _pages.Add(page);
        }
        page.Close += (_, _) => _ = OnPageClosedAsync(page);
        page.FrameNavigated += (_, frame) =>
        {
            if (frame != page.MainFrame) return;
            _loading = true;
            PushState();
        };
        page.Load += (_, _) =>
        {
            _loading = false;
            PushState();
        };
        page.DOMContentLoaded += (_, _) => PushState();
        page.Dialog += async (_, dialog) =>
        {
            lock (_notes) _notes.Add($"A {dialog.Type} dialog appeared and was {(dialog.Type == "alert" ? "accepted" : "dismissed")}: \"{Text.FirstLine(dialog.Message, 200)}\"");
            try
            {
                if (dialog.Type == "alert") await dialog.AcceptAsync();
                else await dialog.DismissAsync();
            }
            catch (PlaywrightException)
            {
                // The page navigated away while the dialog was open.
            }
        };
        _ = ActivateAsync(page);
    }

    public async Task ActivateAsync(IPage page)
    {
        _page = page;
        await RestartCastAsync();
        try
        {
            await page.BringToFrontAsync();
        }
        catch (PlaywrightException)
        {
            // Closed meanwhile.
        }
        PushState();
    }

    private async Task OnPageClosedAsync(IPage page)
    {
        if (_disposed) return;
        IPage? next;
        lock (_gate)
        {
            _pages.Remove(page);
            next = _pages.LastOrDefault();
        }
        if (_page != page)
        {
            PushState();
            return;
        }
        try
        {
            // A fresh page is adopted and activated through the context's Page event.
            if (next is null) await context.NewPageAsync();
            else await ActivateAsync(next);
        }
        catch (PlaywrightException ex)
        {
            log.LogDebug(ex, "Could not recover the browser page");
        }
    }

    public List<IPage> Pages
    {
        get
        {
            lock (_gate) return [.. _pages];
        }
    }

    // --- navigation helpers shared by the agent and the user ---------------------

    /// <summary>
    /// Turns what the user or agent typed into a navigable URL: bare hosts get a scheme and loose text becomes a
    /// search. Only web pages (and about:blank) are allowed, so a page can never be pointed at local files.
    /// </summary>
    public static string NormalizeUrl(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new ArgumentException("A URL is required.");
        if (text.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
        if (!text.Contains("://"))
        {
            var host = text.Split('/', '?', '#')[0];
            var looksLikeHost = !text.Contains(' ') && (host.Contains('.') || host.Contains(':') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            if (!looksLikeHost) return $"https://duckduckgo.com/?q={Uri.EscapeDataString(text)}";
            var local = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.") || host.StartsWith("[::1]");
            text = (local ? "http://" : "https://") + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Only http and https pages can be opened (got '{Text.FirstLine(input, 80)}').");
        return uri.ToString();
    }

    public async Task GotoAsync(string input)
    {
        Touch();
        await Page.GotoAsync(NormalizeUrl(input), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    public async Task ResizeAsync(int width, int height)
    {
        width = Math.Clamp(width, 320, 2560);
        height = Math.Clamp(height, 240, 2560);
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        foreach (var page in Pages)
        {
            try
            {
                await page.SetViewportSizeAsync(width, height);
            }
            catch (PlaywrightException)
            {
                // Closed meanwhile.
            }
        }
        // A static page produces no screencast frame after a resize, so repaint viewers explicitly.
        foreach (var viewer in Viewers()) await StillFrameAsync(viewer);
        PushState();
    }

    // --- user input --------------------------------------------------------------

    public async Task HandleInputAsync(JsonElement message)
    {
        if (_agentBusy || _page is null) return;
        Touch();
        var page = _page;
        string Str(string name) => message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        float Num(string name) => message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0;
        try
        {
            switch (Str("type"))
            {
                case "navigate":
                    await GotoAsync(Str("url"));
                    break;
                case "back":
                    await page.GoBackAsync();
                    break;
                case "forward":
                    await page.GoForwardAsync();
                    break;
                case "reload":
                    await page.ReloadAsync();
                    break;
                case "resize":
                    await ResizeAsync((int)Num("width"), (int)Num("height"));
                    break;
                case "mouse":
                    await HandleMouseAsync(page, Str("action"), Num("x"), Num("y"), Str("button"), Num("deltaX"), Num("deltaY"));
                    break;
                case "key":
                    await PressKeyAsync(page, Str("key"));
                    break;
                case "text":
                    await page.Keyboard.InsertTextAsync(Str("text"));
                    break;
                case "tab":
                    var pages = Pages;
                    var index = (int)Num("index");
                    if (index >= 0 && index < pages.Count) await ActivateAsync(pages[index]);
                    break;
                case "closeTab":
                    var open = Pages;
                    var closing = (int)Num("index");
                    if (closing >= 0 && closing < open.Count && open.Count > 1) await open[closing].CloseAsync();
                    break;
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ArgumentException)
        {
            PushEvent(new { type = "error", message = Text.FirstLine(ex.Message, 200) });
        }
    }

    private static async Task HandleMouseAsync(IPage page, string action, float x, float y, string button, float dx, float dy)
    {
        var which = button switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left };
        switch (action)
        {
            case "move":
                await page.Mouse.MoveAsync(x, y);
                break;
            case "down":
                await page.Mouse.MoveAsync(x, y);
                await page.Mouse.DownAsync(new MouseDownOptions { Button = which });
                break;
            case "up":
                await page.Mouse.MoveAsync(x, y);
                await page.Mouse.UpAsync(new MouseUpOptions { Button = which });
                break;
            case "wheel":
                await page.Mouse.MoveAsync(x, y);
                await page.Mouse.WheelAsync(dx, dy);
                break;
        }
    }

    /// <summary>Accepts a combo such as "Control+a"; a lone printable character is typed, not pressed.</summary>
    public static async Task PressKeyAsync(IPage page, string key)
    {
        if (key.Length == 0) return;
        if (key.Length == 1) await page.Keyboard.TypeAsync(key);
        else await page.Keyboard.PressAsync(key);
    }

    // --- agent cursor ------------------------------------------------------------

    /// <summary>Tells viewers where the agent is acting, so its clicks are visible in the live view.</summary>
    public async Task ShowCursorAsync(ILocator locator, string action)
    {
        try
        {
            var box = await locator.First.BoundingBoxAsync(new LocatorBoundingBoxOptions { Timeout = 2_000 });
            if (box is not null) PushEvent(new { type = "cursor", x = box.X + box.Width / 2, y = box.Y + box.Height / 2, action });
        }
        catch (PlaywrightException)
        {
            // Not measurable; the action itself reports the real error.
        }
    }

    // --- viewers and screencast --------------------------------------------------

    public async Task<BrowserViewer> AddViewerAsync()
    {
        var viewer = new BrowserViewer();
        bool first;
        lock (_gate)
        {
            first = _viewers.Count == 0;
            _viewers.Add(viewer);
        }
        Touch();
        viewer.PushState(StateJson());
        if (_lastFrame is not null) viewer.PushFrame(_lastFrame);
        if (first) await RestartCastAsync();
        await StillFrameAsync(viewer);
        _ = PushStateAsync();
        return viewer;
    }

    public async Task RemoveViewerAsync(BrowserViewer viewer)
    {
        bool none;
        lock (_gate)
        {
            _viewers.Remove(viewer);
            none = _viewers.Count == 0;
        }
        viewer.Complete();
        if (none) await StopCastAsync();
    }

    /// <summary>A still frame, so a new viewer sees the page even when nothing is changing on screen.</summary>
    private async Task StillFrameAsync(BrowserViewer viewer)
    {
        try
        {
            if (_page is null) return;
            var frame = await _page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 60 });
            _lastFrame = frame;
            viewer.PushFrame(frame);
        }
        catch (PlaywrightException)
        {
            // Mid-navigation; the screencast delivers the next frame.
        }
    }

    private async Task StopCastAsync()
    {
        await _cast.WaitAsync();
        try
        {
            var cdp = _cdp;
            _cdp = null;
            if (cdp is null) return;
            try
            {
                await cdp.SendAsync("Page.stopScreencast");
                await cdp.DetachAsync();
            }
            catch (PlaywrightException)
            {
                // The page already closed.
            }
        }
        finally
        {
            _cast.Release();
        }
    }

    private async Task RestartCastAsync()
    {
        await StopCastAsync();
        bool watched;
        lock (_gate) watched = _viewers.Count > 0;
        var page = _page;
        if (!watched || page is null || _disposed) return;
        await _cast.WaitAsync();
        try
        {
            var cdp = await context.NewCDPSessionAsync(page);
            cdp.Event("Page.screencastFrame").OnEvent += (_, args) => OnFrame(cdp, args);
            _cdp = cdp;
            await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = 60,
                ["maxWidth"] = 1600,
                ["maxHeight"] = 1600,
            });
        }
        catch (PlaywrightException ex)
        {
            log.LogDebug(ex, "Could not start the browser screencast");
        }
        finally
        {
            _cast.Release();
        }
    }

    private void OnFrame(ICDPSession cdp, JsonElement? args)
    {
        if (args is not { } json || !json.TryGetProperty("data", out var data)) return;
        var frameId = json.TryGetProperty("sessionId", out var s) ? s.GetInt32() : 0;
        _ = AckAsync(cdp, frameId);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data.GetString() ?? "");
        }
        catch (FormatException)
        {
            return;
        }
        _lastFrame = bytes;
        foreach (var viewer in Viewers()) viewer.PushFrame(bytes);
    }

    private static async Task AckAsync(ICDPSession cdp, int frameId)
    {
        try
        {
            await cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = frameId });
        }
        catch (PlaywrightException)
        {
            // Detached.
        }
    }

    private List<BrowserViewer> Viewers()
    {
        lock (_gate) return [.. _viewers];
    }

    // --- state -------------------------------------------------------------------

    private string StateJson()
    {
        List<IPage> pages;
        lock (_gate) pages = [.. _pages];
        return JsonSerializer.Serialize(new
        {
            type = "state",
            url = _page?.Url ?? "about:blank",
            title = _title,
            loading = _loading,
            agentBusy = _agentBusy,
            viewport = new { width = Width, height = Height },
            tabs = pages.Select((p, i) => new { index = i, url = p.Url, active = p == _page }).ToList(),
        }, Json.Options);
    }

    public void PushState() => _ = PushStateAsync();

    private async Task PushStateAsync()
    {
        if (_disposed) return;
        try
        {
            if (_page is not null) _title = await _page.TitleAsync();
        }
        catch (PlaywrightException)
        {
            // Keep the previous title.
        }
        var json = StateJson();
        foreach (var viewer in Viewers()) viewer.PushState(json);
    }

    private void PushEvent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json.Options);
        foreach (var viewer in Viewers()) viewer.PushEvent(json);
    }

    public async Task<(string Url, string Title)> InfoAsync() => (Page.Url, await Page.TitleAsync());

    public void Publish(bool open, bool agent = false) =>
        bus.Publish(EventTypes.BrowserUpdated, new { open, agent, url = _page?.Url }, id, workspaceId);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var viewer in Viewers()) viewer.Complete();
        await StopCastAsync();
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The browser is already shutting down.
        }
    }
}
