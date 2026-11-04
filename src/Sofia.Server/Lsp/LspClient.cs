using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;

namespace Sofia.Server.Lsp;

/// <summary>Thrown when the server answers a request with a JSON-RPC error object.</summary>
public sealed class LspRequestException(string method, int code, string message) : Exception($"{method} failed ({code}): {message}")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
}

/// <summary>
/// LSP base-protocol framing: <c>Content-Length: N\r\n\r\n</c> headers with UTF-8 JSON bodies.
/// The reader buffers partial messages across reads, so it works over pipes as well as in-memory streams.
/// </summary>
internal static class LspWire
{
    public static byte[] Encode(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var all = new byte[header.Length + body.Length];
        header.CopyTo(all, 0);
        body.CopyTo(all, header.Length);
        return all;
    }

    public sealed class Reader(Stream input)
    {
        private readonly List<byte> _buf = [];
        private readonly byte[] _chunk = new byte[32 * 1024];

        /// <summary>Returns the next complete message, or null when the stream ends (EOF).</summary>
        public async Task<JsonNode?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                var msg = TryRead();
                if (msg is not null) return msg;
                var n = await input.ReadAsync(_chunk, ct);
                if (n <= 0) return null;
                _buf.AddRange(_chunk.AsSpan(0, n));
            }
        }

        private JsonNode? TryRead()
        {
            var (headerEnd, length) = FindHeader();
            if (headerEnd < 0 || length < 0) return null;
            if (_buf.Count < headerEnd + length) return null; // body not complete yet
            var body = _buf.GetRange(headerEnd, (int)length);
            _buf.RemoveRange(0, (int)(headerEnd + length));
            try
            {
                return JsonNode.Parse(body.ToArray());
            }
            catch (JsonException ex)
            {
                // A malformed message desynchronises the stream; surface it so the caller treats the server as crashed.
                throw new InvalidOperationException($"Malformed LSP message: {ex.Message}", ex);
            }
        }

        /// <summary>Locates the end of the header block and its Content-Length; (-1, -1) when the terminator is not in the buffer yet.</summary>
        private (int End, long Length) FindHeader()
        {
            var end = IndexOf(_buf, [13, 10, 13, 10]); // "\r\n\r\n" per spec
            var sepLength = 4;
            if (end < 0)
            {
                // Tolerate servers that terminate with bare LF.
                end = IndexOf(_buf, [10, 10]);
                sepLength = 2;
            }
            if (end < 0) return (-1, -1);

            long length = 0;
            var headerText = Encoding.UTF8.GetString(_buf.GetRange(0, end).ToArray());
            foreach (var line in headerText.Split('\n'))
            {
                var t = line.TrimEnd('\r').Trim();
                if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(t["Content-Length:".Length..].Trim(), out var l))
                    length = l;
            }
            return (end + sepLength, length);
        }

        private static int IndexOf(List<byte> buf, byte[] seq)
        {
            for (var i = 0; i + seq.Length <= buf.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < seq.Length; j++)
                    if (buf[i + j] != seq[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}

/// <summary>
/// Minimal LSP client over stdio: Content-Length framing, request/response correlation by id,
/// <c>$/cancelRequest</c> cancellation and automatic answers to server→client requests
/// (<c>workspace/configuration</c>, capability registration, work-done progress, message requests).
/// Stderr is drained to the logger. Document sync uses full-text <c>didOpen</c>/<c>didChange</c>.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly Stream _stdin;
    private readonly Process? _process;
    private readonly ILogger? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private long _nextId;
    private volatile bool _dead;

    /// <summary>Raised for <c>textDocument/publishDiagnostics</c>: (uri, diagnostics array).</summary>
    public event Action<string, JsonArray>? Diagnostics;

    /// <summary>Raised once when the connection dies (process exit or stream EOF); the exit code is null for stream-only clients.</summary>
    public event Action<int?>? Exited;

    /// <summary>The workspace root this client was initialized with.</summary>
    public string Root { get; private set; } = "";

    internal LspClient(Stream stdin, Stream stdout, Process? process, ILogger? log)
    {
        _stdin = stdin;
        _process = process;
        _log = log;
        _ = ReadLoopAsync(new LspWire.Reader(stdout));
    }

    /// <summary>Test hook: build a client over in-memory streams without a process.</summary>
    internal static LspClient ForStreams(Stream stdin, Stream stdout, ILogger? log = null) =>
        new(stdin, stdout, null, log);

    /// <summary>Test hook: pretend the server process exited (marks the connection dead and raises <see cref="Exited"/>).</summary>
    internal void SimulateProcessExitForTest(int? code) => Die(code);

    /// <summary>Starts the language server for a full command line.</summary>
    public static LspClient Start(string commandLine, string? workingDirectory, ILogger? log) =>
        Start(McpManager.SplitCommandLine(commandLine), workingDirectory, log);

    /// <summary>Starts the language server from an argv list (first element = executable).</summary>
    public static LspClient Start(IReadOnlyList<string> argv, string? workingDirectory, ILogger? log)
    {
        if (argv.Count == 0) throw new ArgumentException("Empty LSP command line.");
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{argv[0]}': {ex.Message}", ex);
        }

        // Drain stderr so a chatty server never blocks on a full pipe buffer.
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    log?.LogDebug("[lsp] {Line}", Text.FirstLine(line, 300));
                }
            }
            catch
            {
                // The stream ends with the process.
            }
        });

        return new LspClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process, log);
    }

    /// <summary>False once the connection is dead (process exited or stream closed).</summary>
    public bool IsAlive => !_dead && (_process is null || !_process.HasExited);

    public bool IsOpen(string uri) => _versions.ContainsKey(uri);

    /// <summary>Sends the LSP <c>initialize</c> request and the <c>initialized</c> notification.</summary>
    public async Task InitializeAsync(string root, CancellationToken ct)
    {
        Root = root;
        var rootUri = new Uri(PathUtil.Normalize(root)).AbsoluteUri;
        await RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["workspaceFolders"] = new JsonArray
            {
                new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(PathUtil.Normalize(root)) },
            },
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    // Full-document sync: we always send the whole text, which every server must support.
                    ["synchronization"] = new JsonObject { ["didSave"] = true },
                },
                ["workspace"] = new JsonObject(),
                ["window"] = new JsonObject { ["workDoneProgress"] = false },
            },
            ["initializationOptions"] = null,
        }, 60_000, ct);
        Notify("initialized", new JsonObject());
    }

    // --- requests -----------------------------------------------------------------

    /// <summary>Sends a request and waits for its response; cancels the server-side work on timeout.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, int timeoutMs = 30_000, CancellationToken ct = default)
    {
        if (_dead) throw new InvalidOperationException("The language server is not running.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        await SendAsync(msg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            var resp = await tcs.Task.WaitAsync(timeoutCts.Token);
            if (resp["error"] is JsonObject err)
                throw new LspRequestException(method, ToInt(err["code"]), err["message"]?.ToString() ?? "unknown error");
            return resp["result"];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Cancel(id);
            throw new TimeoutException($"{method} timed out after {timeoutMs / 1000}s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Cancel(long id) => Notify("$/cancelRequest", new JsonObject { ["id"] = id });

    /// <summary>Sends a notification (fire and forget).</summary>
    public void Notify(string method, JsonNode? @params)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        _ = SendAsync(msg);
    }

    private async Task SendAsync(JsonObject msg)
    {
        var bytes = LspWire.Encode(msg.ToJsonString());
        await _writeGate.WaitAsync();
        try
        {
            await _stdin.WriteAsync(bytes);
            await _stdin.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log?.LogDebug("LSP write failed: {Message}", ex.Message);
            FailAllPending("the connection to the language server was lost");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // --- document sync (full) -------------------------------------------------------

    public void DidOpen(string uri, string languageId, string text)
    {
        _versions[uri] = 1;
        Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = languageId,
                ["version"] = 1,
                ["text"] = text,
            },
        });
    }

    public void DidChangeFull(string uri, string text)
    {
        var version = _versions.AddOrUpdate(uri, 1, (_, v) => v + 1);
        Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray { new JsonObject { ["text"] = text } },
        });
    }

    public void DidSave(string uri) =>
        Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });

    // --- reading the stream -----------------------------------------------------------

    private async Task ReadLoopAsync(LspWire.Reader reader)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                JsonNode? node;
                try
                {
                    node = await reader.ReadAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.LogWarning("LSP stream error: {Message}", ex.Message);
                    break;
                }
                if (node is null) break; // EOF
                Handle(node as JsonObject);
            }
        }
        finally
        {
            OnClosed();
        }
    }

    private void Handle(JsonObject? msg)
    {
        if (msg is null) return;
        if (msg["id"] is JsonValue idv && ToLong(idv, out var id))
        {
            if (msg["method"] is JsonValue m && !string.IsNullOrEmpty(m.ToString()))
                _ = HandleServerRequestAsync(id, m.ToString()!, msg); // server→client request
            else if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(msg); // response to one of our requests
        }
        else if (msg["method"] is JsonValue nm && msg["id"] is not null)
        {
            // Server→client request with a non-numeric id: answer it so the server does not hang.
            _ = HandleServerRequestAsync(0, nm.ToString()!, msg);
        }
        else if (msg["method"] is JsonValue n2)
            OnNotification(n2.ToString()!, msg);
    }

    private void OnNotification(string method, JsonObject msg)
    {
        if (method == "textDocument/publishDiagnostics" && msg["params"] is JsonObject p)
        {
            var uri = p["uri"]?.ToString() ?? "";
            var diags = p["diagnostics"] as JsonArray ?? new JsonArray();
            Diagnostics?.Invoke(uri, diags);
            return;
        }
        if (method is "window/logMessage" or "$/logTrace")
            _log?.LogDebug("[lsp] {Method}: {Message}", method, Text.FirstLine(msg["params"]?["message"]?.ToString() ?? "", 200));
    }

    /// <summary>Answers the server→client requests we support; everything else gets a null result (valid JSON-RPC).</summary>
    private async Task HandleServerRequestAsync(long id, string method, JsonObject? request)
    {
        JsonNode? result = method switch
        {
            "workspace/configuration" => ConfigurationResponse(request),
            "client/registerCapability" or "client/unregisterCapability" or "window/workDoneProgress/create" => new JsonObject(),
            "window/showMessageRequest" => ShowMessageResponse(),
            "workspace/applyEdit" => new JsonObject { ["applied"] = false },
            _ => null,
        };
        var resp = new JsonObject { ["jsonrpc"] = "2.0" };
        // Echo the request's id (servers may use strings); fall back to our numeric id for internal calls.
        if (request?["id"] is not null) resp["id"] = request!["id"];
        else resp["id"] = id;
        if (result is not null) resp["result"] = result;
        await SendAsync(resp);
    }

    /// <summary>Answers one null per requested item (we do not support workspace configuration).</summary>
    private static JsonArray ConfigurationResponse(JsonObject? request)
    {
        var items = request?["params"]?["items"] as JsonArray;
        var count = Math.Max(1, items?.Count ?? 1);
        var arr = new JsonArray();
        for (var i = 0; i < count; i++) arr.Add(null);
        return arr;
    }

    private static JsonObject ShowMessageResponse() => new();

    // --- shutdown --------------------------------------------------------------------

    /// <summary>Polite shutdown (<c>shutdown</c> + <c>exit</c>), then kills the whole process tree if it does not leave in time.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (_process is null)
        {
            await DisposeAsync();
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await RequestAsync("shutdown", null, (int)Math.Max(500, timeout.TotalMilliseconds - 100), cts.Token);
            }
            catch
            {
                // The server may not answer in time; the exit notification and kill below still apply.
            }
            Notify("exit", null);
            if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
                ProcessRunner.Kill(_process);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug(ex, "LSP shutdown failed; killing the process tree");
            ProcessRunner.Kill(_process);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try
        {
            _stdin.Close();
        }
        catch
        {
            // Already closed.
        }
        FailAllPending("the LSP client was disposed");
        if (_process is not null) ProcessRunner.Kill(_process);
        await Task.CompletedTask;
    }

    private void OnClosed() => Die(null);

    /// <summary>Marks the connection dead, fails all pending requests and raises <see cref="Exited"/>. Idempotent.</summary>
    internal void Die(int? forcedCode)
    {
        if (_dead) return;
        _dead = true;
        int? code = forcedCode;
        try
        {
            if (code is null && _process is not null && _process.HasExited) code = _process.ExitCode;
        }
        catch
        {
            // Accessing the process state can race with its disposal.
        }
        FailAllPending("the language server closed the connection");
        Exited?.Invoke(code);
    }

    private void FailAllPending(string reason)
    {
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new InvalidOperationException(reason));
        _pending.Clear();
    }

    private static bool ToLong(JsonValue v, out long value)
    {
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<double>(out var d)) { value = (long)d; return true; }
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out value)) return true;
        value = 0;
        return false;
    }

    private static int ToInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1;
}
