namespace Sofia.Client;

/// <summary>Where the backend lives and how to authenticate against it.</summary>
public sealed class SofiaBackendOptions
{
    /// <summary>Base address of the server, e.g. <c>http://127.0.0.1:5199</c> (no trailing slash needed).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5199";

    /// <summary>Paired bearer token; loopback requests do not need one.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Optional pre-configured client (tests, proxies, a platform handler).</summary>
    public HttpClient? HttpClient { get; set; }
}
