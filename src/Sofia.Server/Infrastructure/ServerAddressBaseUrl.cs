using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sofia.Core.Infrastructure;

namespace Sofia.Server.Infrastructure;

/// <summary>The address Kestrel actually bound, as reachable from the same machine.</summary>
public sealed class ServerAddressBaseUrl(IServer server) : IBaseUrlProvider
{
    public string BaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address)) return "http://127.0.0.1:5199";
        return address.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("//+", "//127.0.0.1").Replace("//*", "//127.0.0.1").TrimEnd('/');
    }
}
