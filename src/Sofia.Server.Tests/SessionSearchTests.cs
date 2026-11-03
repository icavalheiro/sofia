using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Search;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

public class SessionSearchIndexTests
{
    private static SofiaDb Db() => new(new LiteDB.ConnectionString { Filename = ":memory:" });

    private static (Session S, Workspace W) AddSession(SofiaDb db, string id, string workspaceId, bool archived = false, string? parentId = null)
    {
        var w = new Workspace { Id = workspaceId, Name = workspaceId, Kind = "chat" };
        db.Workspaces.Upsert(w);
        var s = new Session { Id = id, WorkspaceId = workspaceId, Title = "t-" + id, Archived = archived, ParentId = parentId, Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Sessions.Upsert(s);
        return (s, w);
    }

    private static Message AddMessage(SofiaDb db, string sessionId, string role, string text)
    {
        var m = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = sessionId, Role = role, Text = text, CreatedAt = DateTime.UtcNow };
        m.Seq = db.NextSeq();
        db.Messages.Insert(m);
        return m;
    }

    [Fact]
    public void TokenizerFoldsDiacriticsAndCase()
    {
        Assert.Equal(new[] { "cafe", "menu" }, SessionSearchIndex.Tokenize("Café MENU").ToList());
        Assert.Equal(new[] { "naïve", "résumé" }.Select(SessionSearchIndex.Fold), SessionSearchIndex.Tokenize("Naïve Résumé").ToList());
    }

    [Fact]
    public void TokenizerSplitsOnNonAlnum()
    {
        Assert.Equal(new[] { "hello", "world", "42" }, SessionSearchIndex.Tokenize("hello, world! 42").ToList());
        // CJK runs become character bigrams.
        Assert.Equal(new[] { "搜索", "索引", "引擎" }, SessionSearchIndex.Tokenize("搜索引擎").ToList());
        // A script switch ends the current run.
        Assert.Equal(new[] { "abc", "搜索", "def" }, SessionSearchIndex.Tokenize("abc搜索def").ToList());
    }

    [Fact]
    public void Bm25RanksMoreRelevantDocFirst()
    {
        using var db = Db();
        var (s1, _) = AddSession(db, "s1", "ws");
        var (s2, _) = AddSession(db, "s2", "ws");
        // s1 mentions both terms; s2 only one.
        AddMessage(db, "s1", "user", "alpha beta alpha");
        AddMessage(db, "s2", "user", "alpha gamma");

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);

        var results = index.Search(db, "alpha beta", null, false, false, 10);
        Assert.Equal(2, results.Count);
        Assert.Equal("s1", results[0].SessionId);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void PrefixMatchesOnlyTheLastTerm()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        AddMessage(db, "s1", "user", "alpha alphabets are fun");

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);

        // "alph" is not an exact posting key, but as the last term it expands to alpha/alphabets.
        Assert.Single(index.Search(db, "alph", null, false, false, 10));
        // As a non-last term it must match exactly, so "alph beta" finds nothing.
        Assert.Empty(index.Search(db, "alph beta", null, false, false, 10));
    }

    [Fact]
    public void QuotedPhraseIsMatchedVerbatim()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        AddMessage(db, "s1", "user", "the quick brown fox");
        AddSession(db, "s2", "ws");
        AddMessage(db, "s2", "user", "quick and the fox");

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);

        var results = index.Search(db, "\"the quick\"", null, false, false, 10);
        var hit = Assert.Single(results);
        Assert.Equal("s1", hit.SessionId);
    }

    [Fact]
    public void WorkspaceArchivedAndSubagentFiltersApply()
    {
        using var db = Db();
        AddSession(db, "a", "wsA");
        AddMessage(db, "a", "user", "markerword here");
        AddSession(db, "b", "wsB");
        AddMessage(db, "b", "user", "another markerword");
        var (arch, _) = AddSession(db, "arch", "wsA", archived: true);
        AddMessage(db, "arch", "user", "markerword in an archived chat");
        var (sub, _) = AddSession(db, "sub", "wsA", parentId: "a");
        AddMessage(db, "sub", "user", "markerword inside a subagent");

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);

        // Only non-archived, non-subagent sessions of wsA by default.
        var def = index.Search(db, "markerword", "wsA", false, false, 10);
        Assert.Equal(["a"], def.Select(r => r.SessionId).ToList());

        // All workspaces when the filter is omitted.
        Assert.Contains(index.Search(db, "markerword", null, false, false, 10), r => r.SessionId == "b");

        // Archived only with the flag.
        var archResults = index.Search(db, "markerword", "wsA", true, false, 10);
        Assert.Contains(archResults, r => r.SessionId == "arch");

        // Subagents only with the flag.
        Assert.Contains(index.Search(db, "markerword", "wsA", false, true, 10), r => r.SessionId == "sub");
    }

    [Fact]
    public void SnippetHasNonOverlappingHighlightsAroundTheHit()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        // The hit sits well past the 80-char radius so the snippet must be a window around it.
        AddMessage(db, "s1", "user", " " + new string('x', 200) + " zebra " + new string('y', 50));

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);

        var hit = Assert.Single(index.Search(db, "zebra", null, false, false, 10)).Hits[0];
        Assert.Contains("zebra", hit.Snippet);
        // The snippet is a ~160-char window (SnippetWidth) around the first hit.
        Assert.True(hit.Snippet.Length <= 200, $"snippet too long: {hit.Snippet.Length}");
        Assert.NotEmpty(hit.Highlights);
        var sorted = hit.Highlights.OrderBy(h => h[0]).ToList();
        for (var i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i][0] >= sorted[i - 1][0] + sorted[i - 1][1], "highlights overlap");
        // The highlight must actually cover the word inside the snippet.
        var at = hit.Snippet.IndexOf("zebra", StringComparison.Ordinal);
        Assert.Contains(sorted, h => h[0] == at && h[1] == "zebra".Length);
    }

    [Fact]
    public void RemoveAndRemoveSessionPurgePostings()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        var m1 = AddMessage(db, "s1", "user", "uniqueword one");
        AddMessage(db, "s1", "user", "uniqueword two");

        var index = new SessionSearchIndex();
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);
        Assert.Equal(2, Assert.Single(index.Search(db, "uniqueword", null, false, false, 10)).Hits.Count);

        index.Remove(m1.Id);
        Assert.Single(Assert.Single(index.Search(db, "uniqueword", null, false, false, 10)).Hits);

        index.RemoveSession("s1");
        Assert.Empty(index.Search(db, "uniqueword", null, false, false, 10));
    }

    [Fact]
    public async Task RebuildEqualsIncremental()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        AddSession(db, "s2", "ws");
        var m1 = AddMessage(db, "s1", "user", "sharedterm first");
        var m2 = AddMessage(db, "s2", "user", "sharedterm second");

        // Incremental index: upsert each message as it "arrives".
        var incremental = new SessionSearchIndex();
        incremental.Upsert(m1);
        incremental.Upsert(m2);

        // Rebuild index: one pass over the DB.
        var rebuilt = new SessionSearchIndex();
        await rebuilt.RebuildAsync(db);

        var a = incremental.Search(db, "sharedterm", null, false, false, 10).Select(r => (r.SessionId, r.Hits.Count)).ToList();
        var b = rebuilt.Search(db, "sharedterm", null, false, false, 10).Select(r => (r.SessionId, r.Hits.Count)).ToList();
        Assert.Equal(a, b);

        // Mutate the DB and mirror it incrementally; both indexes must still agree.
        var m3 = AddMessage(db, "s2", "user", "sharedterm third");
        incremental.Upsert(m3);
        await rebuilt.RebuildAsync(db);

        var a2 = incremental.Search(db, "sharedterm", null, false, false, 10).Select(r => (r.SessionId, r.Hits.Count)).ToList();
        var b2 = rebuilt.Search(db, "sharedterm", null, false, false, 10).Select(r => (r.SessionId, r.Hits.Count)).ToList();
        Assert.Equal(a2, b2);
    }

    [Fact]
    public async Task HandlerIndexesCreatedUpdatedAndDeletedMessages()
    {
        using var db = Db();
        AddSession(db, "s1", "ws");
        var bus = new EventBus();
        var index = new SessionSearchIndex();
        var handler = new SearchIndexHandler(index, db);

        // Events are serialized with the server's JSON options (camelCase), like EventBus.Publish does.
        // Created: indexed immediately.
        var m = AddMessage(db, "s1", "user", "eventdriven word");
        await handler.HandleAsync(new SofiaEvent(EventTypes.MessageCreated, "s1", "ws", JsonSerializer.SerializeToElement(m, Json.Options)), CancellationToken.None);
        Assert.Single(index.Search(db, "eventdriven", null, false, false, 10));

        // Updated: debounced; the reindex reads the message back from the DB.
        m.Text = "eventdriven changed";
        db.Messages.Upsert(m);
        await handler.HandleAsync(new SofiaEvent(EventTypes.MessageUpdated, "s1", "ws", JsonSerializer.SerializeToElement(m, Json.Options)), CancellationToken.None);
        Assert.True(await Poll(() => index.Search(db, "changed", null, false, false, 10).Count == 1), "debounced update did not land");

        // Deleted: removed immediately.
        db.Messages.Delete(m.Id);
        await handler.HandleAsync(new SofiaEvent(EventTypes.MessageDeleted, "s1", "ws", JsonSerializer.SerializeToElement(new { ids = new[] { m.Id } })), CancellationToken.None);
        Assert.Empty(index.Search(db, "eventdriven", null, false, false, 10));

        // Session deleted: every message of the session is purged.
        var m2 = AddMessage(db, "s1", "user", "stillhere");
        await handler.HandleAsync(new SofiaEvent(EventTypes.MessageCreated, "s1", "ws", JsonSerializer.SerializeToElement(m2, Json.Options)), CancellationToken.None);
        Assert.Single(index.Search(db, "stillhere", null, false, false, 10));
        db.Messages.DeleteMany(x => x.SessionId == "s1");
        await handler.HandleAsync(new SofiaEvent(EventTypes.SessionDeleted, "s1", "ws", JsonSerializer.SerializeToElement(new { id = "s1" })), CancellationToken.None);
        Assert.Empty(index.Search(db, "stillhere", null, false, false, 10));

        static async Task<bool> Poll(Func<bool> check)
        {
            for (var i = 0; i < 100; i++)
                if (check()) return true;
                else await Task.Delay(50);
            return check();
        }
    }
}

public class SessionSearchToolTests
{
    private static SofiaDb Db() => new(new LiteDB.ConnectionString { Filename = ":memory:" });

    private static ToolContext Context(string workspaceId, string sessionId) => new()
    {
        Session = new Session { Id = sessionId, WorkspaceId = workspaceId, Agent = "build" },
        Workspace = new Workspace { Id = workspaceId, Kind = "chat" }, Cwd = "", Message = null!, Call = null!,
        Config = null!, Changes = null!, Host = null!,
    };

    private static (SessionSearchTool Search, SessionReadTool Read, SessionSearchIndex Index, SofiaDb Db) Create()
    {
        var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:" });
        var index = new SessionSearchIndex();
        return (new SessionSearchTool(index, db), new SessionReadTool(db), index, db);
    }

    private static void IndexAll(SessionSearchIndex index, SofiaDb db)
    {
        foreach (var m in db.Messages.FindAll()) index.Upsert(m);
    }

    [Fact]
    public async Task SearchExcludesTheCallingSessionAndHonoursScope()
    {
        var (search, _, index, db) = Create();
        using var _ = db;
        // Two sessions in wsA, one in wsB.
        foreach (var (id, ws, text) in new[] { ("a", "wsA", "zebra stripes"), ("b", "wsA", "zebra crossing"), ("c", "wsB", "zebra elsewhere") })
        {
            db.Workspaces.Upsert(new Workspace { Id = ws, Name = ws, Kind = "chat" });
            db.Sessions.Upsert(new Session { Id = id, WorkspaceId = ws, Title = "t-" + id, Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            var m = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = id, Role = "user", Text = text, CreatedAt = DateTime.UtcNow };
            m.Seq = db.NextSeq();
            db.Messages.Insert(m);
        }

        IndexAll(index, db);

        var ctxA = Context("wsA", "a");
        var result = await search.ExecuteAsync(ctxA, Args.Parse("""{"query":"zebra"}"""), CancellationToken.None);
        Assert.False(result.IsError);
        // The calling session 'a' is excluded; workspace scope keeps only wsA.
        Assert.DoesNotContain("t-a", result.Output);
        Assert.Contains("t-b", result.Output);
        Assert.DoesNotContain("t-c", result.Output);

        var all = await search.ExecuteAsync(ctxA, Args.Parse("""{"query":"zebra","scope":"all"}"""), CancellationToken.None);
        Assert.Contains("t-c", all.Output);
    }

    [Fact]
    public async Task ReadHonoursRangeBoundsAndWorkspaceScope()
    {
        var (_, read, _, db) = Create();
        using var _ = db;
        db.Workspaces.Upsert(new Workspace { Id = "wsA", Name = "wsA", Kind = "chat" });
        db.Sessions.Upsert(new Session { Id = "a", WorkspaceId = "wsA", Title = "t-a", Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var ids = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            var m = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = "a", Role = i % 2 == 0 ? "assistant" : "user", Text = $"line {i}", CreatedAt = DateTime.UtcNow };
            m.Seq = db.NextSeq();
            db.Messages.Insert(m);
            ids.Add(m.Id);
        }

        var ctxA = Context("wsA", "other");
        // Default: first 20 (here all 5), in seq order.
        var all = await read.ExecuteAsync(ctxA, Args.Parse("""{"sessionId":"a"}"""), CancellationToken.None);
        Assert.Contains("line 1", all.Output);
        Assert.Contains("line 5", all.Output);
        Assert.Contains("<session_transcript", all.Output);

        // fromSeq limits the lower bound.
        var from = await read.ExecuteAsync(ctxA, Args.Parse("""{"sessionId":"a","fromSeq":4}"""), CancellationToken.None);
        Assert.DoesNotContain("line 3", from.Output);
        Assert.Contains("line 4", from.Output);

        // around a message id reads a window centred on it.
        var aroundArgs = "{\"sessionId\":\"a\",\"around\":\"" + ids[2] + "\",\"maxMessages\":3}";
        var around = await read.ExecuteAsync(ctxA, Args.Parse(aroundArgs), CancellationToken.None);
        Assert.Contains("line 3", around.Output);

        // maxMessages caps the output.
        var capped = await read.ExecuteAsync(ctxA, Args.Parse("""{"sessionId":"a","maxMessages":2}"""), CancellationToken.None);
        Assert.Contains("line 1", capped.Output);
        Assert.DoesNotContain("line 3", capped.Output);

        // Another workspace is refused unless scope is 'all'.
        var ctxB = Context("wsB", "other");
        var denied = await read.ExecuteAsync(ctxB, Args.Parse("""{"sessionId":"a"}"""), CancellationToken.None);
        Assert.True(denied.IsError);
        var allowed = await read.ExecuteAsync(ctxB, Args.Parse("""{"sessionId":"a","scope":"all"}"""), CancellationToken.None);
        Assert.False(allowed.IsError);
    }

    [Fact]
    public async Task BothToolsRedactSecretLikeStrings()
    {
        var (_, read, index, db) = Create();
        using var _ = db;
        db.Workspaces.Upsert(new Workspace { Id = "wsA", Name = "wsA", Kind = "chat" });
        db.Sessions.Upsert(new Session { Id = "a", WorkspaceId = "wsA", Title = "t-a", Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz123456";
        var m = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = "a", Role = "user", Text = $"key is {secret} ok", CreatedAt = DateTime.UtcNow };
        m.Seq = db.NextSeq();
        db.Messages.Insert(m);
        IndexAll(index, db);

        var ctxA = Context("wsA", "other");
        var r1 = await read.ExecuteAsync(ctxA, Args.Parse("""{"sessionId":"a"}"""), CancellationToken.None);
        Assert.DoesNotContain(secret, r1.Output);
        Assert.Contains("[redacted]", r1.Output);

        // The search tool redacts snippets too.
        var (search2, _, index2, db2) = Create();
        using (db2)
        {
            db2.Workspaces.Upsert(new Workspace { Id = "wsA", Name = "wsA", Kind = "chat" });
            db2.Sessions.Upsert(new Session { Id = "a", WorkspaceId = "wsA", Title = "t-a", Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            var m2 = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = "a", Role = "user", Text = $"key is {secret} ok", CreatedAt = DateTime.UtcNow };
            m2.Seq = db2.NextSeq();
            db2.Messages.Insert(m2);
            IndexAll(index2, db2);
            var r2 = await search2.ExecuteAsync(Context("wsA", "other"), Args.Parse("""{"query":"key"}"""), CancellationToken.None);
            Assert.DoesNotContain(secret, r2.Output);
            Assert.Contains("[redacted]", r2.Output);
        }
    }

    [Fact]
    public void BothToolsAreReadKind()
    {
        var (search, read, _, _) = Create();
        Assert.Equal(ToolKind.Read, search.Kind);
        Assert.Equal(ToolKind.Read, read.Kind);
    }
}

public class SessionSearchEndpointTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sofia-search-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public SessionSearchEndpointTests()
    {
        // The data dir comes from host configuration, not the SOFIA_HOME env var, so these tests never
        // touch process-global state and can run in parallel with every other test class.
        Directory.CreateDirectory(_home);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Sofia:DataDir", Path.Combine(_home, "data"))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
                services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
                services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private static async Task<JsonElement> Post(HttpClient http, string url, object body)
    {
        var res = await http.PostAsJsonAsync(url, body);
        Assert.True(res.IsSuccessStatusCode, $"{url} -> {(int)res.StatusCode}");
        return (await res.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task<string> NewWorkspace(HttpClient http, string marker)
    {
        var ws = await Post(http, "/api/workspaces", new { kind = "chat" });
        var id = ws.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        // Prompt the session with a unique marker so it has an indexed message.
        await Post(http, $"/api/sessions/{id}/prompt", new { text = marker, modelId = "fake/fake-model", permissionMode = "full" });
        return id;
    }

    private static async Task<JsonElement> Search(HttpClient http, string q, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}";
        if (workspaceId is not null) url += $"&workspaceId={workspaceId}";
        if (includeArchived) url += "&includeArchived=true";
        if (includeSubagents) url += "&includeSubagents=true";
        var res = await http.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url} -> {(int)res.StatusCode}");
        return (await res.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    /// <summary>Polls the search endpoint until the index catches up (the handler runs off the agent loop).</summary>
    private static async Task<JsonElement> SearchUntil(HttpClient http, Func<JsonElement, bool> ok, string q, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false)
    {
        for (var i = 0; i < 150; i++)
        {
            var el = await Search(http, q, workspaceId, includeArchived, includeSubagents);
            if (ok(el)) return el;
            await Task.Delay(100);
        }
        throw new TimeoutException($"search for '{q}' did not converge");
    }

    [Fact]
    public async Task EndpointReturns400OnEmptyQueryAnd404OnUnknownWorkspace()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)(await http.GetAsync("/api/search?q=")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (HttpStatusCode)(await http.GetAsync("/api/search?q=hello&workspaceId=nope")).StatusCode);
    }

    [Fact]
    public async Task EndpointReturnsRankedResultsFilteredByWorkspaceArchivedAndSubagents()
    {
        var http = _factory.CreateClient();
        // A provider so the prompt can run (the fake LLM answers without tools).
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";

        var wsA = await Post(http, "/api/workspaces", new { kind = "chat" });
        var wsB = await Post(http, "/api/workspaces", new { kind = "chat" });
        var idA = wsA.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var idB = wsB.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var wsBid = wsB.GetProperty("id").GetString()!;

        await Post(http, $"/api/sessions/{idA}/prompt", new { text = "zebra stripes are unique", modelId, permissionMode = "full" });
        await Post(http, $"/api/sessions/{idB}/prompt", new { text = "quokka hops around", modelId, permissionMode = "full" });

        // Wait for the index to pick up both.
        var a = await SearchUntil(http, el => el.GetProperty("results").EnumerateArray().Any(r => r.GetProperty("sessionId").GetString() == idA), "zebra");
        Assert.Equal(idA, Assert.Single(a.GetProperty("results").EnumerateArray().ToArray()).GetProperty("sessionId").GetString());

        // Workspace filter: wsB does not see zebra.
        var b = await Search(http, "zebra", wsBid);
        Assert.Empty(b.GetProperty("results").EnumerateArray().ToArray());

        // Archived session is excluded by default and included with the flag.
        await Post(http, $"/api/sessions/{idA}/archive", new { });
        var afterArchive = await SearchUntil(http, el => el.GetProperty("results").EnumerateArray().Count() == 0, "zebra");
        Assert.Empty(afterArchive.GetProperty("results").EnumerateArray().ToArray());
        var withArchived = await Search(http, "zebra", includeArchived: true);
        Assert.Contains(withArchived.GetProperty("results").EnumerateArray(), r => r.GetProperty("sessionId").GetString() == idA);

        // Subagent sessions are excluded by default and included with the flag. A subagent is a session whose
        // ParentId points at another session, so inject one directly and let the event handler index it.
        var services = _factory.Services;
        var db = services.GetRequiredService<SofiaDb>();
        var bus = services.GetRequiredService<IEventBus>();
        const string subId = "sub-test";
        db.Sessions.Upsert(new Session { Id = subId, WorkspaceId = idB, Title = "wombat burrows", ParentId = idB, Status = "idle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var subMsg = new Message { Id = Guid.NewGuid().ToString("N"), SessionId = subId, Role = "user", Text = "wombat burrows are deep", CreatedAt = DateTime.UtcNow };
        subMsg.Seq = db.NextSeq();
        db.Messages.Insert(subMsg);
        bus.Publish(EventTypes.MessageCreated, subMsg, subId, idB);

        var noSub = await SearchUntil(http, el => el.GetProperty("results").EnumerateArray().Count() == 0, "wombat");
        Assert.Empty(noSub.GetProperty("results").EnumerateArray().ToArray());
        var withSub = await Search(http, "wombat", includeSubagents: true);
        Assert.Contains(withSub.GetProperty("results").EnumerateArray(), r => r.GetProperty("sessionId").GetString() == subId);
    }
}
