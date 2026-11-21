using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sofia.App.Services;
using Sofia.Client;
using Sofia.Client.InProc;
using Sofia.Core;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;

namespace Sofia.App.Desktop;

/// <summary>
/// Desktop backend discovery: a server answering on <c>127.0.0.1:5199</c> wins (it may be a long-running service the
/// user started), otherwise this process takes the <c>~/.sofia</c> lock and runs the engine in-process through
/// <see cref="InProcSofiaBackend"/>. When neither works the gate stays up with the reason.
/// </summary>
public sealed class DesktopBackendResolver : IBackendResolver, IDisposable
{
    public const string DefaultUrl = "http://127.0.0.1:5199";

    private ServiceProvider? _provider;
    private HttpSofiaBackend? _http;

    public string Description => $"in-process engine, else {DefaultUrl}";

    public async Task<ISofiaBackend?> ResolveAsync(Action<string> log, CancellationToken ct = default)
    {
        log($"probing {DefaultUrl}");
        try
        {
            _http = new HttpSofiaBackend(new SofiaBackendOptions { BaseUrl = DefaultUrl });
            await _http.HealthAsync(ct);
            log($"{DefaultUrl} answered");
            return _http;
        }
        catch (Exception ex)
        {
            log($"no server on {DefaultUrl} ({ex.GetType().Name})");
            if (_http is not null) await _http.DisposeAsync();
            _http = null;
        }

        var dataDir = SofiaOptions.DefaultDataDir();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IHostApplicationLifetime, DesktopHostLifetime>();
            services.AddSofiaCore(new SofiaOptions { DataDir = dataDir }, core => core.ExclusiveDataDir = true);
            _provider = services.BuildServiceProvider();

            var ownership = _provider.GetRequiredService<DataDirectoryOwnership>();
            await ownership.StartAsync(ct);

            foreach (var hosted in _provider.GetServices<IHostedService>())
            {
                if (hosted is DataDirectoryOwnership) continue;
                await hosted.StartAsync(ct);
            }

            var backend = new InProcSofiaBackend(_provider);
            await backend.HealthAsync(ct);
            log($"in-process engine on {dataDir}");
            return backend;
        }
        catch (Exception ex)
        {
            log($"in-process engine unavailable: {ex.Message}");
            await DisposeAsync();
            return null;
        }
    }

    private async Task DisposeAsync()
    {
        if (_provider is null) return;
        try
        {
            foreach (var hosted in _provider.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
            await _provider.DisposeAsync();
        }
        catch
        {
            // Shutdown best effort.
        }
        _provider = null;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _provider = null;
    }

    private sealed class DesktopHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopping.Token;

        public void StopApplication() => _stopping.Cancel();
    }
}


