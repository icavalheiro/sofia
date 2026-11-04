using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sofia.Acp;

public sealed class AcpAgent
{
    private static readonly (string Id, string Name)[] Modes =
    [
        ("read-only", "Read only"),
        ("ask", "Ask"),
        ("plan", "Plan"),
        ("full", "Full access"),
    ];

    private readonly AcpConnection _conn;
    private readonly ISofiaClient _client;
    private readonly ServerLauncher? _launcher;
    private readonly Dictionary<string, PromptState> _prompts = new();
    private readonly Dictionary<string, HashSet<string>> _emittedToolCalls = new();
    private readonly Dictionary<string, Dictionary<string, string>> _toolStatus = new();
    private readonly HashSet<string> _childSessions = new();
    private string? _clientName;

    public AcpAgent(AcpConnection connection, ISofiaClient client, ServerLauncher? launcher = null)
    {
        _conn = connection;
        _client = client;
        _launcher = launcher;
        _conn.RequestReceived += HandleRequestAsync;
        _conn.NotificationReceived += HandleNotificationAsync;
    }

    private async Task HandleRequestAsync(JsonRpcRequest req)
    {
        try
        {
            var result = await DispatchAsync(req.Method, req.Params);
            await _conn.WriteResultAsync(req.Id, result);
        }
        catch (JsonRpcException ex)
        {
            await _conn.WriteErrorAsync(req.Id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            await _conn.WriteErrorAsync(req.Id, -32603, ex.Message);
        }
    }

    private Task HandleNotificationAsync(JsonRpcNotification n)
    {
        if (n.Method == "session/cancel")
        {
            var sid = n.Params?["sessionId"]?.GetValue<string>();
            if (sid is not null) _ = CancelAsync(sid);
        }
        return Task.CompletedTask;
    }

    private async Task<object?> DispatchAsync(string method, JsonNode? @params)
    {
        return method switch
        {
            "initialize" => await InitializeAsync(@params),
            "authenticate" => new { },
            "session/new" => await SessionNewAsync(@params),
            "session/load" => await SessionLoadAsync(@params),
            "session/prompt" => await SessionPromptAsync(@params),
            "session/set_mode" => await SetModeAsync(@params),
            "session/set_config_option" => await SetConfigOptionAsync(@params),
            "session/set_model" => await SetModelAsync(@params),
            _ => throw new JsonRpcException(-32601, $"Method not found: {method}"),
        };
    }

    private async Task<object> InitializeAsync(JsonNode? @params)
    {
        if (_launcher is not null) await _launcher.EnsureAsync(CancellationToken.None);
        var clientInfo = @params?["clientInfo"];
        _clientName = clientInfo?["name"]?.GetValue<string>();
        // store capabilities (not strictly used in phase 1)
        return new
        {
            protocolVersion = 1,
            agentCapabilities = new
            {
                loadSession = true,
                promptCapabilities = new { image = true, embeddedContext = true },
                mcpCapabilities = new { http = true },
            },
            agentInfo = new { name = "Sofia", version = "0.1" },
            authMethods = Array.Empty<object>(),
        };
    }

    private async Task<object> SessionNewAsync(JsonNode? @params)
    {
        var cwd = @params?["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory;
        var workspace = await _client.FindOrCreateWorkspaceAsync(cwd, CancellationToken.None);
        var session = await _client.CreateSessionAsync(workspace.Id, null, "acp", CancellationToken.None);
        await RegisterMcpAsync(session.Id, @params?["mcpServers"]);
        var models = await _client.GetModelsAsync(CancellationToken.None);
        return BuildSessionResult(session, models);
    }

    private async Task<object> SessionLoadAsync(JsonNode? @params)
    {
        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "sessionId required");
        var session = await _client.GetSessionAsync(sessionId, CancellationToken.None);
        await RegisterMcpAsync(sessionId, @params?["mcpServers"]);
        // replay history
        var messages = await _client.GetMessagesAsync(sessionId, CancellationToken.None);
        foreach (var m in messages)
        {
            if (m.Role == "user")
            {
                await _conn.WriteNotificationAsync("session/update", new { sessionId, update = new { sessionUpdate = "user_message_chunk", content = new { type = "text", text = m.Text } } });
            }
            else
            {
                if (!string.IsNullOrEmpty(m.Reasoning))
                    await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentThoughtChunk(m.Reasoning) });
                if (!string.IsNullOrEmpty(m.Text))
                    await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk(m.Text) });
                if (m.ToolCalls is not null)
                {
                    foreach (var tc in m.ToolCalls)
                    {
                        var kind = EventMapper.KindForTool(tc.Name);
                        var args = TryParse(tc.Input);
                        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.ToolCall(tc.Id, tc.Name, kind, args, EventMapper.LocationsFromArgs(args as JsonObject)) });
                        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.ToolCallUpdate(tc.Id, tc.Status == "error" ? "failed" : "completed", tc.Output ?? tc.Error, null) });
                    }
                }
                if (m.Summary == true)
                {
                    await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk("[compacted summary]") });
                }
            }
        }
        var models = await _client.GetModelsAsync(CancellationToken.None);
        return BuildSessionResult(session, models);
    }

    private async Task<object> SessionPromptAsync(JsonNode? @params)
    {
        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "sessionId required");
        var promptBlocks = @params?["prompt"] as JsonArray;
        var text = ConvertPrompt(promptBlocks);
        var state = new PromptState(sessionId);
        _prompts[sessionId] = state;
        var tcs = state.Completion;
        using var sub = _client.SubscribeEvents(ev => HandleEventAsync(sessionId, ev, state));
        await _client.PromptAsync(sessionId, new PromptInputDto(text), CancellationToken.None);
        // wait for completion
        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            // cancelled
        }
        _prompts.Remove(sessionId);
        var reason = state.StopReason ?? "end_turn";
        if (state.Error is not null) throw new JsonRpcException(-32603, state.Error);
        return new { stopReason = reason };
    }

    private async Task CancelAsync(string sessionId)
    {
        try { await _client.AbortAsync(sessionId, CancellationToken.None); } catch { }
        if (_prompts.TryGetValue(sessionId, out var st))
        {
            st.StopReason = "cancelled";
            st.Completion.TrySetResult();
        }
    }

    private async Task<object> SetModeAsync(JsonNode? @params)
    {
        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "sessionId required");
        var modeId = @params?["modeId"]?.GetValue<string>() ?? @params?["mode"]?.GetValue<string>();
        if (modeId is null) throw new JsonRpcException(-32602, "modeId required");
        await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(PermissionMode: modeId), CancellationToken.None);
        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.CurrentModeUpdate(modeId) });
        return new { };
    }

    private async Task<object> SetConfigOptionAsync(JsonNode? @params)
    {
        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "sessionId required");
        var configId = @params?["configId"]?.GetValue<string>();
        var value = @params?["value"];
        var strVal = value is JsonValue v ? v.GetValue<string>() : value?.ToString();
        if (configId == "mode" && strVal is not null)
        {
            await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(PermissionMode: strVal), CancellationToken.None);
            await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.CurrentModeUpdate(strVal) });
        }
        else if (configId == "model" && strVal is not null)
        {
            await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(ModelId: strVal), CancellationToken.None);
        }
        else if (configId == "reasoning" && strVal is not null)
        {
            await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(ReasoningMode: strVal), CancellationToken.None);
        }
        else if (configId == "agent" && strVal is not null)
        {
            await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(Agent: strVal), CancellationToken.None);
        }
        return new { };
    }

    private async Task<object> SetModelAsync(JsonNode? @params)
    {
        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "sessionId required");
        var modelId = @params?["modelId"]?.GetValue<string>() ?? @params?["model"]?.GetValue<string>();
        if (modelId is null) throw new JsonRpcException(-32602, "modelId required");
        await _client.ApplySettingsAsync(sessionId, new UpdateSessionRequestDto(ModelId: modelId), CancellationToken.None);
        return new { };
    }

    private async Task RegisterMcpAsync(string sessionId, JsonNode? mcpServers)
    {
        if (mcpServers is not JsonArray arr) return;
        foreach (var s in arr)
        {
            var name = s?["name"]?.GetValue<string>() ?? "mcp";
            string transport = "command";
            string? url = null;
            string? command = null;
            List<string>? args = null;
            Dictionary<string, string>? env = null;
            if (s?["type"]?.GetValue<string>() == "http" || s?["url"] is not null)
            {
                transport = "web";
                url = s?["url"]?.GetValue<string>();
            }
            else
            {
                command = s?["command"]?.GetValue<string>();
                if (s?["args"] is JsonArray a) args = a.Select(x => x?.GetValue<string>()).OfType<string>().ToList();
                if (s?["env"] is JsonArray e)
                {
                    env = new();
                    foreach (var item in e)
                    {
                        var k = item?["name"]?.GetValue<string>() ?? item?["key"]?.GetValue<string>();
                        var v = item?["value"]?.GetValue<string>();
                        if (k is not null && v is not null) env[k] = v;
                    }
                }
            }
            try
            {
                await _client.AddSessionMcpAsync(sessionId, new SessionMcpRequestDto(name, transport, command, args, url, null, env), CancellationToken.None);
            }
            catch { }
        }
    }

    private async Task HandleEventAsync(string sessionId, SofiaEvent ev, PromptState state)
    {
        // track child sessions
        if (ev.Type == "session.created" || ev.Type == "session.updated")
        {
            try
            {
                var pid = ev.Data.TryGetProperty("parentId", out var p) ? p.GetString() : null;
                if (pid == sessionId && ev.SessionId is not null) _childSessions.Add(ev.SessionId);
            }
            catch { }
        }

        var relevant = ev.SessionId == sessionId || (ev.SessionId is not null && _childSessions.Contains(ev.SessionId));
        if (!relevant) return;

        switch (ev.Type)
        {
            case "message.delta":
                {
                    var msgId = ev.Data.TryGetProperty("messageId", out var m) ? m.GetString() : null;
                    var field = ev.Data.TryGetProperty("field", out var f) ? f.GetString() : null;
                    var delta = ev.Data.TryGetProperty("delta", out var d) ? d.GetString() : null;
                    if (delta is null) break;
                    if (field == "text")
                        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk(delta) });
                    else if (field == "reasoning")
                        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentThoughtChunk(delta) });
                    break;
                }
            case "message.created":
            case "message.updated":
                await HandleMessageEventAsync(sessionId, ev);
                break;
            case "tasks.updated":
                await HandleTasksAsync(sessionId, ev);
                break;
            case "session.updated":
                await HandleSessionUpdatedAsync(sessionId, ev);
                break;
            case "session.status":
                {
                    var status = ev.Data.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (status == "idle")
                    {
                        state.StopReason = "end_turn";
                        state.Completion.TrySetResult();
                    }
                    else if (status == "error")
                    {
                        state.Error = ev.Data.TryGetProperty("error", out var e) ? e.GetString() : "error";
                        state.Completion.TrySetResult();
                    }
                    break;
                }
        }
    }

    private async Task HandleMessageEventAsync(string sessionId, SofiaEvent ev)
    {
        MessageDto? msg = null;
        try { msg = JsonSerializer.Deserialize<MessageDto>(ev.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { }
        if (msg is null) return;

        // permission card
        if (msg.Permission is not null && string.IsNullOrEmpty(msg.Permission.Resolved))
        {
            await HandlePermissionAsync(sessionId, msg.Permission);
            return;
        }
        // question card
        if (msg.Question is not null && string.IsNullOrEmpty(msg.Question.Resolved))
        {
            await HandleQuestionAsync(sessionId, msg.Question);
            return;
        }
        if (msg.QuestionSet is not null && msg.QuestionSet.Confirmed is null)
        {
            await HandleQuestionSetAsync(sessionId, msg.QuestionSet);
            return;
        }

        // tool calls
        if (msg.ToolCalls is not null)
        {
            _emittedToolCalls.TryAdd(sessionId, new HashSet<string>());
            _toolStatus.TryAdd(sessionId, new Dictionary<string, string>());
            foreach (var tc in msg.ToolCalls)
            {
                var seen = _emittedToolCalls[sessionId];
                var args = TryParse(tc.Input) as JsonObject;
                if (!seen.Contains(tc.Id))
                {
                    seen.Add(tc.Id);
                    var kind = EventMapper.KindForTool(tc.Name);
                    await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.ToolCall(tc.Id, tc.Name, kind, args is null ? null : JsonNode.Parse(args.ToJsonString()), EventMapper.LocationsFromArgs(args)) });
                }
                // status update
                var prev = _toolStatus[sessionId].TryGetValue(tc.Id, out var p) ? p : null;
                var acpStatus = tc.Status == "success" ? "completed" : tc.Status == "error" ? "failed" : "in_progress";
                if (prev != acpStatus)
                {
                    _toolStatus[sessionId][tc.Id] = acpStatus;
                    JsonObject? diff = null;
                    if (tc.Status == "success" && (tc.Name == "write" || tc.Name == "edit"))
                        diff = EventMapper.DiffFromArgs(args);
                    await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.ToolCallUpdate(tc.Id, acpStatus, tc.Output ?? tc.Error, diff) });
                }
            }
        }

        if (msg.Summary == true)
        {
            await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk("(context compacted)") });
        }
    }

    private async Task HandlePermissionAsync(string sessionId, PermissionCardDto card)
    {
        var options = new JsonArray
        {
            new JsonObject { ["optionId"] = "allow_once", ["name"] = "Allow once", ["kind"] = "allow_once" },
            new JsonObject { ["optionId"] = "allow_always", ["name"] = "Allow for this session", ["kind"] = "allow_always" },
            new JsonObject { ["optionId"] = "reject_once", ["name"] = "Deny", ["kind"] = "reject_once" },
        };
        JsonNode? resp = null;
        try
        {
            resp = await _conn.SendRequestAsync("session/request_permission", new
            {
                sessionId,
                toolCall = new { toolCallId = card.RequestId },
                options,
            }, CancellationToken.None);
        }
        catch { }
        var outcome = resp?["outcome"]?["optionId"]?.GetValue<string>() ?? resp?["optionId"]?.GetValue<string>();
        string decision = outcome switch
        {
            "allow_once" => "Allow once",
            "allow_always" => "Allow for this session",
            "reject_once" => "Deny",
            _ => "Deny",
        };
        try { await _client.ReplyPermissionAsync(sessionId, card.RequestId, decision, CancellationToken.None); } catch { }
    }

    private async Task HandleQuestionAsync(string sessionId, QuestionCardDto q)
    {
        if (q.Options is { Count: > 0 })
        {
            var options = new JsonArray();
            for (var i = 0; i < q.Options.Count; i++)
                options.Add(new JsonObject { ["optionId"] = i.ToString(), ["name"] = q.Options[i], ["kind"] = "allow_once" });
            JsonNode? resp = null;
            try { resp = await _conn.SendRequestAsync("session/request_permission", new { sessionId, options }, CancellationToken.None); } catch { }
            var outcome = resp?["outcome"]?["optionId"]?.GetValue<string>() ?? resp?["optionId"]?.GetValue<string>();
            if (outcome is not null && int.TryParse(outcome, out var idx) && idx >= 0 && idx < q.Options.Count)
            {
                try { await _client.AnswerQuestionAsync(sessionId, q.FormId, new List<JsonElement?> { JsonSerializer.SerializeToElement(q.Options[idx]) }, CancellationToken.None); } catch { }
            }
        }
        else
        {
            // degrade: notify model/editor cannot answer
            await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk($"(question: {q.Prompt})") });
        }
    }

    private async Task HandleQuestionSetAsync(string sessionId, QuestionSetCardDto qs)
    {
        await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.AgentMessageChunk($"(questions: {qs.Questions.Count})") });
    }

    private async Task HandleTasksAsync(string sessionId, SofiaEvent ev)
    {
        try
        {
            if (!ev.Data.TryGetProperty("tasks", out var tasks)) return;
            var entries = new JsonArray();
            foreach (var t in tasks.EnumerateArray())
            {
                var status = t.TryGetProperty("status", out var st) ? st.GetString() : "pending";
                if (status == "cancelled") continue;
                var title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                var acpStatus = status switch
                {
                    "completed" => "completed",
                    "in_progress" => "in_progress",
                    _ => "pending",
                };
                entries.Add(new JsonObject { ["content"] = title, ["priority"] = "medium", ["status"] = acpStatus });
            }
            await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.Plan(entries) });
        }
        catch { }
    }

    private async Task HandleSessionUpdatedAsync(string sessionId, SofiaEvent ev)
    {
        try
        {
            var mode = ev.Data.TryGetProperty("permissionMode", out var pm) ? pm.GetString() : null;
            if (mode is not null)
                await _conn.WriteNotificationAsync("session/update", new { sessionId, update = EventMapper.CurrentModeUpdate(mode) });
        }
        catch { }
    }

    private static string ConvertPrompt(JsonArray? blocks)
    {
        if (blocks is null) return "";
        var parts = new List<string>();
        foreach (var b in blocks)
        {
            var type = b?["type"]?.GetValue<string>();
            if (type == "text")
            {
                parts.Add(b?["text"]?.GetValue<string>() ?? "");
            }
            else if (type == "resource" || type == "resource_link")
            {
                var uri = b?["uri"]?.GetValue<string>() ?? b?["resource"]?["uri"]?.GetValue<string>();
                var text = b?["resource"]?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add($"```\n{text}\n```");
                }
                else if (!string.IsNullOrEmpty(uri))
                {
                    parts.Add($"@{uri}");
                }
            }
        }
        return string.Join("\n", parts);
    }

    private object BuildSessionResult(SessionDto session, List<ModelDto> models)
    {
        var modeId = session.PermissionMode ?? "ask";
        var reasoning = session.ReasoningMode ?? "medium";
        var availableModes = Modes.Select(m => new { id = m.Id, name = m.Name }).ToArray();
        var modelOptions = models.Select(m => new { id = m.Id, name = m.Name }).ToArray();
        var reasoningOptions = new[] { "off", "low", "medium", "high" }.Select(x => new { id = x, name = x }).ToArray();
        return new
        {
            sessionId = session.Id,
            modes = new { currentModeId = modeId, availableModes },
            configOptions = new object[]
            {
                new { id = "mode", name = "Mode", category = "mode", type = "select", currentValue = modeId, options = availableModes },
                new { id = "model", name = "Model", category = "model", type = "select", currentValue = session.ModelId, options = modelOptions },
                new { id = "reasoning", name = "Reasoning", category = "reasoning", type = "select", currentValue = reasoning, options = reasoningOptions },
            },
            models = new { currentModelId = session.ModelId, availableModels = modelOptions },
        };
    }

    private static JsonNode? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonNode.Parse(s); } catch { return JsonValue.Create(s); }
    }

    private sealed class PromptState(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StopReason { get; set; }
        public string? Error { get; set; }
    }
}
