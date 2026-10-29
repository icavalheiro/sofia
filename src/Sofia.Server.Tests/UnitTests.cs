using System.Globalization;
using System.Text.Json.Nodes;
using Sofia.Server.Agent;
using Sofia.Server.Changes;
using Sofia.Server.Context;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;
using Sofia.Server.Providers;
using Sofia.Server.Tools;
using Sofia.Server.Vcs;

namespace Sofia.Server.Tests;

public class EditToolTests
{
    [Fact]
    public void ReplacesUniqueExactMatch()
    {
        var (updated, count, error) = EditTool.Apply("a\nb\nc\n", "b", "B", false);
        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal("a\nB\nc\n", updated);
    }

    [Fact]
    public void RejectsAmbiguousMatchUnlessReplaceAll()
    {
        Assert.NotNull(EditTool.Apply("x x", "x", "y", false).Error);
        var (updated, count, _) = EditTool.Apply("x x", "x", "y", true);
        Assert.Equal("y y", updated);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ToleratesCrLfFiles()
    {
        var (updated, _, error) = EditTool.Apply("one\r\ntwo\r\nthree\r\n", "one\ntwo", "uno\ndos", false);
        Assert.Null(error);
        Assert.Equal("uno\r\ndos\r\nthree\r\n", updated);
    }

    [Fact]
    public void FallsBackToWhitespaceTolerantMatch()
    {
        var (updated, _, error) = EditTool.Apply("def f():\n    return 1   \n", "def f():\n  return 1", "def f():\n    return 2", false);
        Assert.Null(error);
        Assert.Equal("def f():\n    return 2\n", updated);
    }

    [Fact]
    public void ReportsMissingText()
    {
        Assert.Contains("not found", EditTool.Apply("abc", "zzz", "y", false).Error);
    }
}

public class UnifiedDiffTests
{
    [Fact]
    public void ProducesHunkWithCounts()
    {
        var (patch, add, del) = UnifiedDiff.Create("a\nb\nc\n", "a\nB\nc\n", "f.txt");
        Assert.Equal(1, add);
        Assert.Equal(1, del);
        Assert.Contains("@@ -1,3 +1,3 @@", patch);
        Assert.Contains("-b\n+B\n", patch);
    }

    [Fact]
    public void HandlesNewAndDeletedFiles()
    {
        var created = UnifiedDiff.Create(null, "x\ny\n", "new.txt");
        Assert.Equal((2, 0), (created.Additions, created.Deletions));
        Assert.Contains("--- /dev/null", created.Patch);
        var deleted = UnifiedDiff.Create("x\n", null, "old.txt");
        Assert.Equal((0, 1), (deleted.Additions, deleted.Deletions));
    }

    [Fact]
    public void IdenticalTextsHaveNoPatch()
    {
        Assert.Equal("", UnifiedDiff.Create("same", "same", "f").Patch);
    }
}

public class StreamingTests
{
    [Fact]
    public void SplitsThinkTagsAcrossChunks()
    {
        var splitter = new ThinkTagSplitter();
        var events = new List<StreamEvent>();
        foreach (var chunk in new[] { "<thi", "nk>plan it", "</th", "ink>\nAnswer" }) events.AddRange(splitter.Push(chunk));
        events.AddRange(splitter.Flush());
        Assert.Equal("plan it", string.Concat(events.OfType<ReasoningDelta>().Select(e => e.Text)));
        Assert.Equal("Answer", string.Concat(events.OfType<TextDelta>().Select(e => e.Text)));
    }

    [Fact]
    public void LeavesInlineTagsInsideAnswerAlone()
    {
        var splitter = new ThinkTagSplitter();
        var events = splitter.Push("Use <think> tags like this").Concat(splitter.Flush()).ToList();
        Assert.Equal("Use <think> tags like this", string.Concat(events.OfType<TextDelta>().Select(e => e.Text)));
        Assert.Empty(events.OfType<ReasoningDelta>());
    }
}

public class ProviderBodyTests
{
    private static ChatRequest Request() => new()
    {
        Model = "m",
        System = "sys",
        Messages =
        [
            ChatMessage.UserText("hi"),
            new ChatMessage { Role = "assistant", ToolCalls = [new ToolCallSpec("c1", "read", "{\"path\":\"a\"}")] },
            new ChatMessage { Role = "tool", ToolCallId = "c1", Content = [ContentPart.FromText("content")] },
            ChatMessage.UserText("steer"),
        ],
        Tools = [new ToolSpec("read", "Read", new JsonObject { ["type"] = "object" })],
        Reasoning = "off",
    };

    [Fact]
    public void OpenAiBodyMapsToolsAndDisablesThinkingForLocalRuntimes()
    {
        var body = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "unsloth" }).BuildBody(Request());
        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("c1", messages[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void DeepSeekBodyTogglesThinkingAndEchoesReasoning()
    {
        var request = new ChatRequest
        {
            Model = "deepseek-v4-pro",
            Reasoning = "high",
            ModelSupportsReasoning = true,
            Messages =
            [
                ChatMessage.UserText("hi"),
                new ChatMessage { Role = "assistant", Reasoning = "thinking…", ToolCalls = [new ToolCallSpec("c1", "read", "{}")] },
            ],
        };
        var client = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "deepseek" });
        var body = client.BuildBody(request);
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("thinking…", body["messages"]![1]!["reasoning_content"]!.GetValue<string>());

        var off = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "deepseek" })
            .BuildBody(new ChatRequest { Model = "deepseek-flash", Reasoning = "off", ModelSupportsReasoning = true });
        Assert.Equal("disabled", off["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(off["reasoning_effort"]);
    }

    [Fact]
    public void PreserveThinkingOverridesFlavorDefault()
    {
        ChatRequest Req() => new()
        {
            Model = "m",
            Messages = [ChatMessage.UserText("hi"), new ChatMessage { Role = "assistant", Reasoning = "r", ToolCalls = [new ToolCallSpec("c1", "read", "{}")] }],
        };
        var on = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "unsloth", PreserveThinking = true }).BuildBody(Req());
        Assert.Equal("r", on["messages"]![1]!["reasoning_content"]!.GetValue<string>());
        var off = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "deepseek", PreserveThinking = false }).BuildBody(Req());
        Assert.Null(off["messages"]![1]!["reasoning_content"]);
    }

    [Fact]
    public void QwenCloudBodySendsEnableThinking()
    {
        var body = new OpenAiChatClient(new HttpClient(), new ProviderConfig { Flavor = "qwencloud" })
            .BuildBody(new ChatRequest { Model = "qwen3.8-max", Reasoning = "off", ModelSupportsReasoning = true });
        Assert.False(body["enable_thinking"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("glm-5.3", OpenCodeGo.Wire.Chat)]
    [InlineData("kimi-k3", OpenCodeGo.Wire.Chat)]
    [InlineData("minimax-m3.1-flash", OpenCodeGo.Wire.Chat)]
    [InlineData("minimax-m3", OpenCodeGo.Wire.Anthropic)]
    [InlineData("qwen3.8-max", OpenCodeGo.Wire.Anthropic)]
    [InlineData("claude-haiku-5-5", OpenCodeGo.Wire.Anthropic)]
    [InlineData("gpt-6-luna", OpenCodeGo.Wire.Unsupported)]
    [InlineData("gemini-4-argon", OpenCodeGo.Wire.Unsupported)]
    public void OpenCodeGoRoutesModelsByWire(string model, OpenCodeGo.Wire wire) =>
        Assert.Equal(wire, OpenCodeGo.WireFor(model));

    [Fact]
    public void OpenCodeGoUsesDedicatedModelsAndAnthropicUrls()
    {
        Assert.Equal("https://opencode.ai/inference/go/v1/models", OpenCodeGo.ModelsUrl("https://opencode.ai/inference/go/openai/v1"));
        var routed = OpenCodeGo.ForAnthropic(new ProviderConfig { BaseUrl = "https://opencode.ai/inference/go/openai/v1", ApiKey = "k", Flavor = "opencode-go" });
        Assert.Equal("anthropic", routed.Type);
        Assert.Equal("https://opencode.ai/inference/go/anthropic", routed.BaseUrl);
        Assert.Equal("k", routed.ApiKey);
    }

    /// <summary>Go rejects requests without the harness identity, so both wire protocols must send it.</summary>
    [Fact]
    public async Task OpenCodeGoRequestsCarryAgentUserAgentAndSessionId()
    {
        var go = new ProviderConfig { Name = "OpenCode Go", BaseUrl = "https://opencode.ai/inference/go/openai/v1", ApiKey = "k", Flavor = "opencode-go" };
        var request = new ChatRequest { Model = "glm-5.3", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" };

        var capture = new CapturingHandler();
        await Drain(new OpenAiChatClient(capture.Http(), go).StreamAsync(request, CancellationToken.None));
        Assert.Equal("ses_abc", Header(capture.Last, "x-opencode-session"));
        Assert.Contains("sofia/", capture.Last.Headers.UserAgent.ToString());

        capture = new CapturingHandler();
        await Drain(new AnthropicChatClient(capture.Http(), OpenCodeGo.ForAnthropic(go))
            .StreamAsync(request, CancellationToken.None));
        Assert.Equal("ses_abc", Header(capture.Last, "x-opencode-session"));

        // Vendor names other than Go's stay out of the request.
        Assert.False(capture.Last.Headers.Contains("x-amzn-bedrock-agentcore-runtime-session-id"));
    }

    /// <summary>Every provider gets the conversation id, under each gateway's own header name.</summary>
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("anthropic-proxy", "https://gw.example.com/v1")]
    [InlineData("ollama", "http://127.0.0.1:11434/v1")]
    public async Task EveryProviderSendsTheSessionHeaders(string flavor, string baseUrl)
    {
        var capture = new CapturingHandler();
        await Drain(new OpenAiChatClient(capture.Http(), new ProviderConfig { Name = "P", BaseUrl = baseUrl, Flavor = flavor })
            .StreamAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" }, CancellationToken.None));
        foreach (var name in new[] { "x-session-id", "x-litellm-trace-id", "x-claude-code-session-id", "x-dynamo-session-id" })
            Assert.Equal("ses_abc", Header(capture.Last, name));
    }

    /// <summary>Anthropic itself has no proxy in between, so Claude Code's session name is not claimed.</summary>
    [Fact]
    public async Task FirstPartyAnthropicDoesNotClaimAClaudeCodeSession()
    {
        var capture = new CapturingHandler();
        await Drain(new AnthropicChatClient(capture.Http(), new ProviderConfig { Name = "Anthropic", Type = "anthropic", BaseUrl = "https://api.anthropic.com" })
            .StreamAsync(new ChatRequest { Model = "claude-x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" }, CancellationToken.None));
        Assert.False(capture.Last.Headers.Contains("x-claude-code-session-id"));
        Assert.Equal("ses_abc", Header(capture.Last, "x-session-id"));
    }

    /// <summary>An Anthropic-compatible gateway gets every session header, plus the bearer token it usually expects.</summary>
    [Fact]
    public async Task AnthropicCompatibleGatewaySendsTheSessionHeaders()
    {
        var capture = new CapturingHandler();
        await Drain(new AnthropicChatClient(capture.Http(), new ProviderConfig { Name = "Gateway", Type = "anthropic", BaseUrl = "https://gw.example.com", ApiKey = "k" })
            .StreamAsync(new ChatRequest { Model = "claude-x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" }, CancellationToken.None));
        foreach (var name in new[] { "x-session-id", "x-litellm-trace-id", "x-claude-code-session-id", "x-dynamo-session-id" })
            Assert.Equal("ses_abc", Header(capture.Last, name));
        Assert.Equal("k", Header(capture.Last, "x-api-key"));
        Assert.Equal("Bearer k", Header(capture.Last, "Authorization"));
    }

    /// <summary>AgentCore rejects a runtime session id shorter than 33 characters; the padding stays stable.</summary>
    [Fact]
    public async Task AgentCoreRequestsSendAPaddedRuntimeSessionId()
    {
        var agentCore = new ProviderConfig
        {
            Name = "AgentCore", BaseUrl = "https://abc.agentcore-data.bedrock-agentcore.us-east-1.amazonaws.com/runtime/runtimes/abc/invocations",
        };
        var capture = new CapturingHandler();
        await Drain(new OpenAiChatClient(capture.Http(), agentCore)
            .StreamAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" }, CancellationToken.None));

        var runtime = Header(capture.Last, "x-amzn-bedrock-agentcore-runtime-session-id");
        Assert.NotNull(runtime);
        Assert.True(runtime!.Length >= 33, $"expected at least 33 characters, got {runtime.Length}");
        Assert.StartsWith("ses_abc", runtime);
        Assert.Equal(runtime, Header(capture.Last, "x-amzn-bedrock-agentcore-runtime-session-id"));
        Assert.Equal("ses_abc", Header(capture.Last, "x-amzn-bedrock-agentcore-policy-session-id"));
    }

    /// <summary>A request with no conversation (model discovery) still identifies itself with the harness session id.</summary>
    [Fact]
    public void DiscoveryRequestsFallBackToTheHarnessSessionId()
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/inference/go/v1/models");
        ProviderIdentity.ApplyIdentity(message, new ProviderConfig { Name = "Go", BaseUrl = "https://opencode.ai/inference/go/openai/v1", Flavor = "opencode-go" }, null);
        Assert.Equal(ProviderIdentity.HarnessSessionId, Header(message, "x-opencode-session"));
        Assert.Equal(ProviderIdentity.HarnessSessionId, Header(message, "x-session-id"));
        Assert.Equal(ProviderIdentity.UserAgent, message.Headers.UserAgent.ToString());

        // The same fallback covers a second unidentified request, so probes group together.
        var other = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        ProviderIdentity.ApplyIdentity(other, new ProviderConfig { Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" }, "  ");
        Assert.Equal(ProviderIdentity.HarnessSessionId, Header(other, "x-session-id"));
    }

    [Theory]
    [InlineData("ses_abc", 33)]
    [InlineData("ses_0123456789012345678901234567890", 33)]
    public void PaddingReachesTheMinimumDeterministicallyAndStaysUnique(string id, int min)
    {
        var padded = ProviderIdentity.Padded(id, min);
        Assert.True(padded.Length >= min);
        Assert.Equal(padded, ProviderIdentity.Padded(id, min));
        Assert.StartsWith(id, padded);
        Assert.NotEqual(padded, ProviderIdentity.Padded(id + "x", min));
    }

    private static string? Header(HttpRequestMessage message, string name) =>
        message.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    /// <summary>A Go request must send exactly one user agent — the harness one, not the client default.</summary>
    [Fact]
    public async Task GoRequestsReplaceTheClientDefaultUserAgent()
    {
        var capture = new CapturingHandler();
        using var http = capture.Http();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("testharness/9");
        var go = new ProviderConfig { Name = "OpenCode Go", BaseUrl = "https://opencode.ai/inference/go/openai/v1", ApiKey = "k", Flavor = "opencode-go" };
        await Drain(new OpenAiChatClient(http, go).StreamAsync(
            new ChatRequest { Model = "glm-5.3", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_abc" }, CancellationToken.None));
        Assert.Equal(ProviderIdentity.UserAgent, capture.Last.Headers.UserAgent.ToString());

        // A non-Go provider keeps the client default untouched.
        capture = new CapturingHandler();
        using var plain = capture.Http();
        plain.DefaultRequestHeaders.UserAgent.ParseAdd("testharness/9");
        await Drain(new OpenAiChatClient(plain, new ProviderConfig { Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" })
            .StreamAsync(new ChatRequest { Model = "gpt-6", Messages = [ChatMessage.UserText("hi")] }, CancellationToken.None));
        Assert.Equal("testharness/9", capture.Last.Headers.UserAgent.ToString());
    }

    private static async Task Drain(IAsyncEnumerable<StreamEvent> events)
    {
        try { await foreach (var _ in events) { } } catch (ProviderException) { /* the stub endpoint answers with no usable stream */ }
    }

    /// <summary>Minimal transport: keeps the request it was given and answers with an empty SSE body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestMessage? _request;
        /// <summary>The request the client sent; throws when a test reads it before any call.</summary>
        public HttpRequestMessage Last => _request ?? throw new InvalidOperationException("the client sent no request");
        public HttpClient Http() => new(this);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    [Fact]
    public void CopilotCatalogKeepsOnlyChatCapableModels()
    {
        var json = JsonNode.Parse("""
        { "data": [
          { "id": "gpt-6-sol", "name": "GPT 6 Sol", "model_picker_enabled": true, "supported_endpoints": ["/chat/completions", "/responses"],
            "capabilities": { "type": "chat", "limits": { "max_context_window_tokens": 400000, "max_output_tokens": 64000 }, "supports": { "vision": true, "tool_calls": true } } },
          { "id": "codex-only", "model_picker_enabled": true, "supported_endpoints": ["/responses"], "capabilities": { "type": "chat" } },
          { "id": "text-embedding-3", "capabilities": { "type": "embeddings" } },
          { "id": "hidden", "model_picker_enabled": false, "capabilities": { "type": "chat" } },
          { "id": "gpt-6-sol", "capabilities": { "type": "chat" } }
        ] }
        """);
        var models = ProviderService.ParseCopilotModels(json);
        Assert.Equal(["gpt-6-sol", "codex-only"], models.Select(m => m.Id));
        var model = models[0];
        Assert.Equal(400_000, model.ContextLength);
        Assert.Equal(64_000, model.MaxOutput);
        Assert.True(model.Vision);
    }

    [Fact]
    public void NewPresetsAreRegisteredWithTheirFlavors()
    {
        var presets = ProviderService.Presets.ToDictionary(p => p.Id);
        Assert.Equal("opencode-go", presets["opencode-go"].Flavor);
        Assert.Equal("qwencloud", presets["qwencloud"].Flavor);
        Assert.Equal("deepseek", presets["deepseek"].Flavor);
        Assert.Equal("github-device", presets["github-copilot"].Auth);
        Assert.Equal("copilot", ProviderService.GuessFlavor("https://api.githubcopilot.com", "openai"));
        Assert.Equal("opencode-go", ProviderService.GuessFlavor("https://opencode.ai/inference/go/openai/v1", "openai"));
    }

    [Fact]
    public void AnthropicBodyMergesToolResultsWithFollowingUserTurn()
    {
        var body = new AnthropicChatClient(new HttpClient(), new ProviderConfig()).BuildBody(Request());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var last = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", last[0]!["type"]!.GetValue<string>());
        Assert.Equal("text", last[1]!["type"]!.GetValue<string>());
        Assert.Null(body["thinking"]);
    }

    [Fact]
    public void AnthropicBodyCachesTheGrowingTranscriptWithARollingBreakpoint()
    {
        var body = new AnthropicChatClient(new HttpClient(), new ProviderConfig()).BuildBody(Request());
        var messages = body["messages"]!.AsArray();
        var lastMessage = messages[^1]!["content"]!.AsArray();
        Assert.Equal("ephemeral", lastMessage[^1]!["cache_control"]!["type"]!.GetValue<string>());
        // Only the last block is marked; earlier blocks of that message are not.
        Assert.All(lastMessage.Take(lastMessage.Count - 1), b => Assert.Null(b!["cache_control"]));
        Assert.All(messages.Take(messages.Count - 1), m => Assert.All(m!["content"]!.AsArray(), b => Assert.Null(b!["cache_control"])));
        if (body["system"] is JsonArray system)
            Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ContextUpdatesInlinesSmallFileChangesAndSummarisesLargeOnes()
    {
        PromptSnapshot Snap(string content) => ContextUpdates.Snapshot([new ContextFile("repo/AGENTS.md", content)], [], [], [], null, "ask", "en", "pwsh", (null, null),
            new DateTime(2026, 10, 27));
        var frozen = Snap("old rules").Facts;

        Assert.Null(ContextUpdates.Build(frozen, Snap("old rules")));
        Assert.Null(ContextUpdates.Build(null, Snap("new rules")));

        var small = ContextUpdates.Build(frozen, Snap("new rules"))!;
        var inlined = Assert.Single(small);
        Assert.Contains("<project_instructions path=\"repo/AGENTS.md\">", inlined);
        Assert.Contains("new rules", inlined);

        var large = ContextUpdates.Build(frozen, Snap(new string('x', ContextUpdates.MaxInlineContextFileChars + 1)))!;
        var notice = Assert.Single(large);
        Assert.Contains("repo/AGENTS.md", notice);
        Assert.DoesNotContain("xxxx", notice);
    }
}

public class HistoryImageTests
{
    private static List<ChatMessage> Build(string modelId, bool vision)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3]);
        var message = new Message
        {
            Id = "m1", Role = "user", Text = "look",
            Attachments = [new Attachment { Id = "a1", Name = "a.png", Kind = "image", Mime = "image/png", Bytes = 3, StoragePath = path }],
        };
        try { return HistoryBuilder.Build([message], new ModelInfo { ModelId = modelId, Vision = vision }); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("unknown-local-model", false, true)]
    [InlineData("deepseek-chat", false, false)]
    [InlineData("deepseek-chat", true, true)]
    public void ImagesReachModelsNotKnownToBeTextOnly(string modelId, bool vision, bool expected)
    {
        var parts = Build(modelId, vision).Single().Content;
        Assert.Equal(expected, parts.Any(p => p.ImageBase64 is not null));
    }
}

public class HistoryMediaTests
{
    private static List<ChatMessage> Build(ModelInfo model, string kind, string mime, string name)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3]);
        var message = new Message
        {
            Id = "a1", Role = "assistant", Seq = 1,
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "c1", Name = "read", Arguments = "{}", Result = "attached", Status = "success", Completed = true,
                    Images = [new Attachment { Id = "m1", Name = name, Kind = kind, Mime = mime, Bytes = 3, StoragePath = path }],
                },
            ],
        };
        try { return HistoryBuilder.Build([message], model); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("audio", "audio/wav", true, false, true)]
    [InlineData("audio", "audio/mpeg", true, false, true)]
    [InlineData("audio", "audio/ogg", true, false, false)]
    [InlineData("audio", "audio/wav", false, true, false)]
    [InlineData("video", "video/mp4", false, true, true)]
    [InlineData("video", "video/mp4", true, false, false)]
    public void AudioAndVideoOnlyReachModelsThatAcceptThem(string kind, string mime, bool audio, bool video, bool expected)
    {
        var history = Build(new ModelInfo { ModelId = "some-model", Audio = audio, Video = video }, kind, mime, "clip");
        var sent = history.SelectMany(m => m.Content).Any(c => c.MediaBase64 is not null && c.MediaKind == kind);
        Assert.Equal(expected, sent);
        if (!expected) Assert.Contains("clip", history.Last().TextContent);
    }
}

public class HistorySteeringTests
{
    [Fact]
    public void ScreenshotsFollowToolResultsAndOnlyTheLatestAreResentAsImages()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Message Shot(int n) => new()
            {
                Id = $"a{n}", Role = "assistant", Seq = n,
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = $"c{n}", Name = "browser", Arguments = "{}", Result = "shot", Status = "success", Completed = true,
                        Images = [new Attachment { Id = $"i{n}", Name = $"s{n}.jpg", Kind = "image", Mime = "image/jpeg", StoragePath = path }],
                    },
                ],
            };
            var history = HistoryBuilder.Build([Shot(1), Shot(2), Shot(3)], new ModelInfo { ModelId = "unknown-local-model" });
            Assert.Equal(["assistant", "tool", "user", "assistant", "tool", "user", "assistant", "tool", "user"], history.Select(m => m.Role));
            Assert.Equal([false, true, true], history.Where(m => m.Role == "user").Select(m => m.Content.Any(c => c.ImageBase64 is not null)));
            Assert.Contains("not resent", history[2].TextContent);

            var textOnly = HistoryBuilder.Build([Shot(1)], new ModelInfo { ModelId = "deepseek-chat" });
            Assert.DoesNotContain(textOnly.SelectMany(m => m.Content), c => c.ImageBase64 is not null);
            Assert.Contains("cannot see images", textOnly[2].TextContent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<Message> Turn(bool steered) =>
    [
        new() { Id = "u1", Role = "user", Text = "start", Seq = 1 },
        new()
        {
            Id = "a1", Role = "assistant", Seq = 2,
            ToolCalls = [new ToolCall { Id = "c1", Name = "read", Arguments = "{}", Result = "file", Status = "success", Completed = true }],
        },
        new() { Id = "u2", Role = "user", Text = "use tabs", Seq = 3, Steered = steered },
    ];

    [Fact]
    public void SteeringJoinsTheInterruptedToolResultInsteadOfOpeningAUserTurn()
    {
        var history = HistoryBuilder.Build(Turn(steered: true), new ModelInfo { ModelId = "m" });
        Assert.Equal(["user", "assistant", "tool"], history.Select(m => m.Role));
        Assert.StartsWith("file", history[2].TextContent);
        Assert.Contains("use tabs", history[2].TextContent);
    }

    [Fact]
    public void QueuedFollowUpsStayRegularUserTurns()
    {
        var history = HistoryBuilder.Build(Turn(steered: false), new ModelInfo { ModelId = "m" });
        Assert.Equal(["user", "assistant", "tool", "user"], history.Select(m => m.Role));
        Assert.Equal("file", history[2].TextContent);
    }
}

public class ParsingTests
{
    [Fact]
    public void ParsesGitPatchPerFile()
    {
        const string patch = "diff --git a/x.txt b/x.txt\nindex 1..2 100644\n--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-a\n+b\n" +
                             "diff --git a/n.txt b/n.txt\nnew file mode 100644\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1 @@\n+hi\n";
        var diffs = GitService.ParsePatch(patch);
        Assert.Equal(2, diffs.Count);
        Assert.Equal(("x.txt", "modified", 1, 1), (diffs[0].File, diffs[0].Status, diffs[0].Additions, diffs[0].Deletions));
        Assert.Equal(("n.txt", "added"), (diffs[1].File, diffs[1].Status));
    }

    [Fact]
    public void SplitsCommandLinesWithQuotes()
    {
        Assert.Equal(["npx", "-y", "@scope/pkg", "C:\\My Folder"], McpManager.SplitCommandLine("npx -y @scope/pkg \"C:\\My Folder\""));
    }

    [Fact]
    public void ExpandsPromptTemplates()
    {
        Assert.Equal("Fix a in b", CommandService.Expand("Fix $1 in $2", "a b"));
        Assert.Equal("Review: all of it", CommandService.Expand("Review: $ARGUMENTS", "all of it"));
        Assert.Equal("Do it\n\nextra", CommandService.Expand("Do it", "extra"));
    }

    [Fact]
    public void RecoversJsonWrappedInProse()
    {
        var args = Args.Parse("Sure: {\"path\": \"a.txt\"} done");
        Assert.Equal("a.txt", Args.Required(args, "path"));
    }

    [Fact]
    public void DuplicateKeysKeepTheLastValueWithoutThrowing()
    {
        var args = Args.Parse("{\"path\":\"a.txt\",\"limit\":80,\"limit\":0}");
        Assert.Equal(0, Args.Int(args, "limit"));
        Assert.Equal("a.txt", Args.Required(args, "path"));
        Assert.Equal("{\"path\":\"a.txt\",\"limit\":0}", args.ToJsonString());
    }
}

public class GitIgnoreTests
{
    private static bool HasGit => GitExe is not null;

    private static readonly string? GitExe = ProcessRunner.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");

    private static async Task InitRepoAsync(string dir)
    {
        var (code, _, err) = await ProcessRunner.ExecAsync(GitExe!, ["init", "-q", "-b", "main"], dir);
        Assert.True(code == 0, "git init failed: " + err);
    }

    /// <summary>Best-effort cleanup: git leaves read-only object files behind.</summary>
    private static void DeleteDir(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch (IOException) { /* temp leftovers are fine */ }
        catch (UnauthorizedAccessException) { /* temp leftovers are fine */ }
    }

    [Fact]
    public async Task ReportsIgnoredPathsAndKeepsTrackedAndForeignOnes()
    {
        if (!HasGit) return;    // no git on this machine: nothing to assert about ignore rules.
        var dir = Directory.CreateTempSubdirectory("sofia-ignore-").FullName;
        try
        {
            await InitRepoAsync(dir);
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "scratch/\n*.log\n");
            Directory.CreateDirectory(Path.Combine(dir, "scratch"));
            File.WriteAllText(Path.Combine(dir, "scratch", "notes.md"), "x");
            File.WriteAllText(Path.Combine(dir, "run.log"), "x");
            File.WriteAllText(Path.Combine(dir, "build.log"), "x");
            File.WriteAllText(Path.Combine(dir, "Program.cs"), "x");
            // Tracked files are never ignored, even when a rule matches them (git status proves it).
            var (addCode, _, addErr) = await ProcessRunner.ExecAsync(GitExe!, ["add", "-f", "build.log"], dir);
            Assert.True(addCode == 0, "git add failed: " + addErr);

            var paths = new[]
            {
                Path.Combine(dir, "scratch", "notes.md"),
                Path.Combine(dir, "run.log"),
                Path.Combine(dir, "build.log"),
                Path.Combine(dir, "Program.cs"),
                Path.Combine(dir, "..", "outside.txt"),
            };
            var ignored = await new GitService().IgnoredAsync(dir, paths, CancellationToken.None);

            Assert.Contains(Path.Combine(dir, "scratch", "notes.md").Replace('\\', '/'), ignored);
            Assert.Contains(Path.Combine(dir, "run.log").Replace('\\', '/'), ignored);
            Assert.DoesNotContain(Path.Combine(dir, "build.log").Replace('\\', '/'), ignored);
            Assert.DoesNotContain(Path.Combine(dir, "Program.cs").Replace('\\', '/'), ignored);
            // A path outside the repository must not break the check nor be reported.
            Assert.All(ignored, p => Assert.DoesNotContain("outside.txt", p, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task ResolvesRelativePathsAgainstASubFolderOfTheRepository()
    {
        if (!HasGit) return;
        var dir = Directory.CreateTempSubdirectory("sofia-ignore-sub-").FullName;
        try
        {
            await InitRepoAsync(dir);
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "src/dist/\n");
            var sub = Path.Combine(dir, "src", "dist");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "bundle.js"), "x");

            var rootIgnore = await new GitService().IgnoredAsync(dir, ["src/dist/bundle.js"], CancellationToken.None);
            var fromSub = await new GitService().IgnoredAsync(sub, ["bundle.js"], CancellationToken.None);

            Assert.Single(rootIgnore);
            Assert.Equal(Path.Combine(sub, "bundle.js").Replace('\\', '/'), Assert.Single(fromSub));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task TreatsNothingAsIgnoredOutsideARepository()
    {
        var dir = Directory.CreateTempSubdirectory("sofia-nogit-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "run.log"), "x");
            var ignored = await new GitService().IgnoredAsync(dir, ["run.log"], CancellationToken.None);
            Assert.Empty(ignored);
        }
        finally
        {
            DeleteDir(dir);
        }
    }
}

public class TextDurationTests
{
    [Theory]
    [InlineData(0L, "0ms")]
    [InlineData(873L, "873ms")]
    [InlineData(999L, "999ms")]
    [InlineData(1000L, "1.0s")]
    [InlineData(12_400L, "12.4s")]
    [InlineData(59_900L, "59.9s")]
    [InlineData(60_000L, "1.00m")]
    [InlineData(135_000L, "2.25m")]
    [InlineData(3_540_000L, "59.00m")]
    [InlineData(3_600_000L, "1.00h")]
    [InlineData(3_780_000L, "1.05h")]
    public void FormatsElapsedInLargestUnitStarted(long ms, string expected) =>
        Assert.Equal(expected, Text.Duration(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void UsesInvariantDecimalSeparatorWhateverTheHostCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            Assert.Equal("1.0s", Text.Duration(TimeSpan.FromSeconds(1)));
            Assert.Equal("2.25m", Text.Duration(TimeSpan.FromSeconds(135)));
            Assert.Equal("1.05h", Text.Duration(TimeSpan.FromSeconds(3780)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class RetryPolicyTests
{
    [Theory]
    // 499 is nginx' "Client Closed Request": the gateway hung up, the model never answered.
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(499, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(529, true)]
    // Everything else says the request itself is wrong: repeating it only delays the message.
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(413, false)]
    [InlineData(422, false)]
    public void RepeatsOnlyTransientStatuses(int status, bool transient) =>
        Assert.Equal(transient, RetryPolicy.IsTransient(new ProviderException("boom", status)));

    [Fact]
    public void RepeatsTransportFailuresAndProviderTimeouts()
    {
        Assert.True(RetryPolicy.IsTransient(new HttpRequestException("connection refused")));
        Assert.True(RetryPolicy.IsTransient(new IOException("the response ended prematurely")));
        Assert.True(RetryPolicy.IsTransient(new TaskCanceledException("The request was canceled due to the configured timeout.")));
    }

    [Fact]
    public void RepeatsInStreamOverloadErrorsWithoutAStatus()
    {
        Assert.True(RetryPolicy.IsTransient(new ProviderException("Claude: Overloaded_error: Server temporarily operating at capacity")));
        Assert.True(RetryPolicy.IsTransient(new ProviderException("OpenRouter: Rate limit exceeded, please try again later")));
    }

    [Fact]
    public void KeepsPermanentFailuresFast()
    {
        Assert.False(RetryPolicy.IsTransient(new ProviderException("Could not reach Ollama at http://localhost:11434: connection refused")));
        Assert.False(RetryPolicy.IsTransient(new ProviderException("Claude: this model's maximum context length is 4096 tokens")));
        Assert.False(RetryPolicy.IsTransient(new ProviderException("OpenAI: Incorrect API key provided.")));
        Assert.False(RetryPolicy.IsTransient(new InvalidOperationException("Stopped after 100 steps without a final answer.")));
    }

    [Fact]
    public void MakesSixAttemptsWaitingFiveSeconds()
    {
        Assert.Equal(6, RetryPolicy.MaxAttempts);
        Assert.Equal(5, RetryPolicy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.Delay(4));
    }

    [Fact]
    public void GivesAOneLineReasonForTheNotice()
    {
        Assert.Equal("Fake: Client Closed Request", RetryPolicy.Reason(new ProviderException("Fake: Client Closed Request\nnginx/1.27")));
    }
}


