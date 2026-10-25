using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>Credentials, base URL override and extra headers for one provider request.</summary>
public sealed record RequestAuth(string? Key, string? BaseUrl = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static RequestAuth From(ProviderConfig provider) => new(provider.ResolveKey());
}

public delegate Task<RequestAuth> ProviderAuthResolver(CancellationToken ct);

public sealed record DeviceFlow(string DeviceCode, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

public sealed record DevicePoll(string Status, string? Token = null, int? Interval = null);

/// <summary>
/// GitHub Copilot sign-in (OAuth device flow) and the short-lived Copilot token exchange the chat API requires.
/// The stored provider key is the long-lived GitHub OAuth token; the Copilot token only lives in memory.
/// </summary>
public sealed class CopilotAuth(IHttpClientFactory httpFactory)
{
    // The VS Code Copilot Chat OAuth app: the one Copilot accepts for device-flow tokens.
    private const string DefaultClientId = "Iv1.b507a08c87ecfe98";
    private const string DefaultApi = "https://api.githubcopilot.com";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private sealed record Session(string OAuthToken, string Token, string Api, DateTimeOffset ExpiresAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public static string ClientId => Environment.GetEnvironmentVariable("SOFIA_GITHUB_CLIENT_ID") is { Length: > 0 } id ? id : DefaultClientId;

    public static IReadOnlyDictionary<string, string> BaseHeaders { get; } = new Dictionary<string, string>
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat",
        ["Openai-Intent"] = "conversation-edits",
    };

    public async Task<DeviceFlow> StartDeviceFlowAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync("https://github.com/login/device/code", new JsonObject
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user",
        }, ct);
        var deviceCode = json["device_code"]?.GetValue<string>();
        var userCode = json["user_code"]?.GetValue<string>();
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
            throw new ProviderException($"GitHub: {json["error_description"]?.GetValue<string>() ?? "could not start the device flow."}");
        return new DeviceFlow(
            deviceCode,
            userCode,
            json["verification_uri"]?.GetValue<string>() ?? "https://github.com/login/device",
            json["interval"]?.GetValue<int>() ?? 5,
            json["expires_in"]?.GetValue<int>() ?? 900);
    }

    /// <summary>Status: 'pending' | 'success' | 'expired' | 'denied'.</summary>
    public async Task<DevicePoll> PollDeviceFlowAsync(string deviceCode, CancellationToken ct)
    {
        var json = await PostJsonAsync("https://github.com/login/oauth/access_token", new JsonObject
        {
            ["client_id"] = ClientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        }, ct);
        if (json["access_token"]?.GetValue<string>() is { Length: > 0 } token) return new DevicePoll("success", token);
        return json["error"]?.GetValue<string>() switch
        {
            "authorization_pending" => new DevicePoll("pending"),
            "slow_down" => new DevicePoll("pending", Interval: json["interval"]?.GetValue<int>()),
            "expired_token" => new DevicePoll("expired"),
            "access_denied" => new DevicePoll("denied"),
            var other => throw new ProviderException($"GitHub: {json["error_description"]?.GetValue<string>() ?? other ?? "unexpected response."}"),
        };
    }

    public async Task<RequestAuth> ResolveAsync(ProviderConfig provider, CancellationToken ct)
    {
        var oauth = provider.ResolveKey();
        if (string.IsNullOrWhiteSpace(oauth))
            throw new ProviderException("GitHub Copilot: not signed in. Sign in from Settings → Models.");

        await _gate.WaitAsync(ct);
        try
        {
            if (_session is { } cached && cached.OAuthToken == oauth && cached.ExpiresAt - RefreshMargin > DateTimeOffset.UtcNow)
                return ToAuth(cached);
            _session = await ExchangeAsync(oauth, ct);
            return ToAuth(_session);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RequestAuth ToAuth(Session s) => new(s.Token, s.Api, BaseHeaders);

    private async Task<Session> ExchangeAsync(string oauth, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/v2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("token", oauth);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in BaseHeaders) req.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"GitHub Copilot: could not reach api.github.com: {ex.Message}");
        }

        using (response)
        {
            // Tokens that cannot be exchanged (e.g. fine-grained PATs) are accepted by the API as-is.
            if ((int)response.StatusCode == 404) return new Session(oauth, oauth, DefaultApi, DateTimeOffset.UtcNow.AddHours(1));
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var hint = status is 401 or 403 ? " Sign in again, and check the account has an active Copilot subscription." : "";
                throw new ProviderException($"GitHub Copilot: token exchange failed ({status}).{hint}", status);
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json?["token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) throw new ProviderException("GitHub Copilot: token exchange returned no token.");
            var expires = json?["expires_at"] is JsonValue ev && ev.TryGetValue<long>(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.UtcNow.AddMinutes(20);
            var api = json?["endpoints"]?["api"]?.GetValue<string>();
            return new Session(oauth, token, string.IsNullOrWhiteSpace(api) ? DefaultApi : api.TrimEnd('/'), expires);
        }
    }

    private async Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("probe");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(req, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(text) ?? throw new ProviderException("GitHub: empty response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new ProviderException($"GitHub: {ex.Message}");
        }
    }
}
