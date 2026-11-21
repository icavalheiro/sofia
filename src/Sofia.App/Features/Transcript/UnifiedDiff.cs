namespace Sofia.App.Features.Transcript;

public enum DiffRowKind
{
    Context,
    Add,
    Delete,
}

/// <summary>One line of a unified patch, numbered the way <c>diff</c> numbers it.</summary>
public sealed record DiffRow(int? OldNumber, int? NewNumber, DiffRowKind Kind, string Text);

/// <summary>
/// Unified-diff parsing for tool output (port of <c>services/diff.ts</c>): successful <c>edit</c>/<c>write</c>/
/// <c>apply_patch</c> results carry prose followed by a <c>---</c>/<c>+++</c>/<c>@@</c> hunk body; the transcript shows
/// that body with +green/−red rows instead of plain text.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Tools whose successful output is rendered as a diff (web <c>DIFF_TOOLS</c>).</summary>
    public static readonly HashSet<string> DiffTools = new(StringComparer.Ordinal) { "edit", "write", "apply_patch" };

    /// <summary>The diff rows of a tool output, or null when the output carries no hunk.</summary>
    public static IReadOnlyList<DiffRow>? FromToolOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var lines = output.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("--- ", StringComparison.Ordinal));
        if (start < 0) return null;

        // Keep only the real hunk body: `---`/`+++`/`@@` headers plus the +/-/blank body lines that follow.
        var body = lines.Skip(start)
            .Where(line => line.Length == 0 || line[0] is '+' or '-' || line.StartsWith("@@", StringComparison.Ordinal));
        var rows = Parse(string.Join('\n', body));
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>Parses a unified patch body into numbered rows (git headers and "\ No newline" markers are skipped).</summary>
    public static IReadOnlyList<DiffRow> Parse(string patch)
    {
        var rows = new List<DiffRow>();
        int? oldNumber = null;
        int? newNumber = null;

        foreach (var raw in patch.Replace("\r", "").Split('\n'))
        {
            var hunk = System.Text.RegularExpressions.Regex.Match(
                raw, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
            if (hunk.Success)
            {
                oldNumber = int.Parse(hunk.Groups[1].Value);
                newNumber = int.Parse(hunk.Groups[2].Value);
                continue;
            }
            if (raw.StartsWith("---", StringComparison.Ordinal) ||
                raw.StartsWith("+++", StringComparison.Ordinal) ||
                raw.StartsWith("diff ", StringComparison.Ordinal) ||
                raw.StartsWith("index ", StringComparison.Ordinal) ||
                raw.StartsWith("\\ ", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith('-'))
            {
                rows.Add(new DiffRow(oldNumber, null, DiffRowKind.Delete, raw[1..]));
                if (oldNumber is not null) oldNumber++;
            }
            else if (raw.StartsWith('+'))
            {
                rows.Add(new DiffRow(null, newNumber, DiffRowKind.Add, raw[1..]));
                if (newNumber is not null) newNumber++;
            }
            else
            {
                rows.Add(new DiffRow(oldNumber, newNumber, DiffRowKind.Context, raw.Length > 0 ? raw[1..] : ""));
                if (oldNumber is not null) oldNumber++;
                if (newNumber is not null) newNumber++;
            }
        }

        return rows;
    }
}
