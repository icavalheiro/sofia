using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Per-workspace key/value store the agent can list, read and write. Shared by every chat and subagent of the workspace,
/// never injected into the system prompt. Saving a key with an empty value deletes it. The data lives in Sofia's own
/// database, so the tool is allowed in read-only and plan modes.
/// </summary>
public sealed partial class MemoryTool(SofiaDb db) : ITool
{
    public const int MaxKeyChars = 100;
    public const int MaxValueChars = 20_000;
    public const int MaxKeys = 200;
    private const int PreviewChars = 60;

    private static readonly string[] Actions = ["list", "get", "set"];

    public string Name => "memory";
    public string Description =>
        "Per-workspace key/value memory shared by every chat of this workspace (it is not shown in the system prompt). " +
        "Actions: list (default; keys with size and a short preview), get (key), set (key, value; an empty value deletes the key). " +
        $"Keys: up to {MaxKeyChars} characters of letters, digits and . _ : / -. Values: up to {MaxValueChars} characters; at most {MaxKeys} keys. Never store secrets.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("What to do (default list).", Actions), false),
        ("key", Schema.String("Key to read or write (get, set)."), false),
        ("value", Schema.String("New value (set). Empty deletes the key."), false));

    public string Summarize(JsonObject args)
    {
        var action = Action(args);
        var key = Args.Optional(args, "key");
        return string.IsNullOrEmpty(key) ? action : $"{action} {Text.FirstLine(key, 80)}";
    }

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var action = Action(args);
        var ws = ctx.Workspace.Id;
        try
        {
            return Task.FromResult(action switch
            {
                "list" => List(ws),
                "get" => Get(ws, Args.Required(args, "key").Trim()),
                "set" => Set(ws, Args.Required(args, "key").Trim(), Args.Optional(args, "value") ?? ""),
                _ => ToolResult.Fail($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}."),
            });
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }

    private static string Action(JsonObject args) => (Args.Optional(args, "action") ?? "list").Trim().ToLowerInvariant();

    private List<MemoryEntry> Entries(string ws) => db.Memory.Find(m => m.WorkspaceId == ws).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();

    private ToolResult List(string ws)
    {
        var entries = Entries(ws);
        if (entries.Count == 0) return ToolResult.Ok("Memory is empty (0 keys).");
        var sb = new StringBuilder().AppendLine($"Memory keys ({entries.Count}):");
        foreach (var e in entries)
            sb.AppendLine($"- {e.Key} ({e.Value.Length} chars): {Text.FirstLine(e.Value, PreviewChars)}");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Get(string ws, string key)
    {
        var entry = db.Memory.FindOne(m => m.WorkspaceId == ws && m.Key == key);
        if (entry is not null) return ToolResult.Ok(entry.Value);
        var near = Entries(ws).Where(e => e.Key.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(e.Key, StringComparison.OrdinalIgnoreCase))
            .Take(10).Select(e => e.Key).ToList();
        return ToolResult.Fail($"No memory key '{key}'." + (near.Count > 0 ? $" Similar keys: {string.Join(", ", near)}." : " Use action 'list' to see the available keys."));
    }

    private ToolResult Set(string ws, string key, string value)
    {
        ValidateKey(key);
        var existing = db.Memory.FindOne(m => m.WorkspaceId == ws && m.Key == key);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is null) return ToolResult.Ok($"Key '{key}' not found; nothing to delete.");
            db.Memory.Delete(existing.Id);
            return ToolResult.Ok($"Deleted key '{key}'.");
        }
        if (value.Length > MaxValueChars) return ToolResult.Fail($"The value is {value.Length} characters; the limit is {MaxValueChars}. Shorten it or split it across keys.");
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            if (db.Memory.Count(m => m.WorkspaceId == ws) >= MaxKeys) return ToolResult.Fail($"This workspace already has {MaxKeys} keys; delete some first (set an empty value).");
            db.Memory.Insert(new MemoryEntry { WorkspaceId = ws, Key = key, Value = value, CreatedAt = now, UpdatedAt = now });
            return ToolResult.Ok($"Created key '{key}'.");
        }
        existing.Value = value;
        existing.UpdatedAt = now;
        db.Memory.Update(existing);
        return ToolResult.Ok($"Updated key '{key}'.");
    }

    private static void ValidateKey(string key)
    {
        if (key.Length > MaxKeyChars) throw new ArgumentException($"The key is {key.Length} characters; the limit is {MaxKeyChars}.");
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Keys may only contain letters, digits and . _ : / -");
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$")]
    private static partial Regex KeyPattern();
}
