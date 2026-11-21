using Microsoft.AspNetCore.SignalR;
using Sofia.Contracts.Events;
using Sofia.Core.Events;

namespace Sofia.Server.Events;

/// <summary>SignalR hub. The server only pushes; clients call the REST API.</summary>
public sealed class SofiaHub : Hub;

/// <summary>Forwards every bus event to every connected client on its own reader.</summary>
public sealed class HubBroadcaster(IEventBus bus, IHubContext<SofiaHub> hub, ILogger<HubBroadcaster> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = bus.Subscribe(stoppingToken);
        try
        {
            await foreach (var e in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await hub.Clients.All.SendAsync("event", new
                    {
                        type = e.Type,
                        sessionId = e.SessionId,
                        workspaceId = e.WorkspaceId,
                        data = e.Data,
                    }, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug(ex, "Broadcast of {Type} failed", e.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
