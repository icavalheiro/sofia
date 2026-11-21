using System.Net;
using Sofia.Core.Infrastructure;
using Sofia.Core.Security;

namespace Sofia.Server.Security;

/// <summary>
/// Loopback callers are trusted; every other caller needs a paired bearer token.
/// Browser requests from foreign origins are rejected so a web page cannot drive the
/// local harness (CSRF / DNS-rebinding protection).
/// </summary>
public sealed class AccessMiddleware(RequestDelegate next, PairingService pairing, SofiaOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");
        if (!guarded)
        {
            await next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsAllowedOrigin(origin, context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"Origin {origin} is not allowed." });
            return;
        }

        if (IsLoopback(ClientAddress(context)) || path.StartsWithSegments("/api/health") || path.StartsWithSegments("/api/pair/connect"))
        {
            await next(context);
            return;
        }

        var token = context.Request.Headers.Authorization.ToString();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        else token = context.Request.Query["access_token"].ToString();
        if (!pairing.IsValid(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A paired access token is required." });
            return;
        }
        await next(context);
    }

    private bool IsAllowedOrigin(string origin, HttpContext context)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback || uri.Host == "localhost") return true;
        if (string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)) return true;
        return options.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    /// <summary>
    /// The real client: a loopback peer may be a local reverse proxy (the Vite dev server),
    /// so its X-Forwarded-For wins; forwarded headers from remote peers are ignored.
    /// </summary>
    public static IPAddress? ClientAddress(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (!IsLoopback(peer)) return peer;
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(forwarded)) return peer;
        var first = forwarded.Split(',')[0].Trim();
        return IPAddress.TryParse(first, out var ip) ? ip : IPAddress.None;
    }
}
