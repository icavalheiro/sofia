using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Changes;
using Sofia.Server.Domain;

namespace Sofia.Server.Tools;

public enum ToolKind
{
    /// <summary>Reads local state; always allowed.</summary>
    Read,
    /// <summary>Modifies files.</summary>
    Write,
    /// <summary>Runs arbitrary commands.</summary>
    Execute,
    /// <summary>Reads from the network.</summary>
    Network,
    /// <summary>Drives the live browser (can click, type and submit on real sites); asks for approval like writes do.</summary>
    Browser,
    /// <summary>Talks to the user.</summary>
    Interactive,
    /// <summary>Spawns subagents.</summary>
    Agent,
    /// <summary>Third-party (MCP) tool with unknown side effects.</summary>
    External,
}

public sealed record ToolResult(string Output, bool IsError = false, List<Attachment>? Images = null)
{
    public static ToolResult Ok(string output) => new(output);
    public static ToolResult Fail(string error) => new(error, true);
    public static ToolResult WithImages(string output, List<Attachment> images) => new(output, false, images);
}

public sealed record QuestionRequest(string Prompt, List<string> Options, bool Multi);

/// <summary>Services a tool can call back into while a run is executing.</summary>
public interface IToolHost
{
    /// <summary>Shows the question cards on the message that made the call, so they read as part of that reply.</summary>
    Task<List<string>> AskAsync(ToolContext ctx, List<QuestionRequest> questions, CancellationToken ct);
    Task<string> RunSubagentAsync(ToolContext ctx, string agent, string description, string prompt, CancellationToken ct);
    void ReportProgress(ToolCall call, string chunk);
}

public sealed class ToolContext
{
    public required Session Session { get; init; }
    public required Workspace Workspace { get; init; }
    public required string Cwd { get; init; }
    public required Message Message { get; init; }
    public required ToolCall Call { get; init; }
    public required ServerConfig Config { get; init; }
    public required ChangeTracker Changes { get; init; }
    public required IToolHost Host { get; init; }
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonObject Parameters { get; }
    ToolKind Kind { get; }
    string Summarize(JsonObject args);
    Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct);
}

public sealed class ToolArgumentException(string message) : Exception(message);

public static class Args
{
    public static JsonObject Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return ParseLenient(raw) as JsonObject ?? throw new ToolArgumentException("Tool arguments must be a JSON object.");
        }
        catch (JsonException ex)
        {
            // Small local models sometimes wrap the JSON in prose or code fences.
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    if (ParseLenient(raw[start..(end + 1)]) is JsonObject recovered) return recovered;
                }
                catch (JsonException)
                {
                    // Fall through to the original error.
                }
            }
            throw new ToolArgumentException($"Invalid JSON arguments: {ex.Message}");
        }
    }

    // Models sometimes emit duplicate keys ({"limit":80,"limit":0}); JsonNode.Parse throws ArgumentException for
    // those, so the last value wins instead of failing the whole call.
    private static JsonNode? ParseLenient(string raw)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (ArgumentException)
        {
            using var doc = JsonDocument.Parse(raw);
            return Copy(doc.RootElement);
        }

        static JsonNode? Copy(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new JsonObject();
                    foreach (var p in e.EnumerateObject()) obj[p.Name] = Copy(p.Value);
                    return obj;
                case JsonValueKind.Array:
                    return new JsonArray(e.EnumerateArray().Select(Copy).ToArray());
                case JsonValueKind.Null:
                    return null;
                default:
                    return JsonValue.Create(e.Clone());
            }
        }
    }

    public static string Required(JsonObject args, string name, params string[] aliases)
    {
        var value = Optional(args, name, aliases);
        if (string.IsNullOrEmpty(value)) throw new ToolArgumentException($"Missing required argument '{name}'.");
        return value;
    }

    public static string? Optional(JsonObject args, string name, params string[] aliases)
    {
        foreach (var key in aliases.Prepend(name))
        {
            if (args[key] is not JsonNode node) continue;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return node.ToJsonString();
        }
        return null;
    }

    /// <summary>Like <see cref="Optional"/> but keeps an explicitly empty string (edit's newText).</summary>
    public static string? Raw(JsonObject args, string name, params string[] aliases)
    {
        foreach (var key in aliases.Prepend(name))
            if (args[key] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    public static int? Int(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    public static bool Bool(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p;
    }
}

/// <summary>Tiny JSON-schema builder for tool parameter definitions.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in props)
        {
            properties[name] = schema;
            if (isRequired) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    public static JsonObject Array(string description, JsonObject items) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items };
}
