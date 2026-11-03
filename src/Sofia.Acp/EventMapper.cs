using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sofia.Acp;

/// <summary>Pure mapping from Sofia events to ACP <c>session/update</c> payloads.</summary>
public static class EventMapper
{
    public static string ToolKindCorrect(string? toolName)
    {
        var n = (toolName ?? "").ToLowerInvariant();
        return n switch
        {
            "read" or "ls" => "read",
            "write" or "edit" => "edit",
            "grep" or "find" => "search",
            "bash" or "processes" or "shell" => "execute",
            "web_fetch" or "web_search" or "browser" => "fetch",
            _ => "other",
        };
    }

    public static JsonObject AgentMessageChunk(string text) => new()
    {
        ["sessionUpdate"] = "agent_message_chunk",
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    };

    public static JsonObject AgentThoughtChunk(string text) => new()
    {
        ["sessionUpdate"] = "agent_thought_chunk",
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    };

    public static JsonObject ToolCall(string toolCallId, string title, string kind, JsonNode? rawInput, JsonArray? locations) => new()
    {
        ["sessionUpdate"] = "tool_call",
        ["toolCallId"] = toolCallId,
        ["title"] = title,
        ["kind"] = kind,
        ["rawInput"] = rawInput?.DeepClone(),
        ["locations"] = locations?.DeepClone(),
        ["status"] = "in_progress",
    };

    public static JsonObject ToolCallUpdate(string toolCallId, string status, string? contentText, JsonObject? diff) => new()
    {
        ["sessionUpdate"] = "tool_call_update",
        ["toolCallId"] = toolCallId,
        ["status"] = status,
        ["content"] = contentText is null ? null : new JsonArray { new JsonObject { ["type"] = "text", ["text"] = contentText } },
        ["diff"] = diff?.DeepClone(),
    };

    public static JsonObject Plan(JsonArray entries) => new()
    {
        ["sessionUpdate"] = "plan",
        ["entries"] = entries.DeepClone(),
    };

    public static JsonObject CurrentModeUpdate(string mode) => new()
    {
        ["sessionUpdate"] = "current_mode_update",
        ["currentModeId"] = mode,
    };

    /// <summary>Extracts a tool kind from a Sofia tool name.</summary>
    public static string KindForTool(string? toolName) => ToolKindCorrect(toolName);

    /// <summary>Alias for <see cref="KindForTool"/>.</summary>
    public static string ToolKind(string? toolName) => KindForTool(toolName);

    /// <summary>Maps a Sofia event type + data to an ACP session/update payload (or null).</summary>
    public static JsonObject? Map(string type, JsonElement data)
    {
        try
        {
            switch (type)
            {
                case "message.delta":
                    {
                        var field = data.TryGetProperty("field", out var f) ? f.GetString() : null;
                        var delta = data.TryGetProperty("delta", out var d) ? d.GetString() : null;
                        if (delta is null) return null;
                        return field == "reasoning" ? AgentThoughtChunk(delta) : (field == "text" ? AgentMessageChunk(delta) : null);
                    }
                case "tasks.updated":
                    {
                        if (!data.TryGetProperty("tasks", out var tasks)) return null;
                        var entries = new JsonArray();
                        foreach (var t in tasks.EnumerateArray())
                        {
                            var status = t.TryGetProperty("status", out var st) ? st.GetString() : "pending";
                            if (status == "cancelled") continue;
                            var title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                            var acpStatus = status switch { "completed" => "completed", "in_progress" => "in_progress", _ => "pending" };
                            entries.Add(new JsonObject { ["content"] = title, ["priority"] = "medium", ["status"] = acpStatus });
                        }
                        return Plan(entries);
                    }
                case "session.updated":
                    {
                        if (data.TryGetProperty("permissionMode", out var pm) && pm.ValueKind == JsonValueKind.String)
                            return CurrentModeUpdate(pm.GetString()!);
                        return null;
                    }
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    public static JsonObject? Map(SofiaEvent ev) => Map(ev.Type, ev.Data);
    public static JsonObject? ToSessionUpdate(string type, JsonElement data) => Map(type, data);
    public static JsonObject? FromEvent(SofiaEvent ev) => Map(ev);

    /// <summary>Builds locations array from an args object containing a "path" key.</summary>
    public static JsonArray? LocationsFromArgs(JsonObject? args)
    {
        if (args is null) return null;
        if (!args.TryGetPropertyValue("path", out var p) || p is null) return null;
        var path = p.GetValue<string>();
        return new JsonArray { new JsonObject { ["path"] = path } };
    }

    /// <summary>Builds a diff object for write/edit tool calls.</summary>
    public static JsonObject? DiffFromArgs(JsonObject? args)
    {
        if (args is null) return null;
        string? path = args["path"]?.GetValue<string>();
        if (path is null) return null;
        return new JsonObject
        {
            ["type"] = "diff",
            ["path"] = path,
            ["oldText"] = args["oldText"]?.GetValue<string>() ?? args["old_string"]?.GetValue<string>(),
            ["newText"] = args["newText"]?.GetValue<string>() ?? args["new_string"]?.GetValue<string>() ?? args["content"]?.GetValue<string>(),
        };
    }
}
