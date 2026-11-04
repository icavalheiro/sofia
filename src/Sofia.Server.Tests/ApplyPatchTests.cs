using LiteDB;
using Sofia.Server.Agent;
using Sofia.Server.Changes;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Providers;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

/// <summary>Codex-style patch parsing and the atomic apply_patch tool (spec 021).</summary>
public class ApplyPatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-applypatch-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParsesAddDeleteUpdateAndMove()
    {
        var (hunks, errors) = PatchParser.Parse("""
            *** Begin Patch
            *** Add File: b.ts
            +line one
            +line two

            *** Update File: a.ts
            *** Move to: a2.ts
            @@ context line
            -old line
            +new line
            *** Delete File: c.txt
            *** End Patch
            """);
        Assert.Empty(errors);
        Assert.Equal(3, hunks.Count);

        var add = Assert.IsType<PatchHunk.Add>(hunks[0]);
        Assert.Equal("b.ts", add.Path);
        Assert.Equal(["line one", "line two", ""], add.Lines);   // trailing blank line → final newline

        var update = Assert.IsType<PatchHunk.Update>(hunks[1]);
        Assert.Equal("a.ts", update.Path);
        Assert.Equal("a2.ts", update.MoveTo);
        var chunk = Assert.Single(update.Chunks);
        Assert.Equal("context line", chunk.Context);
        Assert.Equal(["old line"], chunk.OldLines.ToArray());
        Assert.Equal(["new line"], chunk.NewLines.ToArray());

        var del = Assert.IsType<PatchHunk.Delete>(hunks[2]);
        Assert.Equal("c.txt", del.Path);
    }

    [Fact]
    public void AcceptsTheFirstChunkWithoutAContextHeader()
    {
        var (hunks, errors) = PatchParser.Parse("""
            *** Begin Patch
            *** Update File: a.ts
            -old line
            +new line
            *** End Patch
            """);
        Assert.Empty(errors);
        var update = Assert.IsType<PatchHunk.Update>(Assert.Single(hunks));
        var chunk = Assert.Single(update.Chunks);
        Assert.Null(chunk.Context);
        Assert.Equal(["old line"], chunk.OldLines.ToArray());
        Assert.Equal(["new line"], chunk.NewLines.ToArray());
    }

    [Fact]
    public void StripsAHeredocWrapper()
    {
        var (hunks, errors) = PatchParser.Parse("""
            <<'EOF'
            *** Begin Patch
            *** Update File: a.ts
            @@ x
            -old
            +new
            *** End Patch
            EOF
            """);
        Assert.Empty(errors);
        Assert.Single(hunks);
    }

    [Fact]
    public void MarksEndOfFileChunks()
    {
        var (hunks, errors) = PatchParser.Parse("""
            *** Begin Patch
            *** Update File: a.ts
            @@ tail
            -last line
            +new last line
            *** End of File
            *** End Patch
            """);
        Assert.Empty(errors);
        var chunk = Assert.Single(Assert.IsType<PatchHunk.Update>(Assert.Single(hunks)).Chunks);
        Assert.True(chunk.EndOfFile);
    }

    [Fact]
    public void ReportsMalformedPatchesWithLineNumbers()
    {
        var (hunks, errors) = PatchParser.Parse("""
            *** Begin Patch
            *** Update File: a.ts
            @@ x
            -old
            this is not a patch line
            *** Add File:
            *** End Patch
            """);
        Assert.Single(hunks);   // the update parsed; the add has no path
        Assert.Contains(errors, e => e.StartsWith("Line 5:", StringComparison.Ordinal) && e.Contains("Expected '+', '-'"));
        Assert.Contains(errors, e => e.StartsWith("Line 6:", StringComparison.Ordinal) && e.Contains("Missing file path"));
    }

    [Fact]
    public void RejectsAnEmptyPatchAndContentAfterEnd()
    {
        Assert.Single(PatchParser.Parse("").Errors);
        var (_, errors) = PatchParser.Parse("""
            *** Begin Patch
            *** Delete File: a.txt
            *** End Patch
            trailing garbage
            """);
        Assert.Contains(errors, e => e.Contains("after '*** End Patch'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppliesAMultiFilePatch()
    {
        var (ctx, path) = Setup([("a.txt", "alpha\nbeta\ngamma\n"), ("c.txt", "bye\n")]);
        var patch = """
            *** Begin Patch
            *** Add File: b.ts
            +line one
            +line two

            *** Update File: a.txt
            @@ beta
            -beta
            +BETA
            *** Delete File: c.txt
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
        Assert.Contains("Applied patch: 3 files", result.Output);
        Assert.Equal("alpha\nBETA\ngamma\n", await File.ReadAllTextAsync(path("a.txt")));
        Assert.Equal("line one\nline two\n", await File.ReadAllTextAsync(path("b.ts")));
        Assert.False(File.Exists(path("c.txt")));
    }

    [Fact]
    public async Task ABadHunkInOneFileLeavesTheOthersUntouched()
    {
        var (ctx, path) = Setup([("a.txt", "one\ntwo\n"), ("b.txt", "x\ny\n"), ("c.txt", "p\nq\n")]);
        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            @@ two
            -two
            +TWO
            *** Update File: b.txt
            @@ missing context
            -nope
            +nothing
            *** Update File: c.txt
            @@ q
            -q
            +Q
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Context not found", result.Output);
        // Atomic: nothing on disk changed.
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path("a.txt")));
        Assert.Equal("x\ny\n", await File.ReadAllTextAsync(path("b.txt")));
        Assert.Equal("p\nq\n", await File.ReadAllTextAsync(path("c.txt")));
    }

    [Fact]
    public async Task MatchesLooselyAroundWhitespaceAndUnicodePunctuation()
    {
        var (ctx, path) = Setup([("a.txt", "  const x = “hello — world”;\nnext line\n")]);
        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            @@ hello
            -const x = "hello - world";
            +  const x = "fixed";
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("  const x = \"fixed\";\nnext line\n", await File.ReadAllTextAsync(path("a.txt")));
    }

    [Fact]
    public async Task KeepsCrlfLineEndings()
    {
        var (ctx, path) = Setup([("a.txt", "one\r\ntwo\r\nthree\r\n")]);
        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            @@ two
            -two
            +TWO
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("one\r\nTWO\r\nthree\r\n", await File.ReadAllTextAsync(path("a.txt")));
    }

    [Fact]
    public async Task RevertRestoresUpdatedDeletedMovedFilesAndDeletesAddedOnes()
    {
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        using var _ = db;
        var changes = new ChangeTracker(db);
        var (ctx, path) = Setup([("a.txt", "one\ntwo\n")], extra: [("old.txt", "keep me\n")], changes: changes);

        var patch = """
            *** Begin Patch
            *** Add File: added.txt
            +fresh
            *** Update File: a.txt
            @@ two
            -two
            +TWO
            *** Update File: old.txt
            *** Move to: new.txt
            @@ keep
            -keep me
            +kept
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);
        Assert.False(result.IsError, result.Output);

        // Sanity: the patch landed.
        Assert.True(File.Exists(path("added.txt")));
        Assert.Equal("one\nTWO\n", await File.ReadAllTextAsync(path("a.txt")));
        Assert.Equal("kept\n", await File.ReadAllTextAsync(path("new.txt")));
        Assert.False(File.Exists(path("old.txt")));

        var restored = changes.Revert(ctx.Session.Id, 1);
        Assert.Contains(path("added.txt"), restored);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path("a.txt")));
        Assert.False(File.Exists(path("added.txt")));
        Assert.Equal("keep me\n", await File.ReadAllTextAsync(path("old.txt")));
        Assert.False(File.Exists(path("new.txt")));
    }

    [Fact]
    public async Task AddOnAnExistingFileFailsWithoutTouchingDisk()
    {
        var (ctx, path) = Setup([("a.txt", "here\n")]);
        var patch = """
            *** Begin Patch
            *** Add File: a.txt
            +nope
            *** End Patch
            """;

        var result = await new ApplyPatchTool().ExecuteAsync(ctx, Args.Parse($$"""{"input":{{JsonConvert(patch)}}}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("already exists", result.Output);
        Assert.Equal("here\n", await File.ReadAllTextAsync(path("a.txt")));
    }

    [Fact]
    public void ExposureAutoKeepsApplyPatchOnlyForOpenAiFamilyModels()
    {
        ProviderConfig Provider(string type, string flavor) => new() { Id = "p", Name = "P", Type = type, Flavor = flavor };
        ModelInfo Model(string id) => new() { Id = "p/" + id, ModelId = id };

        // auto: kept for the Responses API, gpt-*, o-series and codex ids…
        Assert.True(SessionRun.ApplyPatchExposure("auto", Provider("openai-responses", "openai"), Model("gpt-5")));
        Assert.True(SessionRun.ApplyPatchExposure("auto", Provider("openai", "openai"), Model("gpt-4o-mini")));
        Assert.True(SessionRun.ApplyPatchExposure("auto", Provider("openai", "openai"), Model("o3")));
        Assert.True(SessionRun.ApplyPatchExposure("auto", Provider("anthropic", "anthropic"), Model("codex-mini")));
        // …and dropped for a local Ollama model.
        Assert.False(SessionRun.ApplyPatchExposure("auto", Provider("openai", "ollama"), Model("nemo-8b")));

        // always / never win regardless of the model.
        Assert.True(SessionRun.ApplyPatchExposure("always", Provider("openai", "ollama"), Model("nemo-8b")));
        Assert.False(SessionRun.ApplyPatchExposure("never", Provider("openai-responses", "openai"), Model("gpt-5")));
    }

    // --- helpers -------------------------------------------------------------

    // The read-before-edit policy is exercised in ReadBeforeEditTests; these tests focus on the patch mechanics.
    private (ToolContext Ctx, Func<string, string> Path) Setup(IEnumerable<(string Name, string Content)> files, IEnumerable<(string Name, string Content)>? extra = null, ChangeTracker? changes = null, bool readBeforeEdit = false)
    {
        Directory.CreateDirectory(_dir);
        foreach (var (name, content) in files.Concat(extra ?? []))
            File.WriteAllText(Path.Combine(_dir, name), content);
        var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var session = new Session { Id = "s", WorkspaceId = "ws" };
        db.Sessions.Insert(session);
        changes ??= new ChangeTracker(db);
        var ctx = new ToolContext
        {
            Session = session,
            Workspace = new Workspace { Id = "ws", Kind = "folder", Path = _dir },
            Cwd = _dir,
            Message = new Message { Id = "m1", SessionId = "s", Seq = 1 },
            Call = null!,
            Config = new ServerConfig { ReadBeforeEdit = readBeforeEdit },
            Changes = changes,
            Host = null!,
            Reads = new ReadTracker(db),
        };
        return (ctx, name => Path.Combine(_dir, name));
    }

    private static string JsonConvert(string s)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        return json;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
