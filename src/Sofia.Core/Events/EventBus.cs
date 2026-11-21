using System.Text.Json;
using System.Threading.Channels;
using Sofia.Contracts.Events;
using Sofia.Contracts.Infrastructure;

namespace Sofia.Core.Events;

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
