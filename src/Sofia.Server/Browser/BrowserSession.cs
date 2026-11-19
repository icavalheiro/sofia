using Microsoft.Playwright;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Browser;

/// <summary>
/// One conversation's browser window: the tabs of the engine Sofia launched (or attached to) and the state the
/// agent and the user share. The window belongs to the user — there is no live view and no input lock; the agent
/// reads it through snapshots and screenshots and acts with Playwright while the user can act natively.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly List<IPage> _pages = [];
    private readonly List<string> _notes = [];
    private readonly object _gate = new();
    // Emulated viewport state: one CDP session per page, the pages that carry an override and, for Firefox, the
    // content size measured before the first override (a Playwright viewport cannot go back to following the window).
    private readonly Dictionary<IPage, ICDPSession> _cdp = [];
    private readonly HashSet<IPage> _emulated = [];
    private readonly SemaphoreSlim _viewportGate = new(1, 1);
    private readonly BrowserHandle _handle;
    private readonly IEventBus _bus;
    private readonly ILogger _log;
    private IPage? _page;
    private string _title = "";
    private BrowserViewport? _viewport;
    private ICDPSession? _browserCdp;
    private (int Width, int Height)? _natural;
    private bool _disposed;
    private volatile bool _agentBusy;

    public BrowserSession(BrowserHandle handle, string id, string? workspaceId, IEventBus bus, ILogger log)
    {
        _handle = handle;
        Id = id;
        WorkspaceId = workspaceId;
        _bus = bus;
        _log = log;
    }

    public string Id { get; }
    public string? WorkspaceId { get; }
    public string EngineId => _handle.EngineId;
    public string ProfileDir => _handle.ProfileDir;
    public bool Headless => _handle.Headless;

    /// <summary>Serializes agent actions: subagents of one conversation share its window.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Raised when the window goes away on its own (the user closed it), so the service can forget it.</summary>
    public event Action<BrowserSession>? Closed;

    public IPage Page => _page ?? throw new InvalidOperationException("The browser has no open page.");

    public List<IPage> Pages
    {
        get
        {
            // A closed tab can stay in the list until its close event runs; numbering must never point at it.
            lock (_gate) return [.. _pages.Where(page => !page.IsClosed)];
        }
    }

    /// <summary>Adopts the windows Playwright already knows about (the user's tabs after a restart, too).</summary>
    public async Task StartAsync()
    {
        var context = _handle.Context;
        context.SetDefaultTimeout(15_000);
        context.SetDefaultNavigationTimeout(30_000);
        context.Page += (_, page) => Adopt(page);
        if (_handle.Browser is { } browser) browser.Disconnected += (_, _) => OnDisconnected();
        foreach (var page in context.Pages) Adopt(page);
        if (Pages.Count == 0) Adopt(await context.NewPageAsync());
        // An attached window may already be showing something the user opened; publish its state.
        await RefreshAsync();
    }

    private void OnDisconnected()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _pages.Clear();
        _page = null;
        _log.LogInformation("Browser window for session {Session} was closed", Id);
        // The window is gone, so its re-attach record points at nothing: drop it now instead of leaving a stale pid
        // behind for the next startup to reconcile (a recycled pid must never be trusted). Only this browser's own
        // record: a reopen of the same conversation reuses the profile dir and may already have written a new one.
        try
        {
            var record = BrowserInstanceStore.Read(_handle.ProfileDir);
            if (record is null || (_handle.Process is { } process && record.Pid == process.Id))
                BrowserInstanceStore.Delete(_handle.ProfileDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A profile still in use will be reconciled at the next startup.
        }
        Publish(open: false);
        Closed?.Invoke(this);
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

    /// <summary>Marks the conversation as agent-driven for the duration of the scope (serializes tool calls).</summary>
    public async Task<IDisposable> BeginAgentAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        _agentBusy = true;
        Publish(open: true, agent: true);
        return new AgentScope(this);
    }

    private sealed class AgentScope(BrowserSession owner) : IDisposable
    {
        public void Dispose()
        {
            owner._agentBusy = false;
            owner.Gate.Release();
        }
    }

    // --- pages -------------------------------------------------------------------

    private void Adopt(IPage page)
    {
        if (!Attach(page)) return;
        _ = ActivateAsync(page);
    }

    /// <summary>Registers a page (tabs, close handler, dialogs). Returns false when it was already known.</summary>
    private bool Attach(IPage page)
    {
        lock (_gate)
        {
            if (_pages.Contains(page)) return false;
            _pages.Add(page);
        }
        page.Close += (_, _) => _ = OnPageClosedAsync(page);
        page.Load += (_, _) => _ = RefreshAsync(page);
        page.Dialog += async (_, dialog) =>
        {
            // Deterministic for the agent: alerts are accepted, everything else dismissed. The user is told.
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
        return true;
    }

    /// <summary>
    /// Opens a page and makes it the active tab. The page is adopted here instead of waiting for the context's
    /// <c>Page</c> event, so the caller can address it immediately (the event can arrive after the call returns).
    /// </summary>
    public async Task<IPage> OpenTabAsync()
    {
        var page = await _handle.Context.NewPageAsync();
        Attach(page);
        await ActivateAsync(page);
        return page;
    }

    public async Task ActivateAsync(IPage page)
    {
        _page = page;
        try
        {
            await page.BringToFrontAsync();
        }
        catch (PlaywrightException)
        {
            // Closed meanwhile.
        }
        // Chromium device metrics are per target: give the newly activated tab the conversation's viewport back.
        await ReapplyViewportAsync(page);
        await RefreshAsync();
    }

    /// <summary>Brings the window itself to the front (the agent asking the user to look at it).</summary>
    public async Task BringToFrontAsync()
    {
        if (_handle.Process is { } process) WindowFocus.TryFocus(process);
        try
        {
            await Page.BringToFrontAsync();
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException($"The browser window is not reachable any more: {Text.FirstLine(ex.Message, 200)}", ex);
        }
    }

    private async Task OnPageClosedAsync(IPage page)
    {
        IPage? next;
        lock (_gate)
        {
            _pages.Remove(page);
            _cdp.Remove(page);
            _emulated.Remove(page);
            next = _pages.LastOrDefault();
        }
        if (_page != page || _disposed) return;
        if (next is null)
        {
            // The last tab is gone, which is how the user closes the window: Chromium exits on its own and raises
            // Disconnected. Never open a replacement page here — a target created while the window is going away
            // keeps the browser alive and resurrects it (the closed window came back with a fresh about:blank tab).
            // When the agent needs the browser again, BrowserTool reopens a window on demand.
            _page = null;
            Publish(open: true);
            // A headless browser has no window to close and stays alive with zero tabs, so nobody would ever close
            // it: stop it here (Disconnected then forgets the session). A windowed one exits by itself.
            if (_handle.Headless)
            {
                try
                {
                    if (_handle.Browser is { } browser) await browser.CloseAsync();
                }
                catch (PlaywrightException)
                {
                    // Already gone.
                }
                // Playwright's close asks the browser to quit; if it did not (or the process is a child Sofia
                // started), make sure nothing is left behind — the service drops the session on Disconnected and
                // would never dispose it.
                try
                {
                    if (_handle.Process is { HasExited: false } process) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone.
                }
            }
            return;
        }
        await ActivateAsync(next);
    }

    private async Task RefreshAsync(IPage? page = null)
    {
        var target = page ?? _page;
        if (target is null || _disposed) return;
        try
        {
            _title = await target.TitleAsync();
        }
        catch (PlaywrightException)
        {
            // Navigation in flight; the next load refreshes it.
            return;
        }
        Publish(open: true);
    }

    // --- navigation helpers shared by the agent ----------------------------------

    /// <summary>
    /// Turns what the agent typed into a navigable URL: bare hosts get a scheme and loose text becomes a
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
        await Page.GotoAsync(NormalizeUrl(input), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await RefreshAsync();
    }

    /// <summary>
    /// Walks the browser history. Done through the page instead of <c>Page.GoBackAsync</c> because with a CDP-attached
    /// window the previous document usually comes back from the back/forward cache, and Playwright then waits for a
    /// navigation event that never fires (30s timeout with the page already restored).
    /// </summary>
    public async Task GoHistoryAsync(bool back)
    {
        var before = Page.Url;
        await Page.EvaluateAsync(back ? "() => history.back()" : "() => history.forward()");
        for (var i = 0; i < 60 && Page.Url == before; i++) await Task.Delay(50);
        // A restored document is already loaded, so there is no lifecycle event to wait for; just let it paint.
        await Task.Delay(200);
        await RefreshAsync();
    }

    /// <summary>Accepts a combo such as "Control+a"; a lone printable character is typed, not pressed.</summary>
    public static async Task PressKeyAsync(IPage page, string key)
    {
        if (key.Length == 0) return;
        if (key.Length == 1) await page.Keyboard.TypeAsync(key);
        else await page.Keyboard.PressAsync(key);
    }

    public async Task<(string Url, string Title)> InfoAsync() => (Page.Url, await Page.TitleAsync());

    // --- emulated viewport -------------------------------------------------------

    /// <summary>
    /// Emulated layout viewport currently applied to this conversation's pages, or null when pages follow the window.
    /// Chrome DevTools device mode: it changes the layout, never the OS window.
    /// </summary>
    public BrowserViewport? Viewport => _viewport;

    /// <summary>One line for tool results while an emulation is active, so the emulated size is never mistaken for the window.</summary>
    public string ViewportNote => _viewport is { } viewport
        ? $"Viewport: {viewport.Describe()} emulated (layout only; the window is unchanged). Reset with viewport (no width/height)."
        : "";

    /// <summary>
    /// Applies an emulated layout viewport, or clears it when <paramref name="viewport"/> is null, and returns what the
    /// page actually reports — so a silent no-op (like <c>window.resizeTo</c> in this window) cannot be mistaken for
    /// success. The OS window is never resized or moved.
    /// </summary>
    public async Task<string> SetViewportAsync(BrowserViewport? viewport, CancellationToken ct)
    {
        await _viewportGate.WaitAsync(ct);
        try
        {
            if (viewport is null) return await ResetViewportAsync();
            _viewport = viewport;
            var note = await ApplyViewportAsync(Page, viewport);
            var applied = $"Viewport emulated: {viewport.Describe()} " +
                          $"(via {(_handle.IsChromium ? "Chromium CDP Emulation.setDeviceMetricsOverride" : "Firefox Playwright page viewport")}).{note}";
            if (viewport.Mobile)
                applied += " In mobile mode a page without <meta name=\"viewport\"> lays out at 980px wide (the mobile default) " +
                           "and scales down, so the effective layout can be wider than the device size.";
            return await DescribeLayoutAsync(applied) +
                   "\nMedia queries, flex wrapping and screenshots use the emulated viewport; the OS window was not resized. " +
                   "The page may look narrower than the window frame, and native browser UI is unaffected.";
        }
        finally
        {
            _viewportGate.Release();
        }
    }

    /// <summary>Emulation is session state: a new or switched tab gets the same viewport back.</summary>
    private async Task ReapplyViewportAsync(IPage page)
    {
        await _viewportGate.WaitAsync();
        try
        {
            if (_viewport is not { } viewport) return;
            try
            {
                var note = await ApplyViewportAsync(page, viewport);
                if (note.Length > 0) lock (_notes) _notes.Add(note.Trim());
            }
            catch (PlaywrightException ex)
            {
                lock (_notes) _notes.Add($"The emulated viewport {viewport.Describe()} could not be re-applied to this tab: {Text.FirstLine(ex.Message, 160)}");
            }
        }
        finally
        {
            _viewportGate.Release();
        }
    }

    private async Task<string> ApplyViewportAsync(IPage page, BrowserViewport viewport)
    {
        if (_handle.IsChromium)
        {
            await (await CdpAsync(page)).SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = viewport.Width,
                ["height"] = viewport.Height,
                ["deviceScaleFactor"] = viewport.DeviceScaleFactor,
                ["mobile"] = viewport.Mobile,
            });
            lock (_gate) _emulated.Add(page);
            return "";
        }

        // WebDriver BiDi has no device-metrics override in this Playwright version: width/height only.
        _natural ??= await MeasureAsync(page);
        await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
        return " This engine applies width/height only: mobile and device_scale_factor are ignored over WebDriver BiDi.";
    }

    private async Task<string> ResetViewportAsync()
    {
        List<IPage> pages;
        lock (_gate) pages = [.. _pages];
        var notes = new List<string>();

        if (_handle.IsChromium)
        {
            // Clear on every open page, not only the tracked ones: the override lives on the target, so whatever path
            // applied it, the page must not keep a stale emulation after a reset.
            foreach (var page in pages)
            {
                try
                {
                    await (await CdpAsync(page)).SendAsync("Emulation.clearDeviceMetricsOverride");
                }
                catch (PlaywrightException ex)
                {
                    notes.Add($"Could not clear the override on {page.Url}: {Text.FirstLine(ex.Message, 140)}.");
                }
            }
            lock (_gate) _emulated.Clear();
            if (_viewport is null && notes.Count == 0) return "No emulated viewport is active; the page follows the window.";
            _viewport = null;
            notes.Insert(0, "Viewport emulation cleared; the page follows the window again.");
        }
        else
        {
            if (_viewport is null) return "No emulated viewport is active; the page follows the window.";
            _viewport = null;
            if (_natural is { } natural)
            {
                try
                {
                    await Page.SetViewportSizeAsync(natural.Width, natural.Height);
                }
                catch (PlaywrightException ex)
                {
                    notes.Add($"Could not restore the pre-emulation size: {Text.FirstLine(ex.Message, 140)}.");
                }
                notes.Insert(0, $"Viewport emulation cleared; this engine cannot go back to following the window, so the " +
                                $"viewport is fixed at the pre-emulation size {natural.Width}x{natural.Height} " +
                                "(close and reopen the window to let it follow the window again).");
            }
            else notes.Insert(0, "Viewport emulation cleared.");
        }

        return await DescribeLayoutAsync(string.Join(" ", notes));
    }

    /// <summary>The measured layout, never the requested numbers: the caller can see whether the change took effect.</summary>
    private async Task<string> DescribeLayoutAsync(string message)
    {
        var page = Page;
        try
        {
            var inner = await page.EvaluateAsync<int[]>("() => [window.innerWidth, window.innerHeight]");
            var screen = await page.EvaluateAsync<int[]>("() => [window.screen.width, window.screen.height]");
            var dpr = await page.EvaluateAsync<double>("() => window.devicePixelRatio");
            var window = await WindowBoundsAsync();
            var windowText = window is null
                ? "this engine cannot report the OS window size here"
                : $"the OS window is still {window}";
            return $"{message}\nEffective layout viewport: {inner[0]}x{inner[1]} " +
                   $"(the page sees screen {screen[0]}x{screen[1]}, dpr {dpr:0.##}); {windowText}.";
        }
        catch (PlaywrightException ex)
        {
            return $"{message}\nCould not measure the layout viewport: {Text.FirstLine(ex.Message, 200)}";
        }
    }

    /// <summary>
    /// The real OS window size from the browser, independent of any emulation (a mobile override also changes what
    /// <c>window.outerWidth</c> reports, so it cannot prove the window was left alone).
    /// </summary>
    private async Task<string?> WindowBoundsAsync()
    {
        if (!_handle.IsChromium || _handle.Browser is not { } browser) return null;
        try
        {
            var info = await (await CdpAsync(Page)).SendAsync("Target.getTargetInfo");
            var targetId = info?.GetProperty("targetInfo").GetProperty("targetId").GetString();
            if (targetId is null) return null;
            _browserCdp ??= await browser.NewBrowserCDPSessionAsync();
            var window = await _browserCdp.SendAsync("Browser.getWindowForTarget", new Dictionary<string, object> { ["targetId"] = targetId });
            if (window is not { } value) return null;
            var bounds = value.GetProperty("bounds");
            return $"{bounds.GetProperty("width").GetInt32()}x{bounds.GetProperty("height").GetInt32()}";
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// After a tab closes: wait until the session points at an open page and re-apply the emulated viewport, so the
    /// snapshot of the closing action is not read from the tab that is going away.
    /// </summary>
    public async Task SettleActiveAsync()
    {
        IPage? active;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            lock (_gate) active = _page;
            if (active is not null && !active.IsClosed) break;
            await Task.Delay(50);
        }
        IPage? page;
        lock (_gate) page = _page;
        if (page is null || page.IsClosed) return;
        await ReapplyViewportAsync(page);
    }

    private async Task<(int Width, int Height)> MeasureAsync(IPage page)
    {
        var size = await page.EvaluateAsync<int[]>("() => [window.innerWidth, window.innerHeight]");
        return size.Length >= 2 ? (size[0], size[1]) : (0, 0);
    }

    private async Task<ICDPSession> CdpAsync(IPage page)
    {
        lock (_gate) if (_cdp.TryGetValue(page, out var cached)) return cached;
        var session = await _handle.Context.NewCDPSessionAsync(page);
        lock (_gate) _cdp[page] = session;
        return session;
    }

    /// <summary>The page is agent-driven right now (used by events/handlers, not to lock the user out).</summary>
    public bool AgentBusy => _agentBusy;

    public void Publish(bool open, bool agent = false) =>
        _bus.Publish(EventTypes.BrowserUpdated,
            new { open, agent, url = _page?.Url, engine = _handle.EngineId, title = _title }, Id, WorkspaceId);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_handle.Browser is { } browser) await browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The window is already gone.
        }
        if (_handle.Process is { } process)
        {
            // CDP attach: closing the Playwright connection does not close the window, so stop it ourselves.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
            process.Dispose();
        }
        BrowserInstanceStore.Delete(_handle.ProfileDir);
        Gate.Dispose();
        _viewportGate.Dispose();
    }

    /// <summary>
    /// Server shutdown: a window the user can still use is left open (and its record kept), so the next run
    /// re-attaches instead of opening a second one. A headless browser cannot be closed by anyone, so it is killed.
    /// </summary>
    public async Task DetachAsync()
    {
        if (_handle.Headless)
        {
            await DisposeAsync();
            return;
        }
        _disposed = true;
        try
        {
            if (_handle.Browser is { } browser) await browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The window is already gone.
        }
        _handle.Process?.Dispose();
        Gate.Dispose();
        _viewportGate.Dispose();
    }
}

/// <summary>
/// An emulated layout viewport (Chrome DevTools device mode): the page is laid out at this size while the OS window
/// keeps its own size. <c>Mobile</c> toggles device-style viewport/meta handling only — the user agent and touch input
/// are left alone, so pages keep serving what they serve to this browser.
/// </summary>
public sealed record BrowserViewport(int Width, int Height, bool Mobile, double DeviceScaleFactor)
{
    public const int MinSide = 200;
    public const int MaxSide = 4096;

    /// <summary>
    /// Turns the tool arguments into a viewport: both sides or neither (neither means "reset to the window"), 200–4096
    /// px per side and a device scale factor in 0.1–4.
    /// </summary>
    public static BrowserViewport? Validate(int? width, int? height, bool mobile, double deviceScaleFactor)
    {
        if (width is null && height is null)
        {
            if (mobile || Math.Abs(deviceScaleFactor - 1) > 1e-9)
                throw new ArgumentException("viewport needs 'width' and 'height' to change mobile or device_scale_factor; without them the call resets the emulation.");
            return null;
        }
        if (width is null || height is null)
            throw new ArgumentException("viewport needs both 'width' and 'height' (or neither of them, to reset to the window size).");
        if (width is < MinSide or > MaxSide || height is < MinSide or > MaxSide)
            throw new ArgumentException($"viewport sides must be between {MinSide} and {MaxSide} px (got {width}x{height}).");
        if (deviceScaleFactor is < 0.1 or > 4)
            throw new ArgumentException($"device_scale_factor must be between 0.1 and 4 (got {deviceScaleFactor}).");
        return new BrowserViewport(width.Value, height.Value, mobile, deviceScaleFactor);
    }

    public string Describe() => $"{Width}x{Height}{(Mobile ? " mobile" : "")} at dpr {DeviceScaleFactor:0.##}";
}
