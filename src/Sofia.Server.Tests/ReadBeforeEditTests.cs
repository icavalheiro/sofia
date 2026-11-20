using LiteDB;
using Sofia.Server.Changes;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

/// <summary>The read-before-edit policy: persistence, per-session scope, partial reads, tool-write refresh, toggle.</summary>
public class ReadBeforeEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-readbefore-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EditWithoutAReadFails()
    {
        var (ctx, path) = Setup("a.txt", "one\ntwo\n");

        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("You must `read` a.txt", result.Output);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path));   // untouched
    }

    [Fact]
    public async Task ReadThenEditSucceeds()
    {
        var (ctx, path) = Setup("a.txt", "one\ntwo\n");
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);

        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("one\nTWO\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AnExternalModificationAfterTheReadFails()
    {
        var (ctx, path) = Setup("a.txt", "one\ntwo\n");
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);

        // Simulate an outside writer: change the content and bump the mtime.
        await Task.Delay(20);   // let the filesystem clock move past the recorded mtime
        File.WriteAllText(path, "one\nmodified externally\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("changed on disk since you last read it", result.Output);
    }

    [Fact]
    public async Task APartialReadCounts()
    {
        var (ctx, path) = Setup("a.txt", string.Join("\n", Enumerable.Range(1, 50)) + "\n");
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt","offset":25,"limit":10}"""), CancellationToken.None);

        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"50","newText":"fifty"}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
    }

    [Fact]
    public async Task AToolEditFollowedByASecondEditWorks()
    {
        var (ctx, path) = Setup("a.txt", "one\ntwo\n");
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);

        var first = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);
        Assert.False(first.IsError, first.Output);

        // No re-read: the tool's own write refreshed the tracked state.
        var second = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"TWO","newText":"two again"}"""), CancellationToken.None);
        Assert.False(second.IsError, second.Output);
        Assert.Equal("one\ntwo again\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteToANewFileIsExemptAndAnOverwriteNeedsARead()
    {
        var (ctx, path) = Setup("a.txt", "old content\n");

        // New file: no read needed.
        var create = await new WriteTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"new.txt","content":"fresh"}"""), CancellationToken.None);
        Assert.False(create.IsError, create.Output);

        // Overwrite of the existing file: a read is required first.
        var overwrite = await new WriteTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","content":"replaced"}"""), CancellationToken.None);
        Assert.True(overwrite.IsError);
        Assert.Contains("You must `read` a.txt", overwrite.Output);

        // After reading, the overwrite is allowed.
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);
        var ok = await new WriteTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","content":"replaced"}"""), CancellationToken.None);
        Assert.False(ok.IsError, ok.Output);
    }

    [Fact]
    public async Task TheToggleOffAllowsEverything()
    {
        var (ctx, path) = Setup("a.txt", "one\ntwo\n");
        ctx.Config.ReadBeforeEdit = false;

        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
    }

    [Fact]
    public async Task ASubagentSessionDoesNotInheritTheParentsReads()
    {
        Directory.CreateDirectory(_dir);
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        using var _ = db;
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "one\ntwo\n");

        var parent = Ctx(db, "parent", _dir);
        await new ReadTool().ExecuteAsync(parent, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);

        // The child session has its own id: the parent's read does not carry over.
        var child = Ctx(db, "child", _dir);
        var result = await new EditTool(true).ExecuteAsync(child, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("You must `read` a.txt", result.Output);
    }

    [Fact]
    public async Task RecordsSurviveANewTrackerInstance()
    {
        // Persistence: the record lives in LiteDB, so a brand-new tracker over the same database
        // (e.g. after a server restart) still lets the edit through.
        Directory.CreateDirectory(_dir);
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        using var _ = db;
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "one\ntwo\n");

        var ctx = Ctx(db, "s", _dir);
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);

        ctx.Reads = new ReadTracker(db);   // fresh instance, same db
        var result = await new EditTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"path":"a.txt","oldText":"two","newText":"TWO"}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
    }

    [Fact]
    public void DeletingASessionRemovesItsReadRecords()
    {
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        using var _ = db;
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "one\ntwo\n");

        var ctx = Ctx(db, "s1", _dir);
        new ReadTracker(db).RecordRead("s1", file);
        Assert.Equal(1, db.FileReads.Find(r => r.SessionId == "s1").Count());

        db.DeleteSession("s1");
        Assert.Empty(db.FileReads.FindAll());
    }

    [Fact]
    public async Task ApplyPatchHonoursThePolicyForUpdateAndDelete()
    {
        var (ctx, path) = Setup([("a.txt", "one\ntwo\n"), ("c.txt", "bye\n")]);

        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            @@ two
            -two
            +TWO
            *** Delete File: c.txt
            *** End Patch
            """;
        var result = await new ApplyPatchTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("You must `read` a.txt", result.Output);
        Assert.Contains("You must `read` c.txt", result.Output);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path("a.txt")));
        Assert.True(File.Exists(path("c.txt")));

        // Reading both files makes the same patch succeed.
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.txt"}"""), CancellationToken.None);
        await new ReadTool().ExecuteAsync(ctx, Args.Parse("""{"path":"c.txt"}"""), CancellationToken.None);
        var ok = await new ApplyPatchTool(true).ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);
        Assert.False(ok.IsError, ok.Output);
    }

    // --- helpers -------------------------------------------------------------

    private (ToolContext Ctx, string Path) Setup(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, name), content);
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var ctx = Ctx(db, "s", _dir);
        return (ctx, Path.Combine(_dir, name));
    }

    private (ToolContext Ctx, Func<string, string> Path) Setup(IEnumerable<(string Name, string Content)> files)
    {
        Directory.CreateDirectory(_dir);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(_dir, name), content);
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var ctx = Ctx(db, "s", _dir);
        return (ctx, n => Path.Combine(_dir, n));
    }

    private static ToolContext Ctx(SofiaDb db, string sessionId, string dir)
    {
        var session = new Session { Id = sessionId, WorkspaceId = "ws" };
        db.Sessions.Insert(session);
        return new ToolContext
        {
            Session = session,
            Workspace = new Workspace { Id = "ws", Kind = "folder", Path = dir },
            Cwd = dir,
            Message = new Message { Id = sessionId + "-m1", SessionId = sessionId },
            Call = null!,
            Config = new ServerConfig(),
            Changes = new ChangeTracker(db),
            Host = null!,
            Reads = new ReadTracker(db),
        };
    }

    private static string JsonConvert(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
