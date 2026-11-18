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
    private readonly BrowserHandle _handle;
    private readonly IEventBus _bus;
    private readonly ILogger _log;
    private IPage? _page;
    private string _title = "";
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
            lock (_gate) return [.. _pages];
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
        lock (_gate)
        {
            if (_pages.Contains(page)) return;
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
        _ = ActivateAsync(page);
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
            next = _pages.LastOrDefault();
        }
        if (_page != page || _disposed) return;
        if (next is null)
        {
            // The last tab: Chromium quits on its own (which raises Disconnected). Give the agent a page either way.
            try
            {
                Adopt(await _handle.Context.NewPageAsync());
            }
            catch (PlaywrightException)
            {
                // The window is closing.
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
    }
}
