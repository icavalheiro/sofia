using Sofia.Core.Agent;
using Sofia.Core.Changes;
using Sofia.Core.Data;
using Sofia.Core.Vcs;

namespace Sofia.Core.Services;

/// <summary>
/// The workspace changes of a conversation and its subagent conversations, with git-ignored files removed: the panel
/// lists what a commit could carry. Snapshots stay untouched, so reverting a message still restores ignored files.
/// </summary>
public sealed class DiffService(AgentService agents, ChangeTracker changes, GitService git, SofiaDb db)
{
    public async Task<List<FileDiff>> ForSessionAsync(string sessionId, CancellationToken ct)
    {
        var (_, workspace) = agents.Load(sessionId);
        var ids = new List<string> { sessionId };
        ids.AddRange(Descendants(sessionId));
        var diffs = ids.SelectMany(id => changes.SessionDiff(id, workspace.Path))
            .GroupBy(d => d.File).Select(g => g.Last()).ToList();
        var paths = diffs.Select(d => Absolute(workspace.Path, d.File)).ToList();
        var ignored = await git.IgnoredAsync(workspace.Path, paths, ct);
        return diffs.Where((_, index) => !ignored.Contains(paths[index])).ToList();
    }

    private IEnumerable<string> Descendants(string id)
    {
        foreach (var child in db.Sessions.Find(s => s.ParentId == id).ToList())
        {
            yield return child.Id;
            foreach (var grandchild in Descendants(child.Id)) yield return grandchild;
        }
    }

    /// <summary>Absolute `/`-separated path of a diff entry (`FileDiff.File` is workspace-relative).</summary>
    private static string Absolute(string root, string file) =>
        Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(root, file)).Replace('\\', '/');
}
