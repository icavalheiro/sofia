using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>Anthropic Messages API (streaming, tools, extended thinking).</summary>
public sealed class AnthropicChatClient(HttpClient http, ProviderConfig provider) : IChatClient
{
    private static readonly Dictionary<string, int> ThinkingBudget = new()
    {
        ["low"] = 2048,
        ["medium"] = 8192,
        ["high"] = 24576,
    };

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(request);
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/messages" : $"{baseUrl}/v1/messages";
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var key = provider.ResolveKey();
        if (!string.IsNullOrEmpty(key))
        {
            message.Headers.TryAddWithoutValidation("x-api-key", key);
            // Anthropic-compatible gateways often expect a bearer token instead.
            if (!baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
                message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        }
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        ProviderIdentity.ApplyIdentity(message, provider, request.SessionId);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"Could not reach {provider.Name} at {provider.BaseUrl}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"{provider.Name}: {await Sse.ErrorMessageAsync(response, ct)}", (int)response.StatusCode);

            var usage = new Usage();
            var blockKinds = new Dictionary<int, string>();
            var toolIndex = 0;
            var toolIndexes = new Dictionary<int, int>();
            var finish = "stop";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var (eventName, data) in Sse.ReadAsync(stream, ct))
            {
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (node is null) continue;
                var type = node["type"]?.GetValue<string>() ?? eventName;
                switch (type)
                {
                    case "message_start":
                    {
                        var u = node["message"]?["usage"];
                        usage.Input = Long(u?["input_tokens"]);
                        usage.CacheRead = Long(u?["cache_read_input_tokens"]);
                        usage.CacheWrite = Long(u?["cache_creation_input_tokens"]);
                        usage.Output = Long(u?["output_tokens"]);
                        break;
                    }
                    case "content_block_start":
                    {
                        var index = node["index"]?.GetValue<int>() ?? 0;
                        var block = node["content_block"];
                        var kind = block?["type"]?.GetValue<string>() ?? "text";
                        blockKinds[index] = kind;
                        if (kind == "tool_use")
                        {
                            toolIndexes[index] = toolIndex;
                            yield return new ToolCallStarted(toolIndex++, block?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                                block?["name"]?.GetValue<string>() ?? "unknown");
                        }
                        else if (kind == "text" && block?["text"]?.GetValue<string>() is { Length: > 0 } t)
                        {
                            yield return new TextDelta(t);
                        }
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = node["index"]?.GetValue<int>() ?? 0;
                        var delta = node["delta"];
                        switch (delta?["type"]?.GetValue<string>())
                        {
                            case "text_delta":
                                yield return new TextDelta(delta["text"]?.GetValue<string>() ?? "");
                                break;
                            case "thinking_delta":
                                yield return new ReasoningDelta(delta["thinking"]?.GetValue<string>() ?? "");
                                break;
                            case "signature_delta":
                                yield return new ReasoningSignature(delta["signature"]?.GetValue<string>() ?? "");
                                break;
                            case "input_json_delta":
                                if (toolIndexes.TryGetValue(index, out var ti))
                                    yield return new ToolCallArgumentsDelta(ti, delta["partial_json"]?.GetValue<string>() ?? "");
                                break;
                        }
                        break;
                    }
                    case "message_delta":
                    {
                        var u = node["usage"];
                        if (u?["output_tokens"] is not null) usage.Output = Long(u["output_tokens"]);
                        if (u?["input_tokens"] is not null) usage.Input = Long(u["input_tokens"]);
                        if (u?["cache_read_input_tokens"] is not null) usage.CacheRead = Long(u["cache_read_input_tokens"]);
                        if (u?["cache_creation_input_tokens"] is not null) usage.CacheWrite = Long(u["cache_creation_input_tokens"]);
                        var stop = node["delta"]?["stop_reason"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(stop)) finish = stop == "tool_use" ? "tool_calls" : stop;
                        break;
                    }
                    case "error":
                        throw new ProviderException($"{provider.Name}: {node["error"]?["message"]?.GetValue<string>() ?? data}");
                }
            }
            // Anthropic's input_tokens excludes cached tokens; Usage.Input is the whole prompt (as in OpenAI).
            usage.Input += usage.CacheRead + usage.CacheWrite;
            yield return new UsageReported(usage, null);
            yield return new StreamFinished(finish);
        }
    }

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;

    internal JsonObject BuildBody(ChatRequest request)
    {
        var thinking = request.ModelSupportsReasoning && request.Reasoning != "off" && ThinkingBudget.TryGetValue(request.Reasoning, out var b) ? b : 0;
        var maxTokens = Math.Max(request.MaxTokens, thinking + 1024);

        var messages = new JsonArray();
        JsonObject? last = null;
        foreach (var m in request.Messages)
        {
            var role = m.Role == "assistant" ? "assistant" : "user";
            var blocks = new JsonArray();
            if (m.Role == "tool")
            {
                blocks.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.TextContent.Length == 0 ? "(no output)" : m.TextContent,
                    ["is_error"] = m.IsError,
                });
            }
            else if (m.Role == "assistant")
            {
                if (thinking > 0 && !string.IsNullOrEmpty(m.Reasoning) && !string.IsNullOrEmpty(m.ReasoningSignature))
                    blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = m.Reasoning, ["signature"] = m.ReasoningSignature });
                var text = m.TextContent;
                if (!string.IsNullOrEmpty(text)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                foreach (var tc in m.ToolCalls)
                {
                    JsonNode? input;
                    try
                    {
                        input = JsonNode.Parse(string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments);
                    }
                    catch (JsonException)
                    {
                        input = new JsonObject();
                    }
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id,
                        ["name"] = tc.Name,
                        ["input"] = input as JsonObject ?? new JsonObject(),
                    });
                }
            }
            else
            {
                foreach (var part in m.Content)
                {
                    if (part.ImageBase64 is not null)
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = part.ImageMime, ["data"] = part.ImageBase64 },
                        });
                    }
                    else if (!string.IsNullOrEmpty(part.Text))
                    {
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = part.Text });
                    }
                }
            }
            if (blocks.Count == 0) continue;

            // The API requires alternating roles: merge consecutive same-role turns.
            if (last is not null && last["role"]!.GetValue<string>() == role)
            {
                var existing = (JsonArray)last["content"]!;
                foreach (var block in blocks.ToList())
                {
                    blocks.Remove(block);
                    existing.Add(block);
                }
            }
            else
            {
                last = new JsonObject { ["role"] = role, ["content"] = blocks };
                messages.Add(last);
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["messages"] = messages,
        };
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            body["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            });
        }
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Parameters.DeepClone(),
            }).ToArray());
        }
        if (thinking > 0) body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = thinking };
        return body;
    }
}
