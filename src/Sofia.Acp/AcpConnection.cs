using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sofia.Acp;

/// <summary>NDJSON JSON-RPC 2.0 peer over stdin/stdout (ACP transport).</summary>
public sealed class AcpConnection : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _pendingLock = new();
    private long _nextId;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    public Task ReadTask => _readLoop ?? Task.CompletedTask;

    public event Func<JsonRpcRequest, Task>? RequestReceived;
    public event Func<JsonRpcNotification, Task>? NotificationReceived;

    public AcpConnection(Stream input, Stream output)
    {
        _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public void Start(CancellationToken ct)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { await WriteErrorAsync(null, -32700, "Parse error"); continue; }
                if (node is not JsonObject obj) { await WriteErrorAsync(null, -32600, "Invalid request"); continue; }

                string? method = null;
                var hasMethod = obj.TryGetPropertyValue("method", out var methodNode) && methodNode is JsonValue mv && mv.TryGetValue<string>(out method);
                var idNode = obj["id"];
                var idKey = idNode?.ToJsonString();

                if (hasMethod && idNode is not null)
                {
                    await HandleRequestAsync(new JsonRpcRequest(idNode, method!, obj["params"]), ct);
                }
                else if (hasMethod)
                {
                    if (NotificationReceived is not null)
                        await NotificationReceived(new JsonRpcNotification(method!, obj["params"]));
                }
                else if (idNode is not null)
                {
                    // response
                    TaskCompletionSource<JsonNode?>? tcs = null;
                    lock (_pendingLock)
                    {
                        if (idKey is not null && _pending.Remove(idKey, out tcs)) { }
                    }
                    if (tcs is not null)
                    {
                        if (obj.TryGetPropertyValue("error", out var err) && err is not null)
                            tcs.TrySetException(new JsonRpcException(err["code"]?.GetValue<int>() ?? -32603, err["message"]?.GetValue<string>() ?? "error"));
                        else
                            tcs.TrySetResult(obj["result"]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[acp] read loop: {ex.Message}");
        }
        // EOF: cancel pending
        lock (_pendingLock)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }

    private async Task HandleRequestAsync(JsonRpcRequest req, CancellationToken ct)
    {
        if (RequestReceived is null) { await WriteErrorAsync(req.Id, -32601, "Method not found"); return; }
        try
        {
            await RequestReceived(req);
        }
        catch (JsonRpcException ex)
        {
            await WriteErrorAsync(req.Id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(req.Id, -32603, ex.Message);
        }
    }

    public Task WriteResultAsync(JsonNode? id, object? result)
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = ToNode(result) };
        return WriteLineAsync(obj);
    }

    public Task WriteErrorAsync(JsonNode? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return WriteLineAsync(obj);
    }

    public Task WriteNotificationAsync(string method, object? @params)
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) obj["params"] = ToNode(@params);
        return WriteLineAsync(obj);
    }

    public async Task<JsonNode?> SendRequestAsync(string method, object? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var idNode = JsonValue.Create(id);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[id.ToString()] = tcs;
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) obj["params"] = ToNode(@params);
        await WriteLineAsync(obj);
        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    private async Task WriteLineAsync(JsonObject obj)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(obj.ToJsonString());
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        if (value is JsonNode n) return n.DeepClone();
        if (value is string s) return JsonValue.Create(s);
        return JsonSerializer.SerializeToNode(value, value.GetType(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        if (_readLoop is not null) try { await _readLoop; } catch { }
        _reader.Dispose();
        await _writer.DisposeAsync();
        _writeLock.Dispose();
    }
}

public sealed record JsonRpcRequest(JsonNode? Id, string Method, JsonNode? Params);
public sealed record JsonRpcNotification(string Method, JsonNode? Params);

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
