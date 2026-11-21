using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Contracts.Domain;

namespace Sofia.Core.Tests;

/// <summary>OpenAI-compatible endpoint that answers plain text and records every chat-completion body.</summary>
internal sealed class PlainLlmHandler : HttpMessageHandler
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Bodies = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (!url.StartsWith("http://plain.llm/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.EndsWith("/models", StringComparison.Ordinal))
            return Json("""{"data":[{"id":"fake-model","context_length":16384}]}""");

        var body = await request.Content!.ReadAsStringAsync(ct);
        Bodies.Add(body);
        // The knowledge review parses JSON; everything else just needs a text answer.
        var text = body.Contains("curate a small knowledge base", StringComparison.Ordinal) ? "[]" : "ack";
        var sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text } } } }) + "\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// Reverting (or branching) before a compaction used to leave the transcript on screen while the model saw an empty
/// conversation: the folded messages stayed flagged as compacted and the summary that replaced them was cut away.
/// </summary>
public sealed class CompactionRevertTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sofia-revert-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly WebApplicationFactory<Program> _factory;

    public CompactionRevertTests()
    {
        _project = Path.Combine(_home, "project");
        Directory.CreateDirectory(_project);
        PlainLlmHandler.Bodies.Clear();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Sofia:DataDir", Path.Combine(_home, "data"))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
                services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new PlainLlmHandler());
                services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new PlainLlmHandler());
            }));
    }

    [Fact]
    public async Task RevertingBeforeACompactionBringsTheFoldedMessagesBack()
    {
        var http = _factory.CreateClient();
        var seeded = await SeedAsync(http);
        var db = _factory.Services.GetRequiredService<SofiaDb>();

        var before = (await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{seeded.SessionId}")).GetProperty("context");
        Assert.Equal(before.GetProperty("systemTokens").GetInt32() + before.GetProperty("toolTokens").GetInt32(), before.GetProperty("used").GetInt32());

        // The revert target sits before the compaction: the summary goes away, so the messages it replaced come back.
        await Post(http, $"/api/sessions/{seeded.SessionId}/revert", new { messageId = seeded.Second.Id });

        var survived = db.SessionMessages(seeded.SessionId);
        var unfolded = survived.Single(m => m.Id == seeded.First.Id);
        Assert.False(unfolded.Compacted);
        Assert.Equal(0L, unfolded.CompactedBy);
        Assert.DoesNotContain(survived, m => m.Summary == true);

        // The meter counts the restored history instead of flooring at the frozen prefix.
        var after = (await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{seeded.SessionId}")).GetProperty("context");
        Assert.True(after.GetProperty("used").GetInt32() > before.GetProperty("used").GetInt32(),
            $"used {after.GetProperty("used").GetInt32()} should exceed the prefix floor {before.GetProperty("used").GetInt32()}");

        // ... and the next request actually carries it.
        await Post(http, $"/api/sessions/{seeded.SessionId}/prompt", new { text = "please carry on", permissionMode = "read-only" });
        await WaitFor(http, seeded.SessionId, s => s.GetProperty("status").GetString() == "idle");
        var request = LastRequestBody("please carry on");
        Assert.Contains("first question FIRSTALPHA", request);
        Assert.DoesNotContain("conversation_summary", request);
    }

    [Fact]
    public async Task RevertingAfterACompactionKeepsTheSummaryInCharge()
    {
        var http = _factory.CreateClient();
        var seeded = await SeedAsync(http, legacyPointers: true);
        var db = _factory.Services.GetRequiredService<SofiaDb>();

        // The target is after the compaction: nothing was cut away, so the folded messages stay folded (and the
        // "which compaction folded it" pointer missing on older data must not resurrect them either).
        await Post(http, $"/api/sessions/{seeded.SessionId}/revert", new { messageId = seeded.AfterCompaction.Id });

        var survived = db.SessionMessages(seeded.SessionId);
        Assert.True(survived.Single(m => m.Id == seeded.First.Id).Compacted);
        Assert.Contains(survived, m => m.Summary == true);
    }

    [Fact]
    public async Task BranchingBeforeACompactionUnfoldsTheFoldedMessages()
    {
        var http = _factory.CreateClient();
        var seeded = await SeedAsync(http);
        var db = _factory.Services.GetRequiredService<SofiaDb>();

        var branch = await Post(http, $"/api/sessions/{seeded.SessionId}/branch", new { messageId = seeded.Second.Id });
        var branchId = branch.GetProperty("id").GetString()!;

        var copied = db.SessionMessages(branchId);
        Assert.DoesNotContain(copied, m => m.Compacted);
        Assert.DoesNotContain(copied, m => m.Summary == true);

        await Post(http, $"/api/sessions/{branchId}/prompt", new { text = "continue in this branch", permissionMode = "read-only" });
        await WaitFor(http, branchId, s => s.GetProperty("status").GetString() == "idle");
        Assert.Contains("first question FIRSTALPHA", LastRequestBody("continue in this branch"));
    }

    [Fact]
    public async Task RevertingLegacyCompactedMessagesBeforeTheirSummaryUnfoldsThem()
    {
        var http = _factory.CreateClient();
        var seeded = await SeedAsync(http, legacyPointers: true);
        var db = _factory.Services.GetRequiredService<SofiaDb>();

        // Data written before the pointer existed: with the summary cut away there is nothing left to justify hiding
        // the originals, so they come back too.
        await Post(http, $"/api/sessions/{seeded.SessionId}/revert", new { messageId = seeded.Second.Id });

        var survived = db.SessionMessages(seeded.SessionId);
        Assert.False(survived.Single(m => m.Id == seeded.First.Id).Compacted);
        Assert.DoesNotContain(survived, m => m.Summary == true);
    }

    private sealed record Seeded(string SessionId, Message First, Message Second, Message AfterCompaction);

    /// <summary>
    /// Writes a conversation that a compaction already folded: two messages (the first with a marker the tests look
    /// for in the request body), a compaction notice plus its summary, and one message after the compaction.
    /// </summary>
    private async Task<Seeded> SeedAsync(HttpClient http, bool legacyPointers = false)
    {
        var provider = await Post(http, "/api/providers", new { name = "Plain", type = "openai", baseUrl = "http://plain.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        await http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new { modelId });

        var db = _factory.Services.GetRequiredService<SofiaDb>();
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var session = db.Sessions.FindById(sessionId)!;

        var first = store.AddMessage(session, new Message { Role = "user", Text = "first question FIRSTALPHA" });
        var second = store.AddMessage(session, new Message { Role = "assistant", Text = "first answer" });
        var notice = store.AddMessage(session, new Message
        {
            Role = "assistant",
            Synthetic = true,
            Compaction = new CompactionNotice { Status = "done", Before = 9000, After = 1200 },
        });
        foreach (var folded in new[] { first, second })
        {
            folded.Compacted = true;
            folded.CompactedBy = legacyPointers ? 0 : notice.Seq;
            db.Messages.Update(folded);
        }
        store.AddMessage(session, new Message { Role = "assistant", Summary = true, Text = "SUMMARY: the user asked the first question." });
        var after = store.AddMessage(session, new Message { Role = "assistant", Text = "after compaction" });
        return new Seeded(sessionId, first, second, after);
    }

    /// <summary>The last request body the fake endpoint received for that prompt.</summary>
    private static string LastRequestBody(string marker) =>
        PlainLlmHandler.Bodies.Last(b => b.Contains(marker, StringComparison.Ordinal));

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
        try { Directory.Delete(_home, true); } catch { /* the database may still hold the file briefly */ }
    }
}
