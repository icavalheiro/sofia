using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>OpenAI Chat Completions wire protocol (OpenAI, local runtimes, OpenRouter, Gemini, DeepSeek, …).</summary>
public sealed class OpenAiChatClient(HttpClient http, ProviderConfig provider, ProviderAuthResolver? authResolver = null) : IChatClient
{
    private static readonly HashSet<string> LocalFlavors = ["unsloth", "ollama", "llamacpp", "vllm", "lmstudio", "local"];
    // Thinking models on these flavors reject a follow-up request that drops the earlier reasoning_content.
    private static readonly HashSet<string> ReasoningEchoFlavors = ["deepseek", "opencode-go"];

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(request);
        var auth = authResolver is null ? RequestAuth.From(provider) : await authResolver(ct);
        var baseUrl = (auth.BaseUrl ?? provider.BaseUrl).TrimEnd('/');
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(auth.Key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Key);
        foreach (var (name, value) in auth.Headers ?? new Dictionary<string, string>()) ProviderIdentity.ApplyHeader(message, name, value);
        ProviderIdentity.ApplyIdentity(message, provider, request.SessionId);
        if (provider.Flavor == "openrouter")
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/icavalheiro/sofia");
            message.Headers.TryAddWithoutValidation("X-Title", "Sofia");
        }
        else if (provider.Flavor == "copilot")
        {
            message.Headers.TryAddWithoutValidation("X-Initiator", request.Messages.LastOrDefault()?.Role == "user" ? "user" : "agent");
            if (request.Messages.Any(m => m.Content.Any(c => c.ImageBase64 is not null)))
                message.Headers.TryAddWithoutValidation("Copilot-Vision-Request", "true");
        }
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

            var splitter = new ThinkTagSplitter();
            var toolIndexes = new Dictionary<int, string>();
            var finish = "stop";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var (_, data) in Sse.ReadAsync(stream, ct))
            {
                if (data == "[DONE]") break;
                JsonNode? chunk;
                try
                {
                    chunk = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (chunk is null) continue;

                if (chunk["error"] is JsonNode error)
                    throw new ProviderException($"{provider.Name}: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");

                if (chunk["usage"] is JsonObject usage && usage.Count > 0)
                    yield return new UsageReported(ParseUsage(usage, chunk["timings"]), chunk["timings"]?["predicted_per_second"]?.GetValue<double>());

                if (chunk["choices"] is not JsonArray choices || choices.Count == 0) continue;
                var choice = choices[0]!;
                var delta = choice["delta"];
                if (delta is not null)
                {
                    var reasoning = Str(delta["reasoning_content"]) ?? Str(delta["reasoning"]);
                    if (!string.IsNullOrEmpty(reasoning)) yield return new ReasoningDelta(reasoning);

                    var content = Str(delta["content"]);
                    if (!string.IsNullOrEmpty(content))
                        foreach (var e in splitter.Push(content)) yield return e;

                    if (delta["tool_calls"] is JsonArray calls)
                    {
                        foreach (var call in calls)
                        {
                            if (call is null) continue;
                            var index = call["index"]?.GetValue<int>() ?? 0;
                            var fn = call["function"];
                            if (!toolIndexes.ContainsKey(index))
                            {
                                var id = Str(call["id"]);
                                var name = Str(fn?["name"]);
                                if (string.IsNullOrEmpty(name)) continue;
                                if (string.IsNullOrEmpty(id)) id = $"call_{Guid.NewGuid():N}"[..24];
                                toolIndexes[index] = id;
                                yield return new ToolCallStarted(index, id, name);
                            }
                            var args = Str(fn?["arguments"]);
                            if (!string.IsNullOrEmpty(args)) yield return new ToolCallArgumentsDelta(index, args);
                        }
                    }
                }
                var reason = Str(choice["finish_reason"]);
                if (!string.IsNullOrEmpty(reason)) finish = reason;
            }
            foreach (var e in splitter.Flush()) yield return e;
            yield return new StreamFinished(finish);
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Usage ParseUsage(JsonObject usage, JsonNode? timings)
    {
        long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        var cached = L(usage["prompt_tokens_details"]?["cached_tokens"]);
        // llama.cpp reports the reused prompt prefix as timings.cache_n.
        if (cached == 0) cached = L(timings?["cache_n"]);
        return new Usage
        {
            Input = L(usage["prompt_tokens"]),
            Output = L(usage["completion_tokens"]),
            Reasoning = L(usage["completion_tokens_details"]?["reasoning_tokens"]),
            CacheRead = cached,
        };
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "tool":
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId,
                        ["content"] = m.TextContent,
                    });
                    break;
                case "assistant":
                {
                    var obj = new JsonObject { ["role"] = "assistant" };
                    var text = m.TextContent;
                    obj["content"] = string.IsNullOrEmpty(text) ? null : text;
                    if (!string.IsNullOrEmpty(m.Reasoning) && ReasoningEchoFlavors.Contains(provider.Flavor)) obj["reasoning_content"] = m.Reasoning;
                    if (m.ToolCalls.Count > 0)
                    {
                        obj["tool_calls"] = new JsonArray(m.ToolCalls.Select(tc => (JsonNode)new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments,
                            },
                        }).ToArray());
                    }
                    messages.Add(obj);
                    break;
                }
                default:
                {
                    var hasImages = m.Content.Any(c => c.ImageBase64 is not null);
                    if (!hasImages)
                    {
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.TextContent });
                        break;
                    }
                    var parts = new JsonArray();
                    foreach (var part in m.Content)
                    {
                        if (part.ImageBase64 is not null)
                        {
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = $"data:{part.ImageMime};base64,{part.ImageBase64}" },
                            });
                        }
                        else if (!string.IsNullOrEmpty(part.Text))
                        {
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = part.Text });
                        }
                    }
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    break;
                }
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        if (provider.Flavor == "openai") body["max_completion_tokens"] = request.MaxTokens;
        else body["max_tokens"] = request.MaxTokens;

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            }).ToArray());
        }

        ApplyReasoning(body, request);
        return body;
    }

    private void ApplyReasoning(JsonObject body, ChatRequest request)
    {
        var off = request.Reasoning == "off";
        if (LocalFlavors.Contains(provider.Flavor))
        {
            // Hybrid local models (Qwen3, Nemotron, …) read the switch from the chat template.
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = !off };
            if (provider.Flavor == "ollama") body["think"] = !off;
            return;
        }
        if (!request.ModelSupportsReasoning) return;
        switch (provider.Flavor)
        {
            case "openai":
                if (!off) body["reasoning_effort"] = request.Reasoning;
                break;
            case "openrouter":
                body["reasoning"] = off ? new JsonObject { ["enabled"] = false } : new JsonObject { ["effort"] = request.Reasoning };
                break;
            case "deepseek":
                body["thinking"] = new JsonObject { ["type"] = off ? "disabled" : "enabled" };
                if (!off) body["reasoning_effort"] = request.Reasoning;
                break;
            case "qwencloud":
                body["enable_thinking"] = !off;
                break;
            case "gemini":
            case "xai":
            case "groq":
                if (!off) body["reasoning_effort"] = request.Reasoning;
                break;
        }
    }
}
