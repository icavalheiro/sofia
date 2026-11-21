using Sofia.Contracts.Domain;
using Sofia.Core.Mcp;

namespace Sofia.Core.Services;

/// <summary>Global MCP server administration: list, add (with validation), connect, disconnect, remove.</summary>
public sealed class McpAdminService(McpManager mcp)
{
    public List<McpServerView> List() => mcp.List();

    public async Task<McpServerView> AddAsync(CreateMcpRequest body, CancellationToken ct)
    {
        var transport = body.Transport == "web" ? "web" : "command";
        if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Name is required.");
        if (transport == "command" && string.IsNullOrWhiteSpace(body.Command)) throw new ArgumentException("Command is required.");
        if (transport == "web" && !Uri.TryCreate(body.Url, UriKind.Absolute, out _)) throw new ArgumentException("A valid URL is required.");
        return await mcp.AddAsync(new McpServerConfig
        {
            Name = body.Name.Trim(),
            Transport = transport,
            Command = body.Command?.Trim() ?? "",
            Url = body.Url?.Trim() ?? "",
            Token = string.IsNullOrWhiteSpace(body.Token) ? null : body.Token.Trim(),
            Env = body.Env ?? [],
        }, ct);
    }

    public async Task<McpServerView> ConnectAsync(string id, CancellationToken ct) =>
        await mcp.ConnectAsync(id, ct) ?? throw new NotFoundException("MCP server not found.");

    public async Task<McpServerView> DisconnectAsync(string id) =>
        await mcp.DisconnectAsync(id) ?? throw new NotFoundException("MCP server not found.");

    public Task RemoveAsync(string id) => mcp.RemoveAsync(id);
}
