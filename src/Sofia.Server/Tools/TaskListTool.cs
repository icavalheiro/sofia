using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// A per-session task list the agent uses to plan multi-step work: create items before starting, mark them
/// in_progress / completed as it goes, and make sure every item is finished before the reply ends. The data lives
/// in Sofia's own database, so the tool is always allowed, even in read-only and plan modes. The list is broadcast
/// to the UI through the <c>tasks.updated</c> event.
/// </summary>
public sealed class TaskListTool : ITool
{
    private static readonly string[] Actions = ["list", "add", "set", "update", "remove", "clear"];
    private static readonly string[] Statuses = [TaskItemStatus.Pending, TaskItemStatus.InProgress, TaskItemStatus.Completed, TaskItemStatus.Cancelled];

    private readonly SofiaDb _db;
    private readonly IEventBus? _bus;
    private readonly string _name;

    public TaskListTool(SofiaDb db, IEventBus? bus = null, string name = "tasks")
    {
        _db = db;
        _bus = bus;
        _name = string.IsNullOrWhiteSpace(name) ? "tasks" : name;
    }

    public string Name => _name;
    public string Description =>
        "Manage the task list for the current conversation: plan multi-step work, track each item's status (pending, " +
        "in_progress, completed, cancelled) and make sure everything is done before finishing. Actions: list (default), " +
        "add (one or many), set (replace the whole list), update (change an item's title/status), remove (delete an item), " +
        "clear (delete all, or all of a given status). Item ids come back from list/add and are used by update/remove.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("What to do (default list).", Actions), false),
        ("id", Schema.String("Task id (update/remove)."), false),
        ("title", Schema.String("Task title (add/update)."), false),
        ("content", Schema.String("Alias for title."), false),
        ("text", Schema.String("Alias for title."), false),
        ("status", Schema.Enum("Task status.", Statuses), false),
        ("items", Schema.Array("Tasks to add or replace. Strings or objects with title/content/text and status.", new JsonObject { ["type"] = "object" }), false),
        ("tasks", Schema.Array("Alias for items.", new JsonObject { ["type"] = "object" }), false),
        ("todos", Schema.Array("Alias for items.", new JsonObject { ["type"] = "object" }), false));

    public string Summarize(JsonObject args)
    {
        var action = Action(args);
        var title = TitleOf(args);
        if (!string.IsNullOrEmpty(title)) return $"{action}: {Text.FirstLine(title, 80)}";
        var items = CollectItems(args, null).Count;
        if (items > 0) return $"{action}: {items} item(s)";
        var id = Args.Optional(args, "id");
        if (!string.IsNullOrEmpty(id)) return $"{action} {id}";
        return action;
    }

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var action = Action(args);
        var sessionId = ctx.Session.Id;
        var workspaceId = ctx.Workspace.Id;

        // A collection passed without an explicit action is treated as an add, so a stray call never wipes the list.
        if (action == "list" && HasCollection(args) && !args.ContainsKey("action"))
            action = "add";

        try
        {
            var result = action switch
            {
                "list" => List(sessionId),
                "add" => Add(ctx, sessionId, workspaceId, args),
                "set" => Set(sessionId, workspaceId, args),
                "update" => Update(sessionId, args),
                "remove" or "delete" => Remove(sessionId, args),
                "clear" => Clear(sessionId, args),
                _ => ToolResult.Fail($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}."),
            };
            if (!result.IsError) Publish(sessionId, workspaceId);
            return Task.FromResult(result);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }

    private static string Action(JsonObject args) => (Args.Optional(args, "action") ?? "list").Trim().ToLowerInvariant();

    private static bool HasCollection(JsonObject args) =>
        args["items"] is JsonArray || args["tasks"] is JsonArray || args["todos"] is JsonArray;

    private static string TitleOf(JsonObject args) =>
        Args.Optional(args, "title", "content", "text", "name", "label") ?? "";

    private static List<(string Title, string Status)> CollectItems(JsonObject args, JsonObject? single)
    {
        var source = single ?? args;
        var result = new List<(string, string)>();

        // A single item described directly on the arguments.
        var directTitle = Args.Optional(source, "title", "content", "text", "name", "label");
        if (!string.IsNullOrEmpty(directTitle) && !HasCollection(source))
            result.Add((directTitle, NormalizeStatus(Args.Optional(source, "status", "state"))));

        foreach (var key in new[] { "items", "tasks", "todos" })
        {
            if (source[key] is not JsonArray arr) continue;
            foreach (var node in arr)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    if (!string.IsNullOrWhiteSpace(s)) result.Add((s.Trim(), TaskItemStatus.Pending));
                    continue;
                }
                if (node is JsonObject o)
                {
                    var t = Args.Optional(o, "title", "content", "text", "name", "label");
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    result.Add((t!.Trim(), NormalizeStatus(Args.Optional(o, "status", "state"))));
                }
            }
        }
        return result;
    }

    private ToolResult List(string sessionId)
    {
        var items = _db.Tasks.Find(t => t.SessionId == sessionId).OrderBy(t => t.Order).ThenBy(t => t.CreatedAt).ToList();
        if (items.Count == 0) return ToolResult.Ok("Task list is empty (0 items).");
        var sb = new StringBuilder().AppendLine($"Tasks ({items.Count}):");
        foreach (var t in items)
            sb.AppendLine($"- [{t.Status}] ({t.Id}) {t.Title}");
        var done = items.Count(t => t.Status == TaskItemStatus.Completed);
        sb.AppendLine($"Completed: {done}/{items.Count}.");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Add(ToolContext ctx, string sessionId, string workspaceId, JsonObject args)
    {
        var items = CollectItems(args, null);
        if (items.Count == 0)
        {
            // Allow an update-style call that only carries a title to create an item.
            var title = TitleOf(args);
            if (!string.IsNullOrEmpty(title)) items.Add((title, NormalizeStatus(Args.Optional(args, "status", "state"))));
        }
        if (items.Count == 0) return ToolResult.Fail("Provide at least one task (title or items).");

        var now = DateTime.UtcNow;
        var nextOrder = (_db.Tasks.Find(t => t.SessionId == sessionId).Select(t => (int?)t.Order).Max() ?? 0) + 1;
        var created = new List<TaskItem>();
        foreach (var (title, status) in items)
        {
            var item = new TaskItem
            {
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                Title = title,
                Status = status,
                Order = nextOrder++,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Tasks.Insert(item);
            created.Add(item);
        }
        var sb = new StringBuilder().AppendLine($"Added {created.Count} task(s):");
        foreach (var t in created) sb.AppendLine($"- [{t.Status}] ({t.Id}) {t.Title}");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Set(string sessionId, string workspaceId, JsonObject args)
    {
        var items = CollectItems(args, null);
        if (items.Count == 0) return ToolResult.Fail("Provide at least one task (items).");
        _db.Tasks.DeleteMany(t => t.SessionId == sessionId);
        var now = DateTime.UtcNow;
        var order = 1;
        foreach (var (title, status) in items)
        {
            _db.Tasks.Insert(new TaskItem
            {
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                Title = title,
                Status = status,
                Order = order++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        return ToolResult.Ok($"Replaced the task list with {items.Count} item(s).");
    }

    private ToolResult Update(string sessionId, JsonObject args)
    {
        var id = Args.Optional(args, "id");
        TaskItem? item = null;
        if (!string.IsNullOrEmpty(id)) item = _db.Tasks.FindById(id);
        if (item is null || item.SessionId != sessionId)
        {
            // Fall back to matching by title.
            var title = TitleOf(args);
            if (!string.IsNullOrEmpty(title))
                item = _db.Tasks.Find(t => t.SessionId == sessionId && t.Title == title).FirstOrDefault();
        }
        if (item is null) return ToolResult.Fail($"No task found{(string.IsNullOrEmpty(id) ? "" : $" with id '{id}'")}.");

        var newTitle = TitleOf(args);
        if (!string.IsNullOrEmpty(newTitle)) item.Title = newTitle;
        var newStatus = Args.Optional(args, "status", "state");
        if (!string.IsNullOrEmpty(newStatus)) item.Status = NormalizeStatus(newStatus);
        item.UpdatedAt = DateTime.UtcNow;
        _db.Tasks.Update(item);
        return ToolResult.Ok($"Updated task ({item.Id}): [{item.Status}] {item.Title}");
    }

    private ToolResult Remove(string sessionId, JsonObject args)
    {
        var id = Args.Optional(args, "id");
        if (!string.IsNullOrEmpty(id))
        {
            var item = _db.Tasks.FindById(id);
            if (item is null || item.SessionId != sessionId) return ToolResult.Fail($"No task with id '{id}'.");
            _db.Tasks.Delete(id);
            return ToolResult.Ok($"Removed task ({id}).");
        }
        var title = TitleOf(args);
        if (!string.IsNullOrEmpty(title))
        {
            var matches = _db.Tasks.Find(t => t.SessionId == sessionId && t.Title == title).ToList();
            foreach (var m in matches) _db.Tasks.Delete(m.Id);
            if (matches.Count == 0) return ToolResult.Fail($"No task with title '{title}'.");
            return ToolResult.Ok($"Removed {matches.Count} task(s) with title '{title}'.");
        }
        return ToolResult.Fail("Provide an id (or title) to remove.");
    }

    private ToolResult Clear(string sessionId, JsonObject args)
    {
        var status = Args.Optional(args, "status", "state");
        if (!string.IsNullOrEmpty(status))
        {
            var normalized = NormalizeStatus(status);
            var count = _db.Tasks.DeleteMany(t => t.SessionId == sessionId && t.Status == normalized);
            return ToolResult.Ok($"Cleared {count} {normalized} task(s).");
        }
        var countAll = _db.Tasks.DeleteMany(t => t.SessionId == sessionId);
        return ToolResult.Ok($"Cleared {countAll} task(s).");
    }

    private static string NormalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return TaskItemStatus.Pending;
        var value = raw.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value switch
        {
            "done" or "complete" or "completed" => TaskItemStatus.Completed,
            "doing" or "in_progress" or "inprogress" or "active" or "running" or "started" => TaskItemStatus.InProgress,
            "cancelled" or "canceled" or "skipped" or "dropped" => TaskItemStatus.Cancelled,
            "pending" or "todo" or "open" or "not_started" or "notstarted" => TaskItemStatus.Pending,
            _ => raw.Trim(),
        };
    }

    private void Publish(string sessionId, string workspaceId)
    {
        if (_bus is null) return;
        var items = _db.Tasks.Find(t => t.SessionId == sessionId).OrderBy(t => t.Order).ThenBy(t => t.CreatedAt)
            .Select(t => new { id = t.Id, title = t.Title, status = t.Status, createdAt = t.CreatedAt, updatedAt = t.UpdatedAt })
            .ToList();
        _bus.Publish(EventTypes.TasksUpdated, new { sessionId, tasks = items }, sessionId, workspaceId);
    }
}