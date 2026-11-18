using Sofia.Server.Agent;
using Sofia.Server.Browser;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Api;

/// <summary>Body of <c>POST /api/sessions/{id}/browser/open</c>.</summary>
public sealed record OpenBrowserRequest(string? Browser);

public static class BrowserEndpoints
{
    public static void MapBrowserApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Engines installed on the machine running the server, so Settings can offer only real choices.
        api.MapGet("/browsers", (BrowserService browser, SofiaDb db) =>
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
            return Results.Ok(new
            {
                engines = installed.Select(e => new { id = e.Id, kind = e.Kind, path = e.Path, version = e.Version }),
                auto,
                @default = configured,
                headless = BrowserService.Headless,
            });
        });

        api.MapGet("/sessions/{id}/browser", async (string id, BrowserService browser, SofiaDb db) =>
        {
            var root = RootOf(db, id);
            var live = browser.Find(root.Id);
            if (live is null) return Results.Ok(new { open = false, engine = (string?)null, url = (string?)null, title = (string?)null });
            var (url, title) = await live.InfoAsync();
            return Results.Ok(new { open = true, engine = live.EngineId, url, title });
        });

        // Opens (or returns) the conversation's window. The agent path is the browser tool; this one serves the UI
        // and any other client that wants a window without an agent call.
        api.MapPost("/sessions/{id}/browser/open", async (string id, OpenBrowserRequest? body, BrowserService browser, SofiaDb db, CancellationToken ct) =>
        {
            var root = RootOf(db, id);
            BrowserSession session;
            try
            {
                session = await browser.EnsureAsync(root.Id, root.WorkspaceId, body?.Browser, ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
            var (url, title) = await session.InfoAsync();
            return Results.Ok(new { open = true, engine = session.EngineId, url, title });
        });

        api.MapPost("/sessions/{id}/browser/close", async (string id, BrowserService browser, SofiaDb db) =>
        {
            await browser.CloseAsync(RootOf(db, id).Id);
            return Results.NoContent();
        });
    }

    /// <summary>Subagent conversations share the browser of the conversation that spawned them.</summary>
    private static Session RootOf(SofiaDb db, string id)
    {
        var session = db.Sessions.FindById(id) ?? throw new NotFoundException($"Session {id} not found.");
        return session.ParentId is { } parent ? db.Sessions.FindById(parent) ?? session : session;
    }
}
