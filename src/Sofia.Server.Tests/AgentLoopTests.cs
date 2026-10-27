using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Sofia.Server.Tests;

/// <summary>Scripted OpenAI-compatible endpoint: first call writes a file, the next one answers.</summary>
internal sealed class FakeLlmHandler : HttpMessageHandler
{
    /// <summary>Every request body the fake endpoint received (shared: handlers are created per client).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Bodies = [];

    /// <summary>
    /// Conversations that get nginx' <c>499 Client Closed Request</c> until this counter runs out, keyed by a marker the
    /// test puts at the start of its user message. Only the request whose last message starts with the marker is rejected,
    /// so Sofia's repeated attempts are hit and background calls (knowledge review) are not.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlakyRemaining = [];

    /// <summary>How often each marker was rejected: a conversation that recovered made one more attempt than this.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlakyHits = [];

    /// <summary>How long the fake holds the answer of a flaky conversation that it did <em>not</em> reject, so a test can
    /// read the retry notice off the message while Sofia is waiting for that attempt.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlakyHoldMs = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (!url.StartsWith("http://fake.llm/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.EndsWith("/models", StringComparison.Ordinal))
            return Json("""{"data":[{"id":"fake-model","context_length":16384}]}""");

        var body = await request.Content!.ReadAsStringAsync(ct);
        Bodies.Add(body);
        var flaky = FlakyMarker(body);
        if (flaky is not null)
        {
            var left = FlakyRemaining.AddOrUpdate(flaky, 0, (_, n) => n - 1);
            if (left >= 0)
            {
                FlakyHits.AddOrUpdate(flaky, 1, (_, n) => n + 1);
                return new HttpResponseMessage((HttpStatusCode)499)
                {
                    Content = new StringContent("""{"error":{"message":"Client Closed Request"}}""", Encoding.UTF8, "application/json"),
                };
            }
            // The gateway recovered: answer this conversation with plain text, no tools.
            if (FlakyHoldMs.TryGetValue(flaky, out var hold)) await Task.Delay(hold, ct);
            return Sse(RecoveredScript);
        }
        // Prompt-cache turn: every turn of that test gets two steps — the model saves a workspace note, then answers.
        // The markers live in that test's user messages, and a save already in the history means this turn's save is
        // done, so the script stays deterministic however many turns the test runs.
        if (body.Contains("PREFIXALPHA", StringComparison.Ordinal) || body.Contains("PREFIXBETA", StringComparison.Ordinal))
            return Sse(CountOf(body, "Saved workspace note") < CountOf(body, "\"role\":\"user\"") ? NotesSaveScript : NotesSaveAnswer);
        if (body.Contains("future sessions should know", StringComparison.Ordinal))
        {
            // Knowledge review: answer with one justified workspace note and one the reviewer rejects as useless.
            const string note = """{"choices":[{"index":0,"delta":{"content":"[{\"scope\":\"workspace\",\"text\":\"Greetings live in hello.txt at the project root.\",\"useful\":true,\"why\":\"The next session would otherwise search for where greetings go.\"},{\"scope\":\"workspace\",\"text\":\"We created one file called hello.txt in this chat.\",\"useful\":false,\"why\":\"It only describes what happened here.\"}]"}}]}""";
            const string stop = """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {note}\n\ndata: {stop}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
        string[] chunks = body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
            ?
            [
                """{"choices":[{"index":0,"delta":{"reasoning_content":"File written."}}]}""",
                """{"choices":[{"index":0,"delta":{"content":"Done"}}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            ]
            :
            [
                """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"write","arguments":""}}]}}]}""",
                """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"hello.txt\","}}]}}]}""",
                """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"content\":\"hi\\n\"}"}}]}}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
                """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20}}""",
            ];
        var sse = string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    /// <summary>First step of the turn saves a workspace note through the `notes` tool, the next one answers.</summary>
    private static string[] NotesSaveScript =>
    [
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_n1","function":{"name":"notes","arguments":""}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"action\":\"save\",\"scope\":\"workspace\",\"text\":\"Tests run with dotnet test Sofia.slnx here.\"}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
    ];

    private static string[] NotesSaveAnswer =>
    [
        """{"choices":[{"index":0,"delta":{"content":"Noted"}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
    ];

    /// <summary>The answer the fake gives once a flaky conversation recovers: plain text, no tool calls.</summary>
    private static string[] RecoveredScript =>
    [
        """{"choices":[{"index":0,"delta":{"content":"Recovered"}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
    ];

    /// <summary>
    /// The flaky marker this request carries. The last message has to <em>start</em> with it, so the repeats of one step
    /// are rejected while unrelated calls that merely quote the chat (the knowledge review) are not.
    /// </summary>
    private static string? FlakyMarker(string body)
    {
        var text = LastMessageText(body);
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var marker in FlakyRemaining.Keys)
            if (text.StartsWith(marker, StringComparison.Ordinal)) return marker;
        return null;
    }

    private static string? LastMessageText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0) return null;
            var last = messages[messages.GetArrayLength() - 1];
            return last.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpResponseMessage Sse(string[] chunks) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        };

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>TestServer has no remote address; present every request as loopback like a local UI.</summary>
internal sealed class LoopbackStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class AgentLoopTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sofia-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly WebApplicationFactory<Program> _factory;

    public AgentLoopTests()
    {
        _project = Path.Combine(_home, "project");
        Directory.CreateDirectory(_project);
        Environment.SetEnvironmentVariable("SOFIA_HOME", Path.Combine(_home, "data"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
            services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
            services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
        }));
    }

    [Fact]
    public async Task RunsToolLoopWithPermissionAndTracksChanges()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";

        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "create hello.txt", modelId, permissionMode = "ask" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "permission");
        var card = session.GetProperty("messages").EnumerateArray().Last(m => m.TryGetProperty("permission", out _));
        var requestId = card.GetProperty("permission").GetProperty("requestId").GetString();
        Assert.Equal("write", card.GetProperty("permission").GetProperty("tool").GetString());
        Assert.False(File.Exists(Path.Combine(_project, "hello.txt")));

        var reply = await http.PostAsJsonAsync($"/api/sessions/{sessionId}/permissions/{requestId}", new { decision = "once" });
        reply.EnsureSuccessStatusCode();

        session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        var messages = session.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Done", messages.Last().GetProperty("text").GetString());
        Assert.Equal("File written.", messages.Last().GetProperty("reasoning").GetString());
        var tool = messages.First(m => m.TryGetProperty("toolCalls", out _)).GetProperty("toolCalls")[0];
        Assert.Equal("success", tool.GetProperty("status").GetString());
        Assert.Equal("hi\n", File.ReadAllText(Path.Combine(_project, "hello.txt")).Replace("\r\n", "\n"));
        Assert.Equal("create hello.txt", session.GetProperty("title").GetString());

        var diff = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/diff");
        Assert.Equal("added", diff[0].GetProperty("status").GetString());
        Assert.Equal("hello.txt", diff[0].GetProperty("file").GetString());

        // Branching at the first tool-calling reply copies the history up to there, snapshots included.
        var toolMessageId = messages.First(m => m.TryGetProperty("toolCalls", out _)).GetProperty("id").GetString();
        var branch = await Post(http, $"/api/sessions/{sessionId}/branch", new { messageId = toolMessageId });
        var branchId = branch.GetProperty("id").GetString()!;
        Assert.NotEqual(sessionId, branchId);
        Assert.Equal("create hello.txt (branch)", branch.GetProperty("title").GetString());
        var branchMessages = branch.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(messages.IndexOf(messages.First(m => m.TryGetProperty("toolCalls", out _))) + 1, branchMessages.Count);
        Assert.DoesNotContain(branchMessages, m => messages.Any(o => o.GetProperty("id").GetString() == m.GetProperty("id").GetString()));
        var branchDiff = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{branchId}/diff");
        Assert.Equal("hello.txt", branchDiff[0].GetProperty("file").GetString());

        // Reverting the prompt removes the created file and the transcript from that point on.
        var userId = messages.First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetString();
        var revert = await Post(http, $"/api/sessions/{sessionId}/revert", new { messageId = userId });
        Assert.Single(revert.GetProperty("restoredFiles").EnumerateArray());
        Assert.False(File.Exists(Path.Combine(_project, "hello.txt")));
    }

    [Fact]
    public async Task LearnsKnowledgeNotesAnnouncesThemAndInjectsThemIntoThePrompt()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var wsId = workspace.GetProperty("id").GetString()!;
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        var prompt = "Please create hello.txt at the project root with a friendly greeting inside it, and remember that this is where greetings live.";
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = prompt, modelId, permissionMode = "full" });
        var session = await WaitFor(http, sessionId, s => s.GetProperty("messages").EnumerateArray().Any(m => m.TryGetProperty("knowledge", out _)));
        var card = session.GetProperty("messages").EnumerateArray().Last(m => m.TryGetProperty("knowledge", out _));
        var entry = card.GetProperty("knowledge")[0];
        Assert.Equal("workspace", entry.GetProperty("scope").GetString());
        // The utility gate kept only the justified note, and its argument rides along on the card.
        Assert.Equal(1, card.GetProperty("knowledge").GetArrayLength());
        Assert.Equal("The next session would otherwise search for where greetings go.", entry.GetProperty("why").GetString());
        var noteId = entry.GetProperty("id").GetString()!;

        var notes = await http.GetFromJsonAsync<JsonElement>($"/api/knowledge?workspaceId={wsId}");
        var stored = notes.EnumerateArray().Single();
        Assert.Equal(noteId, stored.GetProperty("id").GetString());
        Assert.DoesNotContain("We created one file called hello.txt", stored.ToString());

        // The note rides along in the next system prompt, but the card itself never reaches the model.
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "thanks", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle" && s.GetProperty("messages").GetArrayLength() > 5);
        Assert.Contains(FakeLlmHandler.Bodies, b => b.Contains("Greetings live in hello.txt", StringComparison.Ordinal));

        var deleted = await http.DeleteAsync($"/api/knowledge/{noteId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>($"/api/knowledge?workspaceId={wsId}")).GetArrayLength());
        session = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
        var struck = session.GetProperty("messages").EnumerateArray().First(m => m.TryGetProperty("knowledge", out _));
        Assert.True(struck.GetProperty("knowledge")[0].GetProperty("deleted").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/api/knowledge/{noteId}")).StatusCode);
    }

    [Fact]
    public async Task SystemPromptAndToolsStayIdenticalAcrossTheStepsOfATurn()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // Step 1 saves a workspace note (it rewrites the <knowledge> block when the prompt is rebuilt), step 2 answers.
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "PREFIXALPHA save the test command", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("text", out var t) && t.GetString() == "Noted"));

        var prompts = ConversationSystems("PREFIXALPHA", "PREFIXBETA");
        Assert.Equal(2, prompts.Count);
        // Saving a note must not invalidate the prefix of the turn that is running.
        Assert.Equal(prompts[0], prompts[1]);
        Assert.DoesNotContain(NoteText, prompts[1]);

        // The next turn keeps the very same system prompt; the note reaches the model as a context update instead.
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "PREFIXBETA and again", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => Count(s, m => m.GetProperty("role").GetString() == "user") >= 2
            && Count(s, m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("text", out var t) && t.GetString() == "Noted") >= 2);

        var secondTurn = ConversationSystems("PREFIXBETA");
        Assert.Equal(2, secondTurn.Count);
        Assert.All(secondTurn, p => Assert.DoesNotContain(NoteText, p, StringComparison.Ordinal));
        Assert.Equal(prompts[0], secondTurn[0]);
        Assert.Equal(secondTurn[0], secondTurn[1]);
    }

    [Fact]
    public async Task SystemPromptStaysIdenticalForTheWholeConversationAndChangesArriveAsContextUpdates()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // Turn 1 saves a workspace note, turn 2 and 3 answer.
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "PREFIXALPHA save the test command", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => Count(s, m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("text", out var t) && t.GetString() == "Noted") >= 1);
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "PREFIXBETA second turn", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => Count(s, m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("text", out var t) && t.GetString() == "Noted") >= 2);
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "PREFIXBETA third turn", modelId, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle"
            && Count(s, m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("text", out var t) && t.GetString() == "Noted") >= 3);

        var bodies = FakeLlmHandler.Bodies
            .Where(b => b.Contains("PREFIXALPHA", StringComparison.Ordinal) && !b.Contains("curate a small knowledge base", StringComparison.Ordinal))
            .Where(b => SystemOf(b).Contains(_project.Replace('\\', '/'), StringComparison.Ordinal))
            .ToList();
        var systems = bodies.Select(SystemOf).ToList();
        Assert.True(systems.Count >= 6, $"expected 2 steps for each of 3 turns, got {systems.Count}");
        Assert.All(systems, s => Assert.Equal(systems[0], s));
        Assert.DoesNotContain(NoteText, systems[0], StringComparison.Ordinal);

        // Turn 1 has no update; turn 2 carries the note; turn 3 replays it once and adds nothing.
        var turn1 = bodies.Where(b => !b.Contains("PREFIXBETA", StringComparison.Ordinal)).ToList();
        Assert.All(turn1, b => Assert.DoesNotContain("<context_updates>", UserTexts(b), StringComparison.Ordinal));
        var turn2 = bodies.Where(b => b.Contains("second turn", StringComparison.Ordinal) && !b.Contains("third turn", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(turn2);
        Assert.All(turn2, b =>
        {
            var users = UserTexts(b);
            Assert.Contains("<context_updates>", users, StringComparison.Ordinal);
            Assert.Contains(NoteText, users, StringComparison.Ordinal);
        });
        var turn3 = bodies.Where(b => b.Contains("third turn", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(turn3);
        Assert.All(turn3, b =>
        {
            var users = UserTexts(b);
            Assert.Equal(1, CountOccurrences(users, "<context_updates>"));
            Assert.Equal(1, CountOccurrences(users, NoteText));
        });
    }

    private static string SystemOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;

    /// <summary>The text of every user message of a request body, decoded (the raw body escapes angle brackets).</summary>
    private static string UserTexts(string body)
    {
        var sb = new StringBuilder();
        foreach (var m in JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray())
        {
            if (m.GetProperty("role").GetString() != "user") continue;
            var content = m.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) sb.AppendLine(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
        }
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public async Task RepeatsAStepTheGatewayRejectedAndRecovers()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // The endpoint answers this conversation with 499 twice. Sofia repeats the step and the answer lands as if
        // nothing happened: no error on the card, no stale retry notice.
        var marker = "FLAKYRECOVER" + Guid.NewGuid().ToString("N");
        FakeLlmHandler.FlakyRemaining[marker] = 2;

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = marker + " hello", modelId, permissionMode = "full" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        var last = session.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("Recovered", last.GetProperty("text").GetString());
        Assert.False(last.TryGetProperty("error", out _));
        Assert.False(last.TryGetProperty("retry", out _));
        Assert.Equal(2, FakeLlmHandler.FlakyHits[marker]);   // rejected twice, answered on the third attempt
    }

    [Fact]
    public async Task ShowsTheErrorOnlyAfterAllAttemptsFailed()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // A gateway that never recovers: Sofia gives up only after every attempt of the step.
        var marker = "FLAKYGIVEUP" + Guid.NewGuid().ToString("N");
        FakeLlmHandler.FlakyRemaining[marker] = 99;

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = marker + " hello", modelId, permissionMode = "full" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "error", tries: 400);
        var last = session.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("assistant", last.GetProperty("role").GetString());
        Assert.Contains("Client Closed Request", last.GetProperty("error").GetString());
        Assert.False(last.TryGetProperty("retry", out _));   // the failed card keeps no "still retrying" line
        Assert.Equal(Sofia.Server.Providers.RetryPolicy.MaxAttempts, FakeLlmHandler.FlakyHits[marker]);
    }

    [Fact]
    public async Task AnnouncesTheAttemptItStartsWhileTheProviderIsRetried()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // Rejected once, then held long enough that the retry notice is still on the card when it is read back.
        var marker = "FLAKYNOTICE" + Guid.NewGuid().ToString("N");
        FakeLlmHandler.FlakyRemaining[marker] = 1;
        FakeLlmHandler.FlakyHoldMs[marker] = 4_000;

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = marker + " hello", modelId, permissionMode = "full" });

        var session = await WaitFor(http, sessionId, s => RetryCard(s).HasValue);
        var retry = RetryCard(session)!.Value;
        Assert.Equal(2, retry.GetProperty("attempt").GetInt32());
        Assert.Equal(Sofia.Server.Providers.RetryPolicy.MaxAttempts, retry.GetProperty("attempts").GetInt32());
        Assert.Contains("Client Closed Request", retry.GetProperty("error").GetString());

        // The attempt that answers clears the notice.
        session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        var last = session.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("Recovered", last.GetProperty("text").GetString());
        Assert.False(last.TryGetProperty("retry", out _));
    }

    private const string NoteText = "Tests run with dotnet test Sofia.slnx here.";

    /// <summary>The retry notice currently carried by a message of the session, when a step is being repeated.</summary>
    private static JsonElement? RetryCard(JsonElement session)
    {
        foreach (var m in session.GetProperty("messages").EnumerateArray())
            if (m.TryGetProperty("retry", out var retry)) return retry.Clone();
        return null;
    }

    private static int Count(JsonElement session, Func<JsonElement, bool> predicate) =>
        session.GetProperty("messages").EnumerateArray().Count(predicate);

    /// <summary>
    /// The system prompt of every agent request whose body contains the marker and none of the exclusions. The knowledge
    /// review replays the transcript, so its own request is filtered out too.
    /// </summary>
    private static List<string> ConversationSystems(string marker, params string[] exclude)
    {
        var prompts = new List<string>();
        foreach (var body in FakeLlmHandler.Bodies.Where(b => b.Contains(marker, StringComparison.Ordinal)
                     && !b.Contains("curate a small knowledge base", StringComparison.Ordinal)
                     && !exclude.Any(e => b.Contains(e, StringComparison.Ordinal))))
        {
            var system = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray()
                .First(m => m.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString()!;
            prompts.Add(system);
        }
        return prompts;
    }

    [Fact]
    public async Task ConfigurationRoundTripsTheNewSettings()
    {
        var http = _factory.CreateClient();
        var patched = await http.PatchAsJsonAsync("/api/config", new { keepScreenAwake = true, knowledge = false, searchProvider = "searxng", searxngUrl = "http://localhost:8080" });
        patched.EnsureSuccessStatusCode();
        var config = await http.GetFromJsonAsync<JsonElement>("/api/config");
        Assert.True(config.GetProperty("keepScreenAwake").GetBoolean());
        Assert.False(config.GetProperty("knowledge").GetBoolean());
        Assert.Equal("searxng", config.GetProperty("searchProvider").GetString());
        Assert.Equal("http://localhost:8080", config.GetProperty("searxngUrl").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/config", new { searchProvider = "bing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/config", new { searxngUrl = "not a url" })).StatusCode);
        await http.PatchAsJsonAsync("/api/config", new { keepScreenAwake = false });
    }

    [Fact]
    public async Task ArchivesAndRestoresAConversation()
    {
        var http = _factory.CreateClient();
        var workspace = await Post(http, "/api/workspaces", new { kind = "chat", name = "Group" });
        var wsId = workspace.GetProperty("id").GetString()!;
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        var archived = await Post(http, $"/api/sessions/{sessionId}/archive", new { });
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.True(archived.TryGetProperty("archivedAt", out _));

        // It stays in the workspace payload (the UI parks it in the Archived folder) with the flag set.
        var tree = await http.GetFromJsonAsync<JsonElement>("/api/workspaces");
        var convs = tree.EnumerateArray().First(w => w.GetProperty("id").GetString() == wsId).GetProperty("conversations");
        Assert.True(convs.EnumerateArray().Single(c => c.GetProperty("id").GetString() == sessionId).GetProperty("archived").GetBoolean());

        var restored = await Post(http, $"/api/sessions/{sessionId}/unarchive", new { });
        Assert.False(restored.GetProperty("archived").GetBoolean());
        Assert.False(restored.TryGetProperty("archivedAt", out _));

        // An archived conversation can still be deleted for good.
        await Post(http, $"/api/sessions/{sessionId}/archive", new { });
        var deleted = await http.DeleteAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        tree = await http.GetFromJsonAsync<JsonElement>("/api/workspaces");
        Assert.Equal(0, tree.EnumerateArray().First(w => w.GetProperty("id").GetString() == wsId).GetProperty("conversations").GetArrayLength());
    }

    [Fact]
    public async Task ReordersWorkspacesAndConversations()
    {
        var http = _factory.CreateClient();
        var folder = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var folderId = folder.GetProperty("id").GetString()!;
        var chatA = await Post(http, "/api/workspaces", new { kind = "chat", name = "A" });
        var chatB = await Post(http, "/api/workspaces", new { kind = "chat", name = "B" });
        var chatAId = chatA.GetProperty("id").GetString()!;
        var chatBId = chatB.GetProperty("id").GetString()!;

        // The tree order is manual: whatever the client sends comes back from GET.
        var reordered = await http.PutAsJsonAsync("/api/workspaces/order", new { ids = new[] { chatBId, chatAId, folderId } });
        Assert.Equal(HttpStatusCode.NoContent, reordered.StatusCode);
        var tree = (await http.GetFromJsonAsync<JsonElement>("/api/workspaces")).EnumerateArray().ToList();
        Assert.Equal([chatBId, chatAId, folderId], tree.Select(w => w.GetProperty("id").GetString()));

        // Ids the server does not know are ignored instead of dropping the tree.
        await http.PutAsJsonAsync("/api/workspaces/order", new { ids = new[] { folderId, chatAId, chatBId, "ws_missing" } });
        tree = (await http.GetFromJsonAsync<JsonElement>("/api/workspaces")).EnumerateArray().ToList();
        Assert.Equal([folderId, chatAId, chatBId], tree.Select(w => w.GetProperty("id").GetString()));

        // Conversations keep their own manual order inside a workspace.
        var second = await Post(http, $"/api/workspaces/{folderId}/sessions", new { title = "second" });
        var secondId = second.GetProperty("id").GetString()!;
        var firstId = tree.First(w => w.GetProperty("id").GetString() == folderId)
            .GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var sessions = await http.PutAsJsonAsync($"/api/workspaces/{folderId}/sessions/order", new { ids = new[] { secondId, firstId } });
        Assert.Equal(HttpStatusCode.NoContent, sessions.StatusCode);
        tree = (await http.GetFromJsonAsync<JsonElement>("/api/workspaces")).EnumerateArray().ToList();
        var convIds = tree.First(w => w.GetProperty("id").GetString() == folderId).GetProperty("conversations")
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal([secondId, firstId], convIds);
    }

    /// <summary>
    /// A new conversation starts on the workspace's base model, not on whatever model the workspace's last chat used;
    /// a base model that Settings switched off falls back to the configured default.
    /// </summary>
    [Fact]
    public async Task NewChatsStartOnTheWorkspaceModelAndSkipDisabledModels()
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var providerId = provider.GetProperty("id").GetString()!;
        var first = $"{providerId}/fake-model";
        var baseModel = $"{providerId}/other-model";
        await http.PatchAsJsonAsync("/api/config", new { defaultModel = first });

        var workspace = await Post(http, "/api/workspaces", new { kind = "chat", name = "Base model", modelId = baseModel });
        var wsId = workspace.GetProperty("id").GetString()!;
        var firstConvId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        // The chat that exists switches to another model, so it is the odd one out from now on.
        await http.PatchAsJsonAsync($"/api/sessions/{firstConvId}", new { modelId = first });
        var switched = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{firstConvId}");
        Assert.Equal(first, switched.GetProperty("modelId").GetString());

        // Every later chat opens on the workspace's model.
        var fresh = await Post(http, $"/api/workspaces/{wsId}/sessions", new { title = "fresh" });
        Assert.Equal(baseModel, fresh.GetProperty("modelId").GetString());

        // Hide that model in Settings: a new chat falls back to the server default instead of keeping it.
        var hide = await http.PostAsJsonAsync("/api/models/enabled", new { ids = new[] { baseModel }, enabled = false });
        hide.EnsureSuccessStatusCode();
        var fallback = await Post(http, $"/api/workspaces/{wsId}/sessions", new { title = "hidden model" });
        Assert.Equal(first, fallback.GetProperty("modelId").GetString());

        // The existing chat keeps its own choice: the base model only ever applies to a new one.
        var stillFirst = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{firstConvId}");
        Assert.Equal(first, stillFirst.GetProperty("modelId").GetString());

        await http.PostAsJsonAsync("/api/models/enabled", new { ids = new[] { baseModel }, enabled = true });
        await http.PatchAsJsonAsync("/api/config", new { defaultModel = "" });
    }

    [Fact]
    public async Task ListsCustomAgentsAndHandsMentionsToTheModel()
    {
        var data = Path.Combine(_home, "data");
        Directory.CreateDirectory(Path.Combine(data, "agents"));
        File.WriteAllText(Path.Combine(data, "agents", "reviewer.md"), "---\ndescription: Reviews code\n---\nYou review code carefully.");
        var http = _factory.CreateClient();

        var agents = await http.GetFromJsonAsync<JsonElement>($"/api/agents?directory={Uri.EscapeDataString(_project)}");
        var reviewer = agents.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "reviewer");
        Assert.Equal("subagent", reviewer.GetProperty("mode").GetString());
        Assert.Equal("global", reviewer.GetProperty("source").GetString());
        Assert.Equal(["build", "plan"], agents.EnumerateArray().Where(a => a.GetProperty("mode").GetString() == "primary").Take(2).Select(a => a.GetProperty("id").GetString()));

        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "please ask @reviewer to look at hello.txt", modelId, permissionMode = "read-only" });
        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle" && s.GetProperty("messages").EnumerateArray().Any(m => m.TryGetProperty("text", out var t) && t.GetString() == "Done"));

        // The user sees what they typed; the model also gets the delegation note and `reviewer` as a task target.
        Assert.Equal("please ask @reviewer to look at hello.txt", session.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Contains(FakeLlmHandler.Bodies, b => b.Contains("agent_mentions", StringComparison.Ordinal) && b.Contains("@reviewer", StringComparison.Ordinal));
        Assert.Contains(FakeLlmHandler.Bodies, b => b.Contains("(Reviews code)", StringComparison.Ordinal));

        var change = await http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new { agent = "nobody" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new { agent = "reviewer" })).StatusCode);
    }

    [Fact]
    public async Task RunsCustomCommandsWithTheirAgentAndArguments()
    {
        var data = Path.Combine(_home, "data");
        Directory.CreateDirectory(Path.Combine(data, "commands"));
        File.WriteAllText(Path.Combine(data, "commands", "greet.md"), "---\ndescription: Greets\nagent: plan\n---\nSay hi to $1 politely");
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        await http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new { modelId, permissionMode = "read-only" });

        var commands = await http.GetFromJsonAsync<JsonElement>($"/api/commands?directory={Uri.EscapeDataString(_project)}");
        Assert.Equal("plan", commands.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "greet").GetProperty("agent").GetString());

        await Post(http, $"/api/sessions/{sessionId}/command", new { name = "greet", arguments = "Bob" });
        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle" && s.GetProperty("messages").EnumerateArray().Any(m => m.TryGetProperty("text", out var t) && t.GetString() == "Done"));
        Assert.Equal("plan", session.GetProperty("agent").GetString());
        Assert.Equal("/greet Bob", session.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Contains(FakeLlmHandler.Bodies, b => b.Contains("Say hi to Bob politely", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync($"/api/sessions/{sessionId}/command", new { name = "missing" })).StatusCode);
    }

    [Fact]
    public async Task SofiaInfoToolExplainsTheSetupWithoutLeakingSecrets()
    {
        var data = Path.Combine(_home, "data");
        Directory.CreateDirectory(Path.Combine(data, "skills", "deploy"));
        File.WriteAllText(Path.Combine(data, "skills", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: Ship it\n---\nSteps");
        var http = _factory.CreateClient();
        await http.GetAsync("/api/config"); // boots the host
        var db = _factory.Services.GetRequiredService<Sofia.Server.Data.SofiaDb>();
        db.McpServers.Insert(new Sofia.Server.Domain.McpServerConfig
        {
            Id = "mcp_test", Name = "demo", Command = "node server.js --api-key SECRET-ARG", Token = "SECRET-TOKEN",
            Env = new Dictionary<string, string> { ["KEY"] = "SECRET-ENV" }, Enabled = false,
        });
        db.Providers.Insert(new Sofia.Server.Domain.ProviderConfig { Id = "p_test", Name = "Keyed", BaseUrl = "https://user:SECRET-URL@api.example.com/v1?key=SECRET-QUERY", ApiKey = "SECRET-KEY", Enabled = false });

        var workspace = new Sofia.Server.Domain.Workspace { Id = "ws", Kind = "folder", Path = _project };
        var registry = _factory.Services.GetRequiredService<Sofia.Server.Agent.ToolRegistry>();
        var tool = registry.For(Sofia.Server.Agent.Agents.Get("build"), workspace).Single(t => t.Name == "sofia_info");
        var context = new Sofia.Server.Tools.ToolContext
        {
            Session = null!, Workspace = workspace, Cwd = _project, Message = null!, Call = null!, Config = null!, Changes = null!, Host = null!,
        };

        async Task<Sofia.Server.Tools.ToolResult> Call(string topic) => await tool.ExecuteAsync(context, new System.Text.Json.Nodes.JsonObject { ["topic"] = topic }, CancellationToken.None);
        var outputs = new Dictionary<string, string>();
        foreach (var topic in new[] { "overview", "skills", "commands", "agents", "mcp", "providers", "config", "context" })
            outputs[topic] = (await Call(topic)).Output;
        var all = string.Join("\n", outputs.Values);

        Assert.Contains(Path.Combine(data, "skills"), outputs["overview"]);
        Assert.Contains("- deploy [global]", outputs["skills"]);
        Assert.Contains("demo (id mcp_test)", outputs["mcp"]);
        Assert.Contains("node (+3 args)", outputs["mcp"]);
        Assert.Contains("https://api.example.com/v1", outputs["providers"]);
        Assert.DoesNotContain("SECRET", all);
        Assert.True((await Call("nope")).IsError);
    }

    [Fact]
    public async Task RejectsForeignBrowserOrigins()
    {
        var http = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces");
        request.Headers.Add("Origin", "https://evil.example");
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<JsonElement> Post(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} → {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitFor(HttpClient http, string sessionId, Func<JsonElement, bool> predicate, int tries = 100)
    {
        JsonElement last = default;
        for (var i = 0; i < tries; i++)
        {
            last = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            if (predicate(last)) return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Session never reached the expected state: {last}");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_home, true); } catch { /* LiteDB may still hold the file briefly */ }
    }
}
