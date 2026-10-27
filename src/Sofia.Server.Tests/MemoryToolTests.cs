using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

public class MemoryToolTests
{
    private static (MemoryTool Tool, SofiaDb Db) Create()
    {
        var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:" });
        return (new MemoryTool(db), db);
    }

    private static ToolContext Context(string workspaceId, string sessionId = "s") => new()
    {
        Session = new Session { Id = sessionId, WorkspaceId = workspaceId, Agent = "build" },
        Workspace = new Workspace { Id = workspaceId, Kind = "chat" }, Cwd = "", Message = null!, Call = null!,
        Config = null!, Changes = null!, Host = null!,
    };

    private static Task<ToolResult> Run(MemoryTool tool, ToolContext ctx, string json) =>
        tool.ExecuteAsync(ctx, Args.Parse(json), CancellationToken.None);

    [Fact]
    public async Task SetGetListAndOverwrite()
    {
        var (tool, db) = Create();
        using var _ = db;
        var ctx = Context("ws");

        Assert.Contains("empty", (await Run(tool, ctx, """{"action":"list"}""")).Output);
        Assert.False((await Run(tool, ctx, """{"action":"set","key":"build.cmd","value":"dotnet build"}""")).IsError);
        Assert.Equal("dotnet build", (await Run(tool, ctx, """{"action":"get","key":"build.cmd"}""")).Output);
        Assert.Contains("build.cmd", (await Run(tool, ctx, "{}")).Output);

        var created = db.Memory.FindAll().Single().UpdatedAt;
        await Task.Delay(5);
        await Run(tool, ctx, """{"action":"set","key":"build.cmd","value":"dotnet build Sofia.slnx"}""");
        var entry = Assert.Single(db.Memory.FindAll());
        Assert.Equal("dotnet build Sofia.slnx", entry.Value);
        Assert.True(entry.UpdatedAt > created);
    }

    [Fact]
    public async Task EmptyValueDeletesTheKey()
    {
        var (tool, db) = Create();
        using var _ = db;
        var ctx = Context("ws");
        await Run(tool, ctx, """{"action":"set","key":"k","value":"v"}""");
        Assert.False((await Run(tool, ctx, """{"action":"set","key":"k","value":""}""")).IsError);
        Assert.Empty(db.Memory.FindAll());
        Assert.True((await Run(tool, ctx, """{"action":"get","key":"k"}""")).IsError);
        Assert.Contains("not found", (await Run(tool, ctx, """{"action":"set","key":"k","value":"  "}""")).Output);
    }

    [Fact]
    public async Task IsIsolatedPerWorkspaceAndSharedAcrossChats()
    {
        var (tool, db) = Create();
        using var _ = db;
        await Run(tool, Context("a", "chat1"), """{"action":"set","key":"fact","value":"one"}""");
        Assert.Equal("one", (await Run(tool, Context("a", "chat2"), """{"action":"get","key":"fact"}""")).Output);
        Assert.True((await Run(tool, Context("b"), """{"action":"get","key":"fact"}""")).IsError);
        Assert.Contains("empty", (await Run(tool, Context("b"), """{"action":"list"}""")).Output);
    }

    [Fact]
    public async Task MissingKeyErrorSuggestsSimilarKeys()
    {
        var (tool, db) = Create();
        using var _ = db;
        var ctx = Context("ws");
        await Run(tool, ctx, """{"action":"set","key":"deploy/staging","value":"x"}""");
        var result = await Run(tool, ctx, """{"action":"get","key":"deploy"}""");
        Assert.True(result.IsError);
        Assert.Contains("deploy/staging", result.Output);
    }

    [Fact]
    public async Task EnforcesLimits()
    {
        var (tool, db) = Create();
        using var _ = db;
        var ctx = Context("ws");
        Assert.True((await Run(tool, ctx, """{"action":"set","key":"bad key","value":"v"}""")).IsError);
        Assert.True((await Run(tool, ctx, $$"""{"action":"set","key":"{{new string('k', MemoryTool.MaxKeyChars + 1)}}","value":"v"}""")).IsError);
        Assert.True((await Run(tool, ctx, $$"""{"action":"set","key":"big","value":"{{new string('v', MemoryTool.MaxValueChars + 1)}}"}""")).IsError);
        Assert.False((await Run(tool, ctx, $$"""{"action":"set","key":"ok","value":"{{new string('v', MemoryTool.MaxValueChars)}}"}""")).IsError);

        for (var i = 1; i < MemoryTool.MaxKeys; i++)
            Assert.False((await Run(tool, ctx, $$"""{"action":"set","key":"k{{i}}","value":"v"}""")).IsError);
        Assert.True((await Run(tool, ctx, """{"action":"set","key":"overflow","value":"v"}""")).IsError);
        // Updating an existing key still works at the cap.
        Assert.False((await Run(tool, ctx, """{"action":"set","key":"ok","value":"new"}""")).IsError);
    }

    [Fact]
    public void IsReadKindAndRemovedWithTheWorkspace()
    {
        var (tool, db) = Create();
        using var _ = db;
        Assert.Equal(ToolKind.Read, tool.Kind);
        db.Memory.Insert(new MemoryEntry { WorkspaceId = "a", Key = "k", Value = "v" });
        db.Memory.Insert(new MemoryEntry { WorkspaceId = "b", Key = "k", Value = "v" });
        db.DeleteWorkspaceMemory("a");
        Assert.Equal("b", Assert.Single(db.Memory.FindAll()).WorkspaceId);
    }
}
