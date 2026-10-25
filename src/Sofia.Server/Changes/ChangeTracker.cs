using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Changes;

public sealed record FileDiff(string File, string Patch, int Additions, int Deletions, string Status);

public static class UnifiedDiff
{
    /// <summary>Builds a unified diff (3 lines of context) between two texts.</summary>
    public static (string Patch, int Additions, int Deletions) Create(string? oldText, string? newText, string path, int context = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var result = Differ.Instance.CreateDiffs(string.Join('\n', oldLines), string.Join('\n', newLines), false, false, LineChunker.Instance);
        // Index through the chunker's own pieces so block offsets always line up.
        oldLines = result.PiecesOld.Select(p => p.TrimEnd('\r', '\n')).ToArray();
        newLines = result.PiecesNew.Select(p => p.TrimEnd('\r', '\n')).ToArray();
        var blocks = result.DiffBlocks.OrderBy(b => b.DeleteStartA).ToList();
        if (blocks.Count == 0) return ("", 0, 0);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldText is null ? "/dev/null" : "a/" + path).Append('\n');
        sb.Append("+++ ").Append(newText is null ? "/dev/null" : "b/" + path).Append('\n');
        int additions = 0, deletions = 0;

        var groups = new List<List<DiffPlex.Model.DiffBlock>>();
        foreach (var block in blocks)
        {
            if (groups.Count > 0)
            {
                var prev = groups[^1][^1];
                if (block.DeleteStartA - (prev.DeleteStartA + prev.DeleteCountA) <= context * 2)
                {
                    groups[^1].Add(block);
                    continue;
                }
            }
            groups.Add([block]);
        }

        foreach (var group in groups)
        {
            var first = group[0];
            var startA = Math.Max(0, first.DeleteStartA - context);
            var startB = first.InsertStartB - (first.DeleteStartA - startA);
            var a = startA;
            var lines = new List<string>();
            int oldCount = 0, newCount = 0;
            foreach (var block in group)
            {
                for (; a < block.DeleteStartA; a++)
                {
                    lines.Add(" " + oldLines[a]);
                    oldCount++;
                    newCount++;
                }
                for (var i = 0; i < block.DeleteCountA; i++)
                {
                    lines.Add("-" + oldLines[block.DeleteStartA + i]);
                    oldCount++;
                    deletions++;
                }
                for (var i = 0; i < block.InsertCountB; i++)
                {
                    lines.Add("+" + newLines[block.InsertStartB + i]);
                    newCount++;
                    additions++;
                }
                a = block.DeleteStartA + block.DeleteCountA;
            }
            var end = Math.Min(oldLines.Length, a + context);
            for (; a < end; a++)
            {
                lines.Add(" " + oldLines[a]);
                oldCount++;
                newCount++;
            }
            sb.Append("@@ -").Append(oldCount == 0 ? startA : startA + 1).Append(',').Append(oldCount)
              .Append(" +").Append(newCount == 0 ? startB : startB + 1).Append(',').Append(newCount).Append(" @@\n");
            foreach (var line in lines) sb.Append(line).Append('\n');
        }
        return (sb.ToString(), additions, deletions);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var normalized = Text.NormalizeNewlines(text);
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }
}

/// <summary>
/// Records the content of every file a session touches, the first time each message
/// touches it. That gives a per-session diff and per-message revert without git.
/// </summary>
public sealed class ChangeTracker(SofiaDb db)
{
    private const long MaxTrackedBytes = 4 * 1024 * 1024;

    public void Capture(string sessionId, Message message, string absolutePath)
    {
        var path = PathUtil.Normalize(absolutePath);
        var exists = db.Snapshots.Exists(s => s.SessionId == sessionId && s.MessageId == message.Id && s.Path == path);
        if (exists) return;
        var snapshot = new FileSnapshot
        {
            Id = Ids.New("snp"),
            SessionId = sessionId,
            MessageId = message.Id,
            MessageSeq = message.Seq,
            Path = path,
            Existed = File.Exists(path),
        };
        if (snapshot.Existed)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTrackedBytes) return;
            snapshot.Content = File.ReadAllText(path);
        }
        db.Snapshots.Insert(snapshot);
    }

    public List<FileDiff> SessionDiff(string sessionId, string root)
    {
        var originals = db.Snapshots.Find(s => s.SessionId == sessionId)
            .GroupBy(s => s.Path)
            .Select(g => g.OrderBy(s => s.MessageSeq).First())
            .ToList();
        var diffs = new List<FileDiff>();
        foreach (var original in originals.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            var existsNow = File.Exists(original.Path);
            string? current = existsNow ? SafeRead(original.Path) : null;
            if (existsNow && current is null) continue;
            var before = original.Existed ? original.Content : null;
            if (before == current || (before is null && current is null)) continue;
            var rel = PathUtil.Relative(root, original.Path);
            var (patch, add, del) = UnifiedDiff.Create(before, current, rel);
            if (patch.Length == 0) continue;
            var status = !original.Existed ? "added" : !existsNow ? "deleted" : "modified";
            diffs.Add(new FileDiff(rel, patch, add, del, status));
        }
        return diffs;
    }

    /// <summary>Restores every file changed at or after <paramref name="fromSeq"/> and returns their paths.</summary>
    public List<string> Revert(string sessionId, long fromSeq)
    {
        var snapshots = db.Snapshots.Find(s => s.SessionId == sessionId && s.MessageSeq >= fromSeq).ToList();
        var restored = new List<string>();
        foreach (var group in snapshots.GroupBy(s => s.Path))
        {
            var original = group.OrderBy(s => s.MessageSeq).First();
            try
            {
                if (original.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original.Path)!);
                    File.WriteAllText(original.Path, original.Content);
                }
                else if (File.Exists(original.Path))
                {
                    File.Delete(original.Path);
                }
                restored.Add(original.Path);
            }
            catch (IOException)
            {
                // A locked file stays as-is; the others are still restored.
            }
        }
        foreach (var s in snapshots) db.Snapshots.Delete(s.Id);
        return restored;
    }

    private static string? SafeRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTrackedBytes) return null;
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
