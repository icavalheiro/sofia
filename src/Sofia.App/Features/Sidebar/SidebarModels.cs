using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Sidebar;

/// <summary>Display status of a sidebar row. Beyond the server's statuses the sidebar knows <c>done</c>: a run that
/// finished while its conversation was not open, cleared the next time the conversation is opened (the web's rule in
/// <c>useHarness.applyStatus</c>).</summary>
public static class SidebarStatus
{
    public const string Idle = "idle";
    public const string Done = "done";

    public static readonly string[] Priority = ["permission", "question", "error", "working", "waiting", Done];

    public static bool IsBusy(string status) => status is "working" or "waiting";
}

/// <summary>One workspace row: the folder/chat group plus the conversation rows it renders while expanded.</summary>
public sealed class WorkspaceRowViewModel(WorkspaceView workspace) : ObservableObject
{
    public WorkspaceView Workspace { get; } = workspace;

    public string Id => Workspace.Id;
    public string Name => Workspace.Name;
    public string Path => Workspace.Path;
    public string Kind => Workspace.Kind;
    public string Icon => Workspace.Icon;
    public string ModelId => Workspace.ModelId;
    public bool Expanded => Workspace.Expanded;
    public int Order => Workspace.Order;
    public bool IsChat => Workspace.Kind == "chat";

    /// <summary>Conversation rows flattened depth-first (subagents nest under their parent), root rows first.</summary>
    public List<ConversationRowViewModel> Tree { get; init; } = [];

    /// <summary>Archived conversations of this workspace, in server order (the "Archived" sub-folder).</summary>
    public List<ConversationRowViewModel> Archived { get; init; } = [];

    /// <summary>Number on the row: every visible root conversation, ignoring subagents and the archive.</summary>
    public int Count { get; init; }

    /// <summary>Highest-priority state across the visible root conversations.</summary>
    public string Status { get; init; } = SidebarStatus.Idle;

    /// <summary>The web marks a collapsed workspace whose conversations are working with the sweep; expanded ones show
    /// the badge on the conversation rows instead.</summary>
    public bool IsWorking => Status == "working" && !Expanded;

    public bool HasStatusBadge => Status != SidebarStatus.Idle;

    public bool HasArchived => Archived.Count > 0;

    public string StatusTitle => SidebarStatusText(Status);

    public static string SidebarStatusText(string status) => status switch
    {
        "working" => "Sofia is working on a task…",
        "waiting" => "Waiting for the model to be free…",
        "done" => "Last task finished",
        "permission" => "Waiting for your permission",
        "question" => "Asking you a question",
        "error" => "The last run failed",
        _ => "",
    };
}

/// <summary>One conversation row, with the state the sidebar renders on it.</summary>
public sealed class ConversationRowViewModel(Session session, int depth) : ObservableObject
{
    public Session Session { get; } = session;

    public string Id => Session.Id;
    public string WorkspaceId => Session.WorkspaceId;
    public string Title => Session.Title;
    public bool Pinned => Session.Pinned;
    public bool Archived => Session.Archived;
    public string? ParentId => Session.ParentId;
    public int Depth { get; } = depth;
    public bool IsSubagent => Depth > 0;

    /// <summary>Display status resolved by the store (server status, the <c>done</c> badge or <c>idle</c>).</summary>
    public string Status { get; init; } = SidebarStatus.Idle;

    public bool HasStatusBadge => Status != SidebarStatus.Idle;

    public bool IsActive { get; init; }

    public double IndentPx => Depth * 14.0; // .875rem at the 16px base

    public bool CanDrag => Depth == 0;
}

/// <summary>One search result row: a session hit with the snippet and its highlight ranges.</summary>
public sealed class SearchHitRow
{
    public string Key { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Snippet { get; init; } = "";
    public IReadOnlyList<(int Start, int Length)> Highlights { get; init; } = [];

    /// <summary>Splits the snippet on the highlight ranges into plain/marked segments (web <c>snippetParts</c>).</summary>
    public IEnumerable<(string Text, bool Mark)> Parts()
    {
        var cursor = 0;
        foreach (var (start, length) in Highlights)
        {
            if (length <= 0 || start < cursor || start >= Snippet.Length) continue;
            if (start > cursor) yield return (Snippet[cursor..start], false);
            var end = Math.Min(Snippet.Length, start + length);
            yield return (Snippet[start..end], true);
            cursor = end;
        }
        if (cursor < Snippet.Length) yield return (Snippet[cursor..], false);
    }
}

/// <summary>One git worktree of a folder workspace (<c>GET /api/worktrees</c>).</summary>
public sealed record WorktreeEntry(string Directory, string Branch, bool IsMain)
{
    public string Name => System.IO.Path.GetFileName(Directory.TrimEnd('/', '\\')) is { Length: > 0 } name
        ? name
        : Directory;

    public static bool SamePath(string? a, string? b)
    {
        static string Norm(string? path) =>
            (path ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return Norm(a) == Norm(b);
    }
}

/// <summary>A model row of the workspace settings menu (<c>GET /api/models</c>).</summary>
public sealed record ModelChoice(string Id, string Label, bool Local, string Icon);
