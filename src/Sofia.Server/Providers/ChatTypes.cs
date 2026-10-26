using System.Text.Json.Nodes;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

public sealed class ChatRequest
{
    public required string Model { get; init; }
    public string System { get; init; } = "";
    public List<ChatMessage> Messages { get; init; } = [];
    public List<ToolSpec> Tools { get; init; } = [];
    /// <summary>'off' | 'low' | 'medium' | 'high'.</summary>
    public string Reasoning { get; init; } = "medium";
    public int MaxTokens { get; init; } = 8192;
    public bool ModelSupportsReasoning { get; init; }
    /// <summary>Conversation id, sent to providers that route and cache per session (OpenCode Go).</summary>
    public string? SessionId { get; init; }
}

public sealed class ChatMessage
{
    /// <summary>'user' | 'assistant' | 'tool'.</summary>
    public required string Role { get; init; }
    public List<ContentPart> Content { get; init; } = [];
    public List<ToolCallSpec> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    public string? Reasoning { get; init; }
    public string? ReasoningSignature { get; init; }

    public string TextContent => string.Concat(Content.Where(c => c.Text is not null).Select(c => c.Text));

    public static ChatMessage UserText(string text) => new() { Role = "user", Content = [ContentPart.FromText(text)] };
}

public sealed class ContentPart
{
    public string? Text { get; init; }
    public string? ImageBase64 { get; init; }
    public string? ImageMime { get; init; }

    public static ContentPart FromText(string text) => new() { Text = text };
}

public sealed record ToolCallSpec(string Id, string Name, string Arguments);

public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public abstract record StreamEvent;
public sealed record TextDelta(string Text) : StreamEvent;
public sealed record ReasoningDelta(string Text) : StreamEvent;
public sealed record ReasoningSignature(string Signature) : StreamEvent;
public sealed record ToolCallStarted(int Index, string Id, string Name) : StreamEvent;
public sealed record ToolCallArgumentsDelta(int Index, string Delta) : StreamEvent;
public sealed record UsageReported(Usage Usage, double? TokensPerSecond) : StreamEvent;
public sealed record StreamFinished(string Reason) : StreamEvent;

public sealed class ProviderException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

public interface IChatClient
{
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}

public sealed record DiscoveredModel(
    string Id,
    string Label,
    int ContextLength,
    int MaxOutput,
    bool Reasoning,
    bool Vision,
    bool? Loaded);
