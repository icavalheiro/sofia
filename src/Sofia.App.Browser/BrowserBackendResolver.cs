using Sofia.App.Services;
using Sofia.Client;

namespace Sofia.App.Browser;

/// <summary>
/// Browser backend discovery (spec 044): the page origin first (the WASM bundle is served by Sofía.Server itself),
/// then the connection remembered in <c>localStorage</c>. There is no local engine and no filesystem, so the gate stays
/// up with the reason when nothing answers.
/// </summary>
/// <remarks>
/// Loopback browsers need no token (<c>AccessMiddleware</c> trusts loopback callers). A remote browser gets
/// <c>401</c> on the workspace list, which becomes <see cref="PairingRequiredException"/>: the gate then shows the
/// pairing section, redeems <c>POST /api/pair/connect</c> and stores the token for the next probe.
/// </remarks>
internal sealed class BrowserBackendResolver : IBackendResolver
{
    public string Description => string.IsNullOrEmpty(Origin) ? "http server" : $"http {Origin}";

    private static string Origin => BrowserInterop.SafeOrigin();

    public async Task<ISofiaBackend?> ResolveAsync(Action<string> log, CancellationToken ct = default)
    {
        var stored = LocalState.Current.Get<StoredConnection>(LocalState.ConnectionKey);
        var candidates = new List<(string Url, string? Token)>();
        if (Origin.Length > 0)
        {
            var sameServer = stored is not null && SameServer(stored.ServerUrl, Origin);
            candidates.Add((Origin, sameServer ? stored!.AccessToken : null));
        }
        if (stored?.ServerUrl is { Length: > 0 } storedUrl && !SameServer(storedUrl, Origin))
        {
            candidates.Add((storedUrl.TrimEnd('/'), stored.AccessToken));
        }
        if (candidates.Count == 0)
        {
            log("no page origin and no stored connection");
            return null;
        }

        foreach (var (url, token) in candidates)
        {
            log($"probing {url}{(string.IsNullOrEmpty(token) ? "" : " (paired token)")}");
            var backend = new HttpSofiaBackend(new SofiaBackendOptions { BaseUrl = url, AccessToken = token });
            try
            {
                await backend.HealthAsync(ct);
                log($"{url} answered");
            }
            catch (Exception ex)
            {
                log($"no server on {url} ({ex.GetType().Name})");
                await backend.DisposeAsync();
                continue;
            }

            try
            {
                await backend.ListWorkspacesAsync(ct);
                return backend;
            }
            catch (SofiaApiException ex) when (ex.IsUnauthorized)
            {
                log($"{url} requires a paired token");
                throw new PairingRequiredException(url, backend, ex.Message);
            }
            catch (Exception ex)
            {
                // The server answered but the shell may still be usable: let the store surface the error.
                log($"workspaces failed: {ex.Message}");
                return backend;
            }
        }

        return null;
    }

    /// <summary>Same host and port, ignoring a trailing slash or a default-port spelling difference.</summary>
    private static bool SameServer(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
    }

    /// <summary>The <c>sofia.connection</c> document (same shape the web stored and the gate persists).</summary>
    internal sealed class StoredConnection
    {
        public string? ServerUrl { get; set; }
        public string? AccessToken { get; set; }
    }
}
