using Sofia.Core.Services;

namespace Sofia.Server.Api;

/// <summary>Body of <c>POST /api/sessions/{id}/browser/open</c>.</summary>
public sealed record OpenBrowserRequest(string? Browser);

/// <summary>HTTP binding for the browser window; the window state machine lives in <see cref="BrowserAdminService"/>.</summary>
public static class BrowserEndpoints
{
    public static void MapBrowserApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Engines installed on the machine running the server, so Settings can offer only real choices.
        api.MapGet("/browsers", (BrowserAdminService browser) => Results.Ok(browser.Engines()));
        api.MapGet("/sessions/{id}/browser", async (string id, BrowserAdminService browser) => Results.Ok(await browser.StateAsync(id)));

        // Opens (or returns) the conversation's window. The agent path is the browser tool; this one serves the UI
        // and any other client that wants a window without an agent call.
        api.MapPost("/sessions/{id}/browser/open", async (string id, OpenBrowserRequest? body, BrowserAdminService browser, CancellationToken ct) =>
            Results.Ok(await browser.OpenAsync(id, body?.Browser, ct)));

        api.MapPost("/sessions/{id}/browser/close", async (string id, BrowserAdminService browser) =>
        {
            await browser.CloseAsync(id);
            return Results.NoContent();
        });
    }
}
