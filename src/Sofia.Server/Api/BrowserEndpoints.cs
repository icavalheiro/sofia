using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sofia.Server.Agent;
using Sofia.Server.Browser;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Api;

public static class BrowserEndpoints
{
    private const int MaxInputBytes = 64 * 1024;

    /// <summary>Close code sent when the server closed the browser (idle) instead of the client leaving.</summary>
    private static readonly WebSocketCloseStatus ClosedStatus = (WebSocketCloseStatus)4001;

    public static void MapBrowserApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/sessions/{id}/browser", (string id, BrowserService browser, SofiaDb db) =>
        {
            var live = browser.Find(RootOf(db, id).Id);
            return Results.Ok(new { open = live is not null, url = live?.Page.Url });
        });

        api.MapPost("/sessions/{id}/browser/close", async (string id, BrowserService browser, SofiaDb db) =>
        {
            await browser.CloseAsync(RootOf(db, id).Id);
            return Results.NoContent();
        });

        // Live view: binary messages are JPEG frames, text messages are JSON (state, cursor, error, closed);
        // the client sends JSON input messages (navigate, mouse, key, text, resize, tab, active).
        // A browser is only started when the client asks for it (?create=1): a plain reconnect attaches to the
        // live session or reports "closed", so an idle-closed browser is never restarted behind the user's back.
        api.Map("/sessions/{id}/browser/stream", async (HttpContext http, string id, BrowserService browser, SofiaDb db) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) throw new ArgumentException("Open this endpoint as a WebSocket.");
            var root = RootOf(db, id);
            var create = http.Request.Query["create"].Any(v => v is "1" or "true");
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var ct = http.RequestAborted;

            var live = browser.Find(root.Id);
            if (live is null && create)
            {
                try
                {
                    live = await browser.GetAsync(root.Id, root.WorkspaceId, ct);
                }
                catch (InvalidOperationException ex)
                {
                    await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "error", fatal = true, message = ex.Message }), ct);
                    await CloseQuietlyAsync(socket);
                    return;
                }
            }
            if (live is null)
            {
                await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "state", closed = true }), ct);
                await CloseQuietlyAsync(socket, ClosedStatus);
                return;
            }

            var viewer = await live.AddViewerAsync();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var send = SendLoopAsync(socket, viewer, linked.Token);
                var receive = ReceiveLoopAsync(socket, live, viewer, linked.Token);
                var first = await Task.WhenAny(send, receive);
                // Say why we are going away before cancelling anything: cancelling a pending receive aborts the
                // socket (close code 1006), and the panel needs 4001 to know it must not reconnect.
                if (ReferenceEquals(first, send) && viewer.Closed)
                    await CloseOutputQuietlyAsync(socket, ClosedStatus);
                await linked.CancelAsync();
                await Task.WhenAll(Quiet(send), Quiet(receive));
            }
            finally
            {
                await live.RemoveViewerAsync(viewer);
                await CloseQuietlyAsync(socket, viewer.Closed ? ClosedStatus : WebSocketCloseStatus.NormalClosure);
            }
        });
    }

    /// <summary>Subagent conversations share the browser of the conversation that spawned them.</summary>
    private static Session RootOf(SofiaDb db, string id)
    {
        var session = db.Sessions.FindById(id) ?? throw new NotFoundException($"Session {id} not found.");
        return session.ParentId is { } parent ? db.Sessions.FindById(parent) ?? session : session;
    }

    private static async Task SendLoopAsync(WebSocket socket, BrowserViewer viewer, CancellationToken ct)
    {
        await foreach (var _ in viewer.Wake.ReadAllAsync(ct))
        {
            if (viewer.Closed) return;
            var (events, state, frame) = viewer.Take();
            foreach (var json in events) await SendTextAsync(socket, json, ct);
            if (state is not null) await SendTextAsync(socket, state, ct);
            if (frame is not null) await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, BrowserSession live, BrowserViewer viewer, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxInputBytes) return;
            if (!result.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "active")
                {
                    var active = !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.False;
                    await live.SetViewerActiveAsync(viewer, active);
                    continue;
                }
                if (!viewer.Active) await live.SetViewerActiveAsync(viewer, true);
                await live.HandleInputAsync(root);
            }
            catch (JsonException)
            {
                // Ignore malformed input.
            }
        }
    }

    private static Task SendTextAsync(WebSocket socket, string json, CancellationToken ct) =>
        socket.State == WebSocketState.Open
            ? socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct)
            : Task.CompletedTask;

    private static async Task Quiet(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The viewer went away.
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already closed.
        }
    }

    /// <summary>Sends our close frame without waiting for the peer (the receive loop may still be pending).</summary>
    private static async Task CloseOutputQuietlyAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(status, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already closing.
        }
    }
}
