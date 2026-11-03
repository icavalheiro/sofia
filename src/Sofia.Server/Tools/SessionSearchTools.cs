using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Search;

namespace Sofia.Server.Tools;

public sealed class SessionSearchTool(ISessionSearch search, SofiaDb db) : ITool
{
    public string Name => "session_search";
    public string Description =>
        "Search past conversations for a query. Returns session-grouped results with snippets. " +
        "Scope: 'workspace' (default, current workspace) or 'all'. Read-only.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("query", Schema.String("Text to search for."), true),
        ("scope", Schema.Enum("workspace (default, current workspace) or all.", new[] { "workspace", "all" }), false),
        ("limit", Schema.Integer("Max sessions to return (default 10)."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "query") ?? "";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var query = Args.Required(args, "query");
        var scope = (Args.Optional(args, "scope") ?? "workspace").Trim().ToLowerInvariant();
        var limit = Args.Int(args, "limit") ?? 10;
        if (limit < 1) limit = 1;
        if (limit > 50) limit = 50;

        var workspaceId = scope == "all" ? null : ctx.Workspace.Id;
        var results = search.Search(db, query, workspaceId, false, false, limit);
        // Exclude the calling session.
        results = results.Where(r => r.SessionId != ctx.Session.Id).ToList();

        if (results.Count == 0) return Task.FromResult(ToolResult.Ok("No matching sessions."));

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"Session: {r.Title} ({r.SessionId})");
            foreach (var h in r.Hits)
            {
                sb.AppendLine($"  [{h.Role}] {Redact(h.Snippet)}");
            }
            sb.AppendLine();
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        result = Regex.Replace(result, @"sk-[A-Za-z0-9_\-]{16,}", "[redacted]");
        result = Regex.Replace(result, @"ghp_[A-Za-z0-9]{20,}", "[redacted]");
        result = Regex.Replace(result, @"xox[baprs]-[A-Za-z0-9\-]{10,}", "[redacted]");
        result = Regex.Replace(result, @"Bearer\s+[A-Za-z0-9_\-\.]{16,}", "Bearer [redacted]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?i)(api[_-]?key|token|secret|password)\s*[:=]\s*[""']?[A-Za-z0-9_\-]{16,}", "$1=[redacted]");
        return result;
    }
}

public sealed class SessionReadTool(SofiaDb db) : ITool
{
    public string Name => "session_read";
    public string Description =>
        "Read a past conversation's transcript. Params: sessionId, optional fromSeq, optional around (messageId), optional maxMessages (<=40). Read-only; same workspace unless scope 'all'.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("sessionId", Schema.String("Session id to read."), true),
        ("fromSeq", Schema.Integer("Start at this message seq (inclusive)."), false),
        ("around", Schema.String("Message id to read around."), false),
        ("maxMessages", Schema.Integer("Max messages to return (<=40)."), false),
        ("scope", Schema.Enum("workspace (default) or all.", new[] { "workspace", "all" }), false));

    public string Summarize(JsonObject args) => Args.Required(args, "sessionId");

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var sessionId = Args.Required(args, "sessionId");
        var session = db.Sessions.FindById(sessionId) ?? throw new ArgumentException("Session not found.");
        var scope = (Args.Optional(args, "scope") ?? "workspace").Trim().ToLowerInvariant();
        if (scope != "all" && session.WorkspaceId != ctx.Workspace.Id)
            return Task.FromResult(ToolResult.Fail("Session is not in this workspace."));

        var maxMessages = Args.Int(args, "maxMessages") ?? 20;
        if (maxMessages < 1) maxMessages = 1;
        if (maxMessages > 40) maxMessages = 40;

        var fromSeq = Args.Int(args, "fromSeq");
        var around = Args.Optional(args, "around");

        var messages = db.SessionMessages(sessionId);
        IEnumerable<Message> selected;
        if (!string.IsNullOrEmpty(around))
        {
            var target = messages.FirstOrDefault(m => m.Id == around);
            var targetSeq = target?.Seq ?? messages.FirstOrDefault()?.Seq ?? 0;
            var before = maxMessages / 2;
            var after = maxMessages - before;
            var ordered = messages.OrderBy(m => m.Seq).ToList();
            var idx = ordered.FindIndex(m => m.Id == around);
            if (idx < 0) idx = ordered.FindIndex(m => m.Seq >= targetSeq);
            if (idx < 0) idx = 0;
            var start = Math.Max(0, idx - before);
            var end = Math.Min(ordered.Count, start + maxMessages);
            selected = ordered.Skip(start).Take(end - start);
        }
        else if (fromSeq.HasValue)
        {
            selected = messages.Where(m => m.Seq >= fromSeq.Value).OrderBy(m => m.Seq).Take(maxMessages);
        }
        else
        {
            selected = messages.OrderBy(m => m.Seq).Take(maxMessages);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<session_transcript id=\"{session.Id}\" title=\"{session.Title}\">");
        foreach (var m in selected)
        {
            if (m.Role is not ("user" or "assistant")) continue;
            var text = m.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            // Skip tool raw outputs: only the assistant's own text is shown.
            sb.AppendLine($"[{m.Role} #{m.Seq}] {Redact(text)}");
        }
        sb.AppendLine("</session_transcript>");
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    private static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        // Common token/key patterns.
        result = Regex.Replace(result, @"sk-[A-Za-z0-9_\-]{16,}", "[redacted]");
        result = Regex.Replace(result, @"ghp_[A-Za-z0-9]{20,}", "[redacted]");
        result = Regex.Replace(result, @"xox[baprs]-[A-Za-z0-9\-]{10,}", "[redacted]");
        result = Regex.Replace(result, @"Bearer\s+[A-Za-z0-9_\-\.]{16,}", "Bearer [redacted]", RegexOptions.IgnoreCase);
        // Generic secret assignments.
        result = Regex.Replace(result, @"(?i)(api[_-]?key|token|secret|password)\s*[:=]\s*[""']?[A-Za-z0-9_\-]{16,}", "$1=[redacted]");
        return result;
    }
}
