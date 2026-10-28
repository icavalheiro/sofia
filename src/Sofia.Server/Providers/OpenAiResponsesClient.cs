using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>OpenAI Responses wire protocol (<c>/responses</c>), used by models that are not served on Chat Completions (e.g. Copilot GPT-5 / codex).</summary>
public sealed class OpenAiResponsesClient(HttpClient http, ProviderConfig provider, ProviderAuthResolver? authResolver = null) : IChatClient
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(request);
        var auth = authResolver is null ? RequestAuth.From(provider) : await authResolver(ct);
        var baseUrl = (auth.BaseUrl ?? provider.BaseUrl).TrimEnd('/');
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(auth.Key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Key);
        foreach (var (name, value) in auth.Headers ?? new Dictionary<string, string>()) ProviderIdentity.ApplyHeader(message, name, value);
        ProviderIdentity.ApplyIdentity(message, provider, request.SessionId);
        if (provider.Flavor == "copilot")
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

            var toolIndexes = new Dictionary<int, int>(); // output_index -> tool call index
            var finish = "stop";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var (name, data) in Sse.ReadAsync(stream, ct))
            {
                if (data == "[DONE]") break;
                JsonNode? evt;
                try
                {
                    evt = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (evt is null) continue;
                var type = Str(evt["type"]) ?? name;

                switch (type)
                {
                    case "response.output_text.delta":
                        if (Str(evt["delta"]) is { Length: > 0 } text) yield return new TextDelta(text);
                        break;
                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta":
                        if (Str(evt["delta"]) is { Length: > 0 } reasoning) yield return new ReasoningDelta(reasoning);
                        break;
                    case "response.output_item.added":
                    {
                        var item = evt["item"];
                        if (Str(item?["type"]) != "function_call") break;
                        var outIndex = evt["output_index"]?.GetValue<int>() ?? toolIndexes.Count;
                        var callId = Str(item?["call_id"]) ?? Str(item?["id"]) ?? $"call_{Guid.NewGuid():N}"[..24];
                        var toolName = Str(item?["name"]);
                        if (string.IsNullOrEmpty(toolName)) break;
                        var index = toolIndexes.Count;
                        toolIndexes[outIndex] = index;
                        finish = "tool_calls";
                        yield return new ToolCallStarted(index, callId, toolName);
                        break;
                    }
                    case "response.function_call_arguments.delta":
                    {
                        var outIndex = evt["output_index"]?.GetValue<int>() ?? -1;
                        if (toolIndexes.TryGetValue(outIndex, out var index) && Str(evt["delta"]) is { Length: > 0 } args)
                            yield return new ToolCallArgumentsDelta(index, args);
                        break;
                    }
                    case "response.completed":
                    case "response.incomplete":
                    {
                        var resp = evt["response"];
                        if (resp?["usage"] is JsonObject usage && usage.Count > 0)
                            yield return new UsageReported(ParseUsage(usage), null);
                        if (type == "response.incomplete" && finish != "tool_calls") finish = "length";
                        break;
                    }
                    case "response.failed":
                    {
                        var err = evt["response"]?["error"];
                        throw new ProviderException($"{provider.Name}: {Str(err?["message"]) ?? "response failed"}");
                    }
                    case "error":
                        throw new ProviderException($"{provider.Name}: {Str(evt["message"]) ?? Str(evt["error"]?["message"]) ?? data}");
                }
            }
            yield return new StreamFinished(finish);
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Usage ParseUsage(JsonObject usage)
    {
        long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        return new Usage
        {
            Input = L(usage["input_tokens"]),
            Output = L(usage["output_tokens"]),
            Reasoning = L(usage["output_tokens_details"]?["reasoning_tokens"]),
            CacheRead = L(usage["input_tokens_details"]?["cached_tokens"]),
        };
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var input = new JsonArray();
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "tool":
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = m.ToolCallId,
                        ["output"] = m.TextContent,
                    });
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(m.TextContent))
                    {
                        input.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = m.TextContent }),
                        });
                    }
                    foreach (var tc in m.ToolCalls)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments,
                        });
                    }
                    break;
                default:
                {
                    var parts = new JsonArray();
                    foreach (var part in m.Content)
                    {
                        if (part.ImageBase64 is not null)
                            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{part.ImageMime};base64,{part.ImageBase64}" });
                        else if (!string.IsNullOrEmpty(part.Text))
                            parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = part.Text });
                    }
                    if (parts.Count == 0) parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = "" });
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    break;
                }
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = request.MaxTokens,
        };
        if (!string.IsNullOrWhiteSpace(request.System)) body["instructions"] = request.System;

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
            }).ToArray());
        }

        if (request.ModelSupportsReasoning && request.Reasoning != "off")
            body["reasoning"] = new JsonObject { ["effort"] = request.Reasoning, ["summary"] = "auto" };
        return body;
    }
}
