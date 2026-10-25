using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Agent;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Lets an agent keep notes: workspace notes are durable facts shared by every chat of the workspace (and injected into
/// its system prompt), chat notes are a scratchpad for the current conversation that survives compaction. Subagents share
/// the notes of the chat that spawned them. Notes live in Sofia's own database, not in the workspace folder, so the tool
/// is always allowed, even in read-only and plan modes.
/// </summary>
public sealed class NotesTool(KnowledgeService knowledge) : ITool
{
    private static readonly string[] Actions = ["list", "save", "delete"];
    private static readonly string[] Scopes = [KnowledgeScope.Workspace, KnowledgeScope.Chat];

    public string Name => "notes";
    public string Description =>
        "Read and save notes. Scope 'workspace' keeps short durable facts (project conventions, commands, decisions) for every chat of this workspace and shows up in the system prompt; " +
        "scope 'chat' is a scratchpad for the current conversation (progress, findings, plans) that survives compaction. " +
        $"Actions: list (default; scope omitted = both), save (text; pass id to rewrite an existing note), delete (id). Workspace notes are limited to {KnowledgeService.MaxNoteChars} characters; never store secrets.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("What to do (default list).", Actions), false),
        ("scope", Schema.Enum("'workspace' or 'chat'. Required for save and delete; list covers both when omitted. Default for save: chat.", Scopes), false),
        ("text", Schema.String("The note text (save)."), false),
        ("id", Schema.String("Note id from a list (delete, or save to rewrite that note)."), false));

    public string Summarize(JsonObject args)
    {
        var action = Action(args);
        var scope = Args.Optional(args, "scope");
        var text = Args.Optional(args, "text");
        return $"{action}{(string.IsNullOrEmpty(scope) ? "" : " " + scope)}{(action == "save" && !string.IsNullOrEmpty(text) ? ": " + Text.FirstLine(text, 80) : "")}";
    }

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var action = Action(args);
        var scope = Args.Optional(args, "scope")?.Trim().ToLowerInvariant();
        var chatId = ctx.Session.ParentId ?? ctx.Session.Id;
        try
        {
            return Task.FromResult(action switch
            {
                "list" => List(ctx, scope, chatId),
                "save" => Save(ctx, scope ?? KnowledgeScope.Chat, chatId, Args.Required(args, "text"), Args.Optional(args, "id")),
                "delete" => Delete(ctx, scope, chatId, Args.Required(args, "id")),
                _ => ToolResult.Fail($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}."),
            });
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }

    private static string Action(JsonObject args) => (Args.Optional(args, "action") ?? "list").Trim().ToLowerInvariant();

    private ToolResult List(ToolContext ctx, string? scope, string chatId)
    {
        if (scope is not null && !Scopes.Contains(scope)) return ToolResult.Fail("Scope must be 'workspace' or 'chat'.");
        var sb = new StringBuilder();
        foreach (var s in scope is null ? Scopes : [scope])
        {
            var notes = knowledge.Notes(s, ctx.Workspace.Id, chatId);
            sb.AppendLine($"{(s == KnowledgeScope.Chat ? "Chat" : "Workspace")} notes ({notes.Count}):");
            if (notes.Count == 0) sb.AppendLine("(none)");
            foreach (var note in notes) sb.AppendLine($"- [{note.Id}] {note.Text}");
            sb.AppendLine();
        }
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Save(ToolContext ctx, string scope, string chatId, string text, string? id)
    {
        var note = knowledge.SaveNote(scope, ctx.Session, chatId, text, id);
        return ToolResult.Ok($"Saved {scope} note [{note.Id}].");
    }

    private ToolResult Delete(ToolContext ctx, string? scope, string chatId, string id)
    {
        if (scope is null || !Scopes.Contains(scope)) return ToolResult.Fail("Scope must be 'workspace' or 'chat'.");
        return knowledge.DeleteNote(scope, ctx.Workspace.Id, chatId, id)
            ? ToolResult.Ok($"Deleted {scope} note [{id}].")
            : ToolResult.Fail($"No {scope} note with id '{id}'.");
    }
}
