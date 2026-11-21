using Sofia.Contracts.Domain;
using Sofia.Core.Agent;
using Sofia.Core.Browser;
using Sofia.Core.Data;

namespace Sofia.Core.Services;

/// <summary>
/// Browser window administration for the UI: which engines are installed, the conversation's window state, opening
/// and closing it. Subagent conversations share the window of the conversation that spawned them.
/// </summary>
public sealed class BrowserAdminService(SofiaDb db, BrowserService browser)
{
    /// <summary>Engines installed on this machine, the automatic choice and the configured default.</summary>
    public object Engines()
    {
        var installed = browser.Installed();
        var configured = db.GetConfig().DefaultBrowser;
        string? auto = null;
        try
        {
            auto = BrowserCatalog.Resolve(BrowserCatalog.Auto, installed).Id;
        }
        catch (InvalidOperationException)
        {
            // Nothing installed: the headless path falls back to Playwright's bundled Chromium.
        }
        return new
        {
            engines = installed.Select(e => new { id = e.Id, kind = e.Kind, path = e.Path, version = e.Version }),
            auto,
            @default = configured,
            headless = BrowserService.Headless,
        };
    }

    public async Task<object> StateAsync(string sessionId)
    {
        var live = browser.Find(RootOf(sessionId).Id);
        if (live is null) return new { open = false, engine = (string?)null, url = (string?)null, title = (string?)null };
        var (url, title) = await live.InfoAsync();
        return new { open = true, engine = live.EngineId, url, title };
    }

    /// <summary>Opens (or returns) the conversation's window, without going through the agent's browser tool.</summary>
    public async Task<object> OpenAsync(string sessionId, string? engine, CancellationToken ct)
    {
        var root = RootOf(sessionId);
        BrowserSession session;
        try
        {
            session = await browser.EnsureAsync(root.Id, root.WorkspaceId, engine, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
        var (url, title) = await session.InfoAsync();
        return new { open = true, engine = session.EngineId, url, title };
    }

    public Task CloseAsync(string sessionId) => browser.CloseAsync(RootOf(sessionId).Id);

    /// <summary>Subagent conversations share the browser of the conversation that spawned them.</summary>
    private Session RootOf(string id)
    {
        var session = db.Sessions.FindById(id) ?? throw new NotFoundException($"Session {id} not found.");
        return session.ParentId is { } parent ? db.Sessions.FindById(parent) ?? session : session;
    }
}
