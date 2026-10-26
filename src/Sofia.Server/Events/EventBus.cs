using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Events;

/// <summary>
/// A domain event. <see cref="Data"/> is serialized when the event is published, so
/// later mutations of the source object never leak into an already published event.
/// </summary>
public sealed record SofiaEvent(string Type, string? SessionId, string? WorkspaceId, JsonElement Data);

public static class EventTypes
{
    public const string WorkspaceUpdated = "workspace.updated";
    public const string WorkspaceDeleted = "workspace.deleted";
    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string SessionDeleted = "session.deleted";
    public const string SessionStatus = "session.status";
    public const string SessionMetrics = "session.metrics";
    public const string MessageCreated = "message.created";
    public const string MessageUpdated = "message.updated";
    public const string MessageDelta = "message.delta";
    public const string MessageDeleted = "message.deleted";
    public const string ConfigChanged = "config.changed";
    public const string McpUpdated = "mcp.updated";
    public const string ModelsUpdated = "models.updated";
    public const string BrowserUpdated = "browser.updated";
}

public interface IEventBus
{
    void Publish(string type, object data, string? sessionId = null, string? workspaceId = null);
    ChannelReader<SofiaEvent> Subscribe(CancellationToken ct);
}

/// <summary>In-process fan-out bus: every subscriber gets its own unbounded channel.</summary>
public sealed class EventBus : IEventBus
{
    private readonly List<Channel<SofiaEvent>> _subscribers = [];
    private readonly object _gate = new();

    public void Publish(string type, object data, string? sessionId = null, string? workspaceId = null)
    {
        var element = JsonSerializer.SerializeToElement(data, data.GetType(), Json.Options);
        var e = new SofiaEvent(type, sessionId, workspaceId, element);
        lock (_gate)
        {
            foreach (var channel in _subscribers) channel.Writer.TryWrite(e);
        }
    }

    public ChannelReader<SofiaEvent> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SofiaEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        ct.Register(() =>
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }
}

/// <summary>Reacts to bus events. Handlers run sequentially on one background reader.</summary>
public interface IEventHandler
{
    Task HandleAsync(SofiaEvent e, CancellationToken ct);
}

public sealed class EventDispatcher(IEventBus bus, IEnumerable<IEventHandler> handlers, ILogger<EventDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = bus.Subscribe(stoppingToken);
        try
        {
            await foreach (var e in reader.ReadAllAsync(stoppingToken))
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        await handler.HandleAsync(e, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Event handler {Handler} failed on {Type}", handler.GetType().Name, e.Type);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}

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
