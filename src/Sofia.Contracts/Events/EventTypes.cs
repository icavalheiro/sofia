using System.Text.Json;

namespace Sofia.Contracts.Events;

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
    public const string TasksUpdated = "tasks.updated";
}
