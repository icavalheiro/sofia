using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Changes;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Lsp;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

/// <summary>A full-duplex in-memory stream pair (deterministic EOF, no platform pipes).</summary>
internal sealed class InMemoryPipe : IAsyncDisposable
{
    private readonly Channel<byte[]> _toA; // written by B, read by A
    private readonly Channel<byte[]> _toB; // written by A, read by B

    public Stream A { get; }
    public Stream B { get; }

    public InMemoryPipe()
    {
        _toA = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _toB = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        A = new Endpoint(_toA, _toB);
        B = new Endpoint(_toB, _toA);
    }

    public ValueTask DisposeAsync()
    {
        _toA.Writer.TryComplete(); // EOF for A's reader
        _toB.Writer.TryComplete(); // EOF for B's reader
        return ValueTask.CompletedTask;
    }

    private sealed class Endpoint(Channel<byte[]> readFrom, Channel<byte[]> writeTo) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // Pipe semantics: return as soon as at least one byte is available (0 on EOF). The LSP reader
            // buffers partial messages across reads, so short reads are fine.
            if (!await readFrom.Reader.WaitToReadAsync(ct)) return 0;
            var chunk = await readFrom.Reader.ReadAsync(ct);
            var n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var copy = new byte[buffer.Length];
            buffer.CopyTo(copy);
            await writeTo.Writer.WriteAsync(copy, ct);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            writeTo.Writer.TryWrite(copy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// A minimal in-memory LSP server: speaks Content-Length framing, answers initialize/definition/rename,
/// syncs documents on didOpen/didChange and publishes diagnostics for lines containing "error" (severity 1)
/// or "warning" (2). Lets the LSP tests run with no language servers installed.
/// </summary>
internal sealed class FakeLspServer : IAsyncDisposable
{
    private readonly InMemoryPipe _pipe;
    private readonly Task _readerTask;
    private readonly ConcurrentDictionary<string, string> _docs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FakeLspServer()
    {
        _pipe = new InMemoryPipe();
        // Full-duplex: the client uses endpoint A (writes to it, reads from it); the fake uses B.
        ClientStdin = _pipe.A;
        ClientStdout = _pipe.A;
        _readerTask = Task.Run(() => ReadLoopAsync(_pipe.B));
    }

    /// <summary>The stream pair to hand to the client: (client writes here, client reads from here).</summary>
    public Stream ClientStdin { get; }
    public Stream ClientStdout { get; }

    private async Task ReadLoopAsync(Stream input)
    {
        var reader = new LspWire.Reader(input);
        while (true)
        {
            JsonNode? msg;
            try
            {
                msg = await reader.ReadAsync(CancellationToken.None);
            }
            catch
            {
                return;
            }
            if (msg is null) return; // EOF
            Handle(msg as JsonObject);
        }
    }

    private void Handle(JsonObject? m)
    {
        if (m is null) return;
        var method = m["method"]?.ToString();
        var id = m["id"];
        if (method is null) return; // a response to one of our requests — the fake sends none

        switch (method)
        {
            case "initialize":
                Reply(id, new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["textDocumentSync"] = 1,
                        ["definitionProvider"] = true,
                        ["referencesProvider"] = true,
                        ["hoverProvider"] = true,
                        ["documentSymbolProvider"] = true,
                        ["workspaceSymbolProvider"] = true,
                        ["renameProvider"] = true,
                    },
                });
                return;

            case "shutdown":
                Reply(id, null);
                return;

            case "textDocument/definition":
                Reply(id, DefinitionFor(m["params"]));
                return;

            case "workspace/symbol":
                Reply(id, new JsonArray());
                return;

            case "textDocument/rename":
                Reply(id, RenameFor(m["params"]?.AsObject() ?? new JsonObject()));
                return;

            case "exit":
                return; // the client is done
        }

        if (id is null && m["params"] is { } p)
        {
            // Notification: didOpen / didChange sync the document and trigger a diagnostics publish.
            if (method is "textDocument/didOpen" or "textDocument/didChange")
            {
                var uri = p.AsObject()["textDocument"]?["uri"]?.ToString() ?? "";
                var text = method == "textDocument/didOpen"
                    ? p.AsObject()["textDocument"]?["text"]?.ToString() ?? ""
                    : (p.AsObject()["contentChanges"] as JsonArray)?.FirstOrDefault()?.AsObject()?["text"]?.ToString() ?? _docs.GetValueOrDefault(uri, "");
                _docs[uri] = text;
                PublishDiagnostics(uri);
            }
        }
    }

    private JsonObject? DefinitionFor(JsonNode? @params)
    {
        // Per the LSP spec the position is a top-level sibling of textDocument.
        var uri = @params?.AsObject()?["textDocument"]?["uri"]?.ToString() ?? "";
        var text = _docs.GetValueOrDefault(uri, "");
        var word = WordAt(text, @params?.AsObject()?["position"]);
        if (word is null) return null;
        var idx = text.IndexOf(word, StringComparison.Ordinal);
        if (idx < 0) return null;
        return new JsonObject
        {
            ["uri"] = uri,
            ["range"] = RangeAt(text, idx, word.Length),
        };
    }

    private JsonNode? RenameFor(JsonObject @params)
    {
        // Per the LSP spec the position is a top-level sibling of textDocument.
        var uri = @params["textDocument"]?["uri"]?.ToString() ?? "";
        var text = _docs.GetValueOrDefault(uri, "");
        var word = WordAt(text, @params["position"]);
        if (word is null) return null;
        var newName = @params["newName"]?.ToString() ?? "";

        var changes = new JsonObject();
        foreach (var (u, doc) in _docs)
        {
            var edits = new JsonArray();
            foreach (Match match in Regex.Matches(doc, $@"\b{Regex.Escape(word)}\b"))
                edits.Add(new JsonObject
                {
                    ["range"] = RangeAt(doc, match.Index, match.Length),
                    ["newText"] = newName,
                });
            if (edits.Count > 0) changes[u] = edits;
        }
        return new JsonObject { ["changes"] = changes };
    }

    private void PublishDiagnostics(string uri)
    {
        var text = _docs.GetValueOrDefault(uri, "");
        var lines = text.Split('\n');
        var diags = new JsonArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("error") && !line.Contains("warning")) continue;
            var isWarning = line.Contains("warning");
            diags.Add(new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = i, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = i, ["character"] = line.Length },
                },
                ["severity"] = isWarning ? 2 : 1,
                ["message"] = isWarning ? "fake warning: something" : "fake error: broken",
            });
        }
        Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = diags });
    }

    private static string? WordAt(string text, JsonNode? pos)
    {
        if (pos is not JsonObject p) return null;
        var lineIdx = Long(p["line"]);
        var col = Long(p["character"]);
        var lines = text.Split('\n');
        if ((long)lines.Length <= lineIdx) return null;
        var line = lines[lineIdx];
        if (col < 0 || col > line.Length) return null;
        var start = (int)col;
        while (start > 0 && char.IsLetterOrDigit(line[start - 1])) start--;
        var end = start;
        while (end < line.Length && char.IsLetterOrDigit(line[end])) end++;
        return end > start ? line[start..end] : null;
    }

    private static JsonObject RangeAt(string text, int index, int length)
    {
        var before = text[..index];
        var line = before.Count(c => c == '\n');
        var lastNl = before.LastIndexOf('\n');
        var col = index - (lastNl < 0 ? 0 : lastNl + 1);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = col },
            ["end"] = new JsonObject { ["line"] = line, ["character"] = col + length },
        };
    }

    private static long Long(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0;
    }

    private void Reply(JsonNode? id, JsonNode? result)
    {
        // The id node belongs to the incoming message; clone it before attaching it to the reply.
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };
        if (result is not null) msg["result"] = result;
        _ = WriteAsync(msg);
    }

    private void Notify(string method, JsonNode? @params) =>
        _ = WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    private async Task WriteAsync(JsonObject msg)
    {
        var bytes = LspWire.Encode(msg.ToJsonString());
        await _writeGate.WaitAsync();
        try
        {
            // Endpoint B: writing here reaches the client's endpoint A.
            await _pipe.B.WriteAsync(bytes);
            await _pipe.B.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync(); // EOF on both directions
        try
        {
            await _readerTask;
        }
        catch
        {
            // The loop ends with the stream.
        }
    }
}

public class LspTests
{
    private static (SofiaDb Db, LspManager Manager) NewManager()
    {
        var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:" });
        var manager = new LspManager(db, new EventBus(), NullLoggerFactory.Instance);
        return (db, manager);
    }

    private static ToolContext Context(Workspace workspace, string cwd, ChangeTracker changes) => new()
    {
        Session = new Session { Id = "s1", WorkspaceId = workspace.Id },
        Workspace = workspace,
        Cwd = cwd,
        Message = new Message { Id = "m1" },
        Call = new ToolCall { Id = "c1", Name = "lsp" },
        Config = new ServerConfig(),
        Changes = changes,
        Host = null!,
    };

    private static Workspace FolderWorkspace(string path) => new()
    {
        Id = "ws1",
        Name = "test",
        Path = path,
        Kind = "folder",
    };

    // --- framing ------------------------------------------------------------------

    [Fact]
    public async Task WireRoundTripSplitsAndReassemblesMessages()
    {
        await using var pipe = new InMemoryPipe();
        var writer = pipe.B;
        var reader = new LspWire.Reader(pipe.A);

        var first = LspWire.Encode("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");
        // Write the first message in two chunks to exercise partial buffering: the read must block until the body is complete.
        await writer.WriteAsync(first.AsMemory(0, 15));
        var pending = reader.ReadAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted, "an incomplete message must not be returned early");
        await writer.WriteAsync(first.AsMemory(15));

        var m1 = (JsonObject)(await pending)!;
        Assert.Equal(1, Long(m1["id"]));
        Assert.True(BoolOf(m1["result"]?["ok"]));

        var second = LspWire.Encode("""{"jsonrpc":"2.0","method":"n"}""");
        await writer.WriteAsync(second);
        var m2 = (JsonObject)(await reader.ReadAsync(CancellationToken.None))!;
        Assert.Equal("n", m2["method"]?.ToString());

        // Complete the channel: the next read must return EOF (null).
        pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void PositionConversionIsOneBasedToZeroBasedUtf16()
    {
        // TryParse reads the tool's 1-based positions (column accepts col/character aliases).
        var args = new JsonObject { ["line"] = 3, ["column"] = 5 };
        Assert.True(LspPositions.TryParse(args, out var line, out var col));
        Assert.Equal(3, line);
        Assert.Equal(5, col);

        // ToLsp converts to the LSP's 0-based UTF-16 positions.
        var lsp = LspPositions.ToLsp(line, col);
        Assert.Equal(2, Long(lsp["line"]));
        Assert.Equal(4, Long(lsp["character"]));

        // Column aliases work too.
        Assert.True(LspPositions.TryParse(new JsonObject { ["line"] = 1, ["col"] = 2 }, out var l2, out _));
        Assert.Equal(1, l2);
    }

    [Fact]
    public void ApplyEditsHandlesMultiEditDocuments()
    {
        var text = "var foo = 1;\nfoo();\r\nvar bar = 2;";
        var edits = new JsonArray
        {
            // Replace the whole first line ("var foo = 1;" is 12 characters).
            JsonObject.Parse("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":12}},"newText":"var renamed = 1;"}""")!,
            // Insert at the end of line 2 (a CRLF position).
            JsonObject.Parse("""{"range":{"start":{"line":1,"character":6},"end":{"line":1,"character":6}},"newText":" // x"}""")!,
        };
        var result = LspRenameTool.ApplyEdits(text, edits);
        Assert.NotNull(result);
        Assert.StartsWith("var renamed = 1;\n", result!);
        Assert.Contains("foo(); // x\r\n", result);
    }

    // --- manager + fake server ------------------------------------------------------

    [Fact]
    public async Task DiagnosticsSettleAndDefinitionRoundTrip()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            var file = Path.Combine(tmp, "a.ts");
            File.WriteAllText(file, "var x: number = 'oops'; // error here\nconsole.log(x);\n");

            await using var fake = new FakeLspServer();
            var client = LspClient.ForStreams(fake.ClientStdin, fake.ClientStdout);
            manager.AttachClientForTest(tmp, "typescript", client);

            // The diagnostics settle wait must see a publish that arrived after our sync.
            var errors = await manager.DiagnosticsAfterWriteAsync(tmp, [file], CancellationToken.None);
            Assert.NotNull(errors);
            Assert.Contains("LSP errors:", errors);
            Assert.Contains("fake error: broken", errors);

            // Definition through the tool (1-based position on line 1 → "var").
            var ctx = Context(FolderWorkspace(tmp), tmp, new ChangeTracker(db));
            ctx.Lsp = manager;
            var result = await new LspTool().ExecuteAsync(ctx, Args.Parse("""{"action":"definition","path":"a.ts","line":1,"column":5}"""), CancellationToken.None);
            Assert.False(result.IsError, result.Output);
            Assert.Contains("a.ts:1:", result.Output);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DiagnosticsAfterWriteReturnsOnlyErrors()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            var file = Path.Combine(tmp, "a.ts");
            File.WriteAllText(file, "line one warning\nline two error\n");

            await using var fake = new FakeLspServer();
            manager.AttachClientForTest(tmp, "typescript", LspClient.ForStreams(fake.ClientStdin, fake.ClientStdout));

            var output = await manager.DiagnosticsAfterWriteAsync(tmp, [file], CancellationToken.None);
            Assert.NotNull(output);
            Assert.Contains("error:", output);
            Assert.DoesNotContain("warning:", output); // errors only
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RenameAppliesEditsAcrossFilesAndIsRevertible()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            var a = Path.Combine(tmp, "a.ts");
            var b = Path.Combine(tmp, "b.ts");
            File.WriteAllText(a, "export function foo() { return 1; }\n");
            File.WriteAllText(b, "import { foo } from './a';\nfoo();\n");

            await using var fake = new FakeLspServer();
            manager.AttachClientForTest(tmp, "typescript", LspClient.ForStreams(fake.ClientStdin, fake.ClientStdout));

            var changes = new ChangeTracker(db);
            var ctx = Context(FolderWorkspace(tmp), tmp, changes);
            ctx.Lsp = manager;

            // Sync both files first (didOpen) so the fake knows about both documents.
            await manager.DiagnosticsAfterWriteAsync(tmp, [a, b], CancellationToken.None);

            // "foo" in `export function foo` is at 1-based line 1, column 17.
            var result = await new LspRenameTool().ExecuteAsync(ctx, Args.Parse("""{"path":"a.ts","line":1,"column":17,"newName":"bar"}"""), CancellationToken.None);
            Assert.False(result.IsError, result.Output);
            Assert.Contains("2 files", result.Output);

            Assert.Equal("export function bar() { return 1; }\n", File.ReadAllText(a));
            Assert.Contains("import { bar } from './a';", File.ReadAllText(b));
            Assert.Contains("bar();", File.ReadAllText(b));

            // Revert restores both files (the rename captured them per message).
            var restored = changes.Revert("s1", 0);
            Assert.Equal(2, restored.Count);
            Assert.Equal("export function foo() { return 1; }\n", File.ReadAllText(a));
            Assert.Contains("foo();", File.ReadAllText(b));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task MissingServerYieldsGracefulNullAndView()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            // Disable every built-in server: no executable is required and nothing may start.
            var config = db.GetConfig();
            foreach (var def in LspServerCatalog.All)
                config.Lsp.Servers[def.Id] = new LspServerSetting { Enabled = false };
            db.SaveConfig(config);

            var client = await manager.GetClientAsync(tmp, "typescript", CancellationToken.None);
            Assert.Null(client);

            var file = Path.Combine(tmp, "a.ts");
            File.WriteAllText(file, "var x: number = 'oops'; // error\n");
            var errors = await manager.DiagnosticsAfterWriteAsync(tmp, [file], CancellationToken.None);
            Assert.Null(errors); // never fails, never hangs

            var view = manager.View("typescript");
            Assert.NotNull(view);
            Assert.Equal("disabled", view!.Status);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task IdleClientIsShutDown()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            manager.IdleTimeout = TimeSpan.FromMilliseconds(100);
            manager.IdleTick = TimeSpan.FromMilliseconds(30);

            await using var fake = new FakeLspServer();
            var client = LspClient.ForStreams(fake.ClientStdin, fake.ClientStdout);
            manager.AttachClientForTest(tmp, "typescript", client);
            Assert.True(client.IsAlive);

            await manager.StartAsync(CancellationToken.None); // starts the idle sweep
            for (var i = 0; i < 200 && client.IsAlive; i++)
                await Task.Delay(50);
            Assert.False(client.IsAlive, "the idle client should have been shut down");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CrashSetsErrorStatusAndRestartClearsIt()
    {
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-").FullName;
        try
        {
            await using var fake = new FakeLspServer();
            var client = LspClient.ForStreams(fake.ClientStdin, fake.ClientStdout);
            manager.AttachClientForTest(tmp, "typescript", client);

            // Simulate the server process dying unexpectedly.
            client.SimulateProcessExitForTest(1);
            await Task.Delay(50); // let the handler run

            var view = manager.View("typescript");
            Assert.Equal("error", view!.Status);
            Assert.Contains("exited unexpectedly", view.Error ?? "");

            await manager.RestartAsync("typescript");
            var after = manager.View("typescript");
            Assert.NotEqual("error", after!.Status);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    // --- gated integration tests (require real servers on PATH) ------------------------

    [Fact(Skip = "Requires SOFIA_TEST_LSP=1 and the language servers installed.")]
    public async Task RealServersReportDiagnosticsWhenGated()
    {
        if (Environment.GetEnvironmentVariable("SOFIA_TEST_LSP") != "1") return;
        var (db, manager) = NewManager();
        using var _db = db;
        await using var _manager = manager;
        var tmp = Directory.CreateTempSubdirectory("sofia-lsp-real-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "index.html"), "<html><body><p>hi</p></body></html>\n");
            File.WriteAllText(Path.Combine(tmp, "style.css"), ".a { color: red }\n");
            File.WriteAllText(Path.Combine(tmp, "app.ts"), "export const n: number = 'not a number';\n");

            var errors = await manager.DiagnosticsAfterWriteAsync(tmp,
                [Path.Combine(tmp, "index.html"), Path.Combine(tmp, "style.css"), Path.Combine(tmp, "app.ts")],
                new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);
            Assert.NotNull(errors); // at least the TS error must surface
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static long Long(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0;
    }

    private static bool BoolOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
