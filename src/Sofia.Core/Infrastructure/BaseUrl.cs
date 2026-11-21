namespace Sofia.Core.Infrastructure;

/// <summary>
/// Base URL the local HTTP API answers on, when the host exposes one. The ASP.NET server reports its own listening
/// address; a host without an API (the in-process desktop head) reports <see cref="None"/>, and the parts of the
/// agent-facing guidance that tell the model to call the REST API are left out.
/// </summary>
public interface IBaseUrlProvider
{
    /// <summary>e.g. <c>http://127.0.0.1:5199</c>, or <c>""</c> when this host has no HTTP API.</summary>
    string BaseUrl();
}

/// <summary>A host whose API address is known up front; <c>""</c> means "no HTTP API".</summary>
public sealed class ConstantBaseUrl(string value) : IBaseUrlProvider
{
    public static readonly ConstantBaseUrl None = new("");

    public string BaseUrl() => value;
}
