using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

namespace Sofia.Server.Mcp;

public sealed class McpServerView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "command";
    public string Command { get; set; } = "";
    public string Url { get; set; } = "";
    public bool HasToken { get; set; }
    public bool Enabled { get; set; }
    /// <summary>'connected' | 'error' | 'stopped' | 'pending'.</summary>
    public string Status { get; set; } = "stopped";
    public string? Error { get; set; }
    public int Tools { get; set; }
    public List<string> ToolNames { get; set; } = [];
}

/// <summary>Owns MCP client connections and exposes their tools to the agent loop.</summary>
public sealed class McpManager(SofiaDb db, IEventBus bus, ILoggerFactory loggers, ILogger<McpManager> log) : IHostedService, IAsyncDisposable
{
    private sealed class Connection
    {
        public McpClient? Client;
        public List<McpClientTool> Tools = [];
        public string Status = "stopped";
        public string? Error;
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var server in db.McpServers.Find(s => s.Enabled))
            _ = Task.Run(() => ConnectAsync(server.Id, CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.Client is null) continue;
            try { await conn.Client.DisposeAsync(); } catch { /* shutting down */ }
            conn.Client = null;
        }
    }

    public List<McpServerView> List() => db.McpServers.FindAll().OrderBy(s => s.Name).Select(View).ToList();

    public McpServerView View(McpServerConfig config)
    {
        _connections.TryGetValue(config.Id, out var conn);
        return new McpServerView
        {
            Id = config.Id,
            Name = config.Name,
            Transport = config.Transport,
            Command = config.Command,
            Url = config.Url,
            HasToken = !string.IsNullOrEmpty(config.Token),
            Enabled = config.Enabled,
            Status = conn?.Status ?? "stopped",
            Error = conn?.Error,
            Tools = conn?.Tools.Count ?? 0,
            ToolNames = conn?.Tools.Select(t => t.Name).ToList() ?? [],
        };
    }

    public async Task<McpServerView> AddAsync(McpServerConfig config, CancellationToken ct)
    {
        config.Id = Ids.New("mcp");
        db.McpServers.Insert(config);
        await ConnectAsync(config.Id, ct);
        return View(db.McpServers.FindById(config.Id));
    }

    public async Task RemoveAsync(string id)
    {
        await DisconnectAsync(id, persist: false);
        db.McpServers.Delete(id);
        _connections.TryRemove(id, out _);
    }

    public async Task<McpServerView?> ConnectAsync(string id, CancellationToken ct)
    {
        var config = db.McpServers.FindById(id);
        if (config is null) return null;
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var conn = _connections.GetOrAdd(id, _ => new Connection());
            if (conn.Client is not null)
            {
                try { await conn.Client.DisposeAsync(); } catch { /* replaced below */ }
                conn.Client = null;
            }
            conn.Status = "pending";
            conn.Error = null;
            conn.Tools = [];
            Publish(config);

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var client = await McpClient.CreateAsync(CreateTransport(config), null, loggers, cts.Token);
                var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
                conn.Client = client;
                conn.Tools = tools.ToList();
                conn.Status = "connected";
                log.LogInformation("MCP server {Name} connected with {Count} tools", config.Name, conn.Tools.Count);
            }
            catch (Exception ex)
            {
                conn.Status = "error";
                conn.Error = Text.FirstLine(ex.Message, 300);
                log.LogWarning("MCP server {Name} failed: {Error}", config.Name, ex.Message);
            }

            if (!config.Enabled)
            {
                config.Enabled = true;
                db.McpServers.Update(config);
            }
            Publish(config);
            return View(config);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<McpServerView?> DisconnectAsync(string id, bool persist = true)
    {
        var config = db.McpServers.FindById(id);
        if (config is null) return null;
        if (_connections.TryGetValue(id, out var conn))
        {
            if (conn.Client is not null)
            {
                try { await conn.Client.DisposeAsync(); } catch { /* already gone */ }
            }
            conn.Client = null;
            conn.Tools = [];
            conn.Status = "stopped";
            conn.Error = null;
        }
        if (persist && config.Enabled)
        {
            config.Enabled = false;
            db.McpServers.Update(config);
        }
        Publish(config);
        return View(config);
    }

    private void Publish(McpServerConfig config) => bus.Publish(EventTypes.McpUpdated, View(config));

    private IClientTransport CreateTransport(McpServerConfig config)
    {
        if (config.Transport == "web")
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(config.Token)) headers["Authorization"] = $"Bearer {config.Token}";
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url),
                Name = config.Name,
                AdditionalHeaders = headers,
            }, loggers);
        }

        var argv = SplitCommandLine(config.Command);
        if (argv.Count == 0) throw new InvalidOperationException("Empty command.");
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = argv[0],
            Arguments = argv.Skip(1).ToList(),
            EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
        }, loggers);
    }

    internal static List<string> SplitCommandLine(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var started = false;
        foreach (var c in value)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                started = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (started || current.Length > 0) parts.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }
            current.Append(c);
        }
        if (started || current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Tools of every connected server, named `server__tool`.</summary>
    public List<ITool> Tools()
    {
        var result = new List<ITool>();
        foreach (var config in db.McpServers.Find(s => s.Enabled))
        {
            if (!_connections.TryGetValue(config.Id, out var conn) || conn.Client is null || conn.Status != "connected") continue;
            foreach (var tool in conn.Tools) result.Add(new McpTool(config.Name, conn.Client, tool));
        }
        return result;
    }

    private sealed class McpTool(string server, McpClient client, McpClientTool tool) : ITool
    {
        public string Name { get; } = Sanitize($"{server}__{tool.Name}");
        public string Description => $"[MCP {server}] {tool.Description}";
        public ToolKind Kind => tool.ProtocolTool.Annotations?.ReadOnlyHint == true ? ToolKind.Read : ToolKind.External;

        public JsonObject Parameters
        {
            get
            {
                var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) as JsonObject ?? [];
                schema["type"] ??= "object";
                schema["properties"] ??= new JsonObject();
                return schema;
            }
        }

        public string Summarize(JsonObject args)
        {
            var first = args.FirstOrDefault();
            return first.Value is null ? tool.Name : $"{tool.Name} {Text.FirstLine(first.Value.ToString(), 80)}";
        }

        public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString())?
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value) ?? [];
            var result = await client.CallToolAsync(tool.Name, dict, cancellationToken: ct);
            var sb = new StringBuilder();
            foreach (var block in result.Content)
            {
                switch (block)
                {
                    case TextContentBlock text:
                        sb.AppendLine(text.Text);
                        break;
                    case ImageContentBlock image:
                        sb.AppendLine($"[image {image.MimeType}, {image.Data.Length} bytes base64]");
                        break;
                    default:
                        sb.AppendLine(JsonSerializer.Serialize(block, block.GetType()));
                        break;
                }
            }
            if (sb.Length == 0 && result.StructuredContent is not null) sb.Append(result.StructuredContent.ToString());
            var output = Text.Truncate(sb.ToString().TrimEnd(), ctx.Config.MaxToolOutput);
            return result.IsError == true ? ToolResult.Fail(output) : ToolResult.Ok(output.Length == 0 ? "(no output)" : output);
        }

        private static string Sanitize(string name)
        {
            var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
            return clean.Length > 64 ? clean[..64] : clean;
        }
    }
}
