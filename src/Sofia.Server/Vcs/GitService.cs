using System.Text;
using Sofia.Server.Changes;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Vcs;

public sealed record VcsInfo(bool IsRepo, string Branch, List<string> Branches);

public sealed record WorktreeEntry(string Directory, string Branch, bool IsMain);

/// <summary>Thin wrapper over the git CLI.</summary>
public sealed class GitService
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private static readonly Lazy<string> Git = new(() => ProcessRunner.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") ?? "git");

    private static async Task<(int Code, string Out, string Err)> RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        try
        {
            return await ProcessRunner.ExecAsync(Git.Value, ["-c", "core.quotepath=off", "-c", "color.ui=never", .. args], cwd, ct, TimeSpan.FromSeconds(30));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, "", "git is not installed");
        }
    }

    public async Task<string?> RootAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        var (code, output, _) = await RunAsync(directory, ct, "rev-parse", "--show-toplevel");
        return code == 0 ? Path.GetFullPath(output.Trim()) : null;
    }

    public async Task<VcsInfo> InfoAsync(string directory, CancellationToken ct)
    {
        var root = await RootAsync(directory, ct);
        if (root is null) return new VcsInfo(false, "", []);
        var (_, current, _) = await RunAsync(directory, ct, "branch", "--show-current");
        var (_, list, _) = await RunAsync(directory, ct, "branch", "--format=%(refname:short)");
        var branches = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var branch = current.Trim();
        if (branch.Length == 0)
        {
            var (_, sha, _) = await RunAsync(directory, ct, "rev-parse", "--short", "HEAD");
            branch = sha.Trim().Length > 0 ? $"detached@{sha.Trim()}" : "";
        }
        return new VcsInfo(true, branch, branches);
    }

    /// <summary>Working tree (tracked + untracked) against HEAD.</summary>
    public async Task<List<FileDiff>> DiffAsync(string directory, CancellationToken ct)
    {
        var root = await RootAsync(directory, ct);
        if (root is null) return [];
        var (headCode, _, _) = await RunAsync(root, ct, "rev-parse", "--verify", "HEAD");
        var baseRef = headCode == 0 ? "HEAD" : EmptyTree;
        var (_, patch, _) = await RunAsync(root, ct, "diff", baseRef, "--no-ext-diff", "-U3", "--find-renames");
        var diffs = ParsePatch(patch);

        var (_, untracked, _) = await RunAsync(root, ct, "ls-files", "--others", "--exclude-standard");
        foreach (var rel in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(200))
        {
            var full = Path.Combine(root, rel);
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > 1024 * 1024) continue;
                var bytes = await File.ReadAllBytesAsync(full, ct);
                if (PathUtil.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                var (p, add, del) = UnifiedDiff.Create(null, Encoding.UTF8.GetString(bytes), rel);
                diffs.Add(new FileDiff(rel, p, add, del, "added"));
            }
            catch (IOException)
            {
                // Skip files that vanish or are locked.
            }
        }
        return diffs;
    }

    /// <summary>
    /// The absolute (`/`-separated) form of those <paramref name="paths"/> Git excludes through .gitignore, the global
    /// or the repository-local excludes — the files that are not workspace changes. Paths may be absolute or relative
    /// to <paramref name="directory"/>; ones outside the repository are never reported, and neither are tracked files
    /// (like <c>git status</c>). Fails open: without a usable git nothing is treated as ignored.
    /// </summary>
    public async Task<HashSet<string>> IgnoredAsync(string directory, IEnumerable<string> paths, CancellationToken ct)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = await RootAsync(directory, ct);
        if (root is null) return ignored;

        var candidates = new List<string>();
        var cwd = Path.GetFullPath(directory);
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            // Paths are given relative to the caller's directory, which may be a sub-folder of the repository.
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
            // Git has no ignore rules for files that live outside the repository: leave those visible.
            var rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) continue;
            candidates.Add(full.Replace('\\', '/'));
        }

        foreach (var chunk in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(64))
        {
            var (code, output, _) = await RunAsync(root, ct, ["check-ignore", "--", .. chunk]);
            // 0 = at least one path ignored, 1 = none; anything else means git cannot answer: keep the paths visible.
            if (code != 0 && code != 1) break;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ignored.Add(line.Replace('\\', '/'));
        }
        return ignored;
    }

    internal static List<FileDiff> ParsePatch(string patch)
    {
        var result = new List<FileDiff>();
        foreach (var chunk in ("\n" + Text.NormalizeNewlines(patch)).Split("\ndiff --git ", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = chunk.Split('\n');
            var header = lines[0];
            var status = "modified";
            string? file = null;
            int add = 0, del = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "deleted";
                else if (line.StartsWith("+++ b/", StringComparison.Ordinal)) file = line[6..];
                else if (line.StartsWith("--- a/", StringComparison.Ordinal) && file is null) file = line[6..];
                else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) add++;
                else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)) del++;
            }
            if (file is null)
            {
                // Binary or mode-only changes: take the path from the header.
                var bIndex = header.LastIndexOf(" b/", StringComparison.Ordinal);
                if (bIndex < 0) continue;
                file = header[(bIndex + 3)..];
            }
            var hunkStart = chunk.IndexOf("\n--- ", StringComparison.Ordinal);
            var body = hunkStart >= 0 ? chunk[(hunkStart + 1)..] : "";
            result.Add(new FileDiff(file, body, add, del, status));
        }
        return result;
    }

    public async Task<(string Root, List<WorktreeEntry> Worktrees)?> WorktreesAsync(string directory, CancellationToken ct)
    {
        var root = await RootAsync(directory, ct);
        if (root is null) return null;
        var (code, output, _) = await RunAsync(root, ct, "worktree", "list", "--porcelain");
        if (code != 0) return (root, [new WorktreeEntry(root, "", true)]);
        var entries = new List<WorktreeEntry>();
        string? path = null;
        var branch = "";
        foreach (var line in (output + "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = Path.GetFullPath(line[9..].Trim());
                branch = "";
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line[7..].Trim().Replace("refs/heads/", "");
            }
            else if (line.Trim().Length == 0 && path is not null)
            {
                entries.Add(new WorktreeEntry(path, branch, entries.Count == 0));
                path = null;
            }
        }
        var main = entries.FirstOrDefault()?.Directory ?? root;
        return (main, entries);
    }

    public async Task<WorktreeEntry> CreateWorktreeAsync(string directory, string name, CancellationToken ct)
    {
        var info = await WorktreesAsync(directory, ct) ?? throw new InvalidOperationException("Not a git repository.");
        var slug = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) throw new InvalidOperationException("Invalid worktree name.");
        var parent = Path.GetDirectoryName(info.Root) ?? info.Root;
        var target = Path.Combine(parent, $"{Path.GetFileName(info.Root)}.worktrees", slug);
        var (code, _, err) = await RunAsync(info.Root, ct, "worktree", "add", "-b", slug, target);
        if (code != 0)
        {
            // The branch may already exist: check it out instead of creating it.
            (code, _, err) = await RunAsync(info.Root, ct, "worktree", "add", target, slug);
            if (code != 0) throw new InvalidOperationException(err.Trim());
        }
        return new WorktreeEntry(Path.GetFullPath(target), slug, false);
    }

    public async Task RemoveWorktreeAsync(string directory, string worktree, bool force, CancellationToken ct)
    {
        var info = await WorktreesAsync(directory, ct) ?? throw new InvalidOperationException("Not a git repository.");
        if (PathUtil.Same(info.Root, worktree)) throw new InvalidOperationException("The main checkout cannot be removed.");
        string[] args = force ? ["worktree", "remove", "--force", worktree] : ["worktree", "remove", worktree];
        var (code, _, err) = await RunAsync(info.Root, ct, args);
        if (code != 0) throw new InvalidOperationException(err.Trim());
    }
}
