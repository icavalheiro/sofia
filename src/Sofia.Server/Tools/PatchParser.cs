using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>One hunk of an apply_patch update: optional `@@` context, the old/new lines and whether it ends the file.</summary>
public sealed class PatchChunk(string? context, List<string> oldLines, List<string> newLines)
{
    /// <summary>The text after `@@`, or null when the chunk skipped its header (a Codex leniency).</summary>
    public string? Context { get; } = context;
    public List<string> OldLines { get; } = oldLines;
    public List<string> NewLines { get; } = newLines;
    /// <summary>Set when the chunk is closed by `*** End of File`: matching must start at the end of the file.</summary>
    public bool EndOfFile { get; set; }
}

/// <summary>A parsed Codex-style patch hunk (see <see cref="PatchParser"/>).</summary>
public abstract class PatchHunk
{
    /// <summary>1-based line of the `*** …` marker in the patch text.</summary>
    public int Line { get; init; }

    public sealed class Add(string Path, List<string>? lines = null) : PatchHunk
    {
        public string Path { get; } = Path;
        /// <summary>The added lines exactly as written (a trailing blank line is an empty entry).</summary>
        public List<string> Lines { get; } = lines ?? [];
    }

    public sealed class Delete(string Path) : PatchHunk
    {
        public string Path { get; } = Path;
    }

    public sealed class Update(string Path, string? MoveTo, List<PatchChunk> Chunks) : PatchHunk
    {
        public string Path { get; } = Path;
        public string? MoveTo { get; } = MoveTo;
        public List<PatchChunk> Chunks { get; } = Chunks;
    }
}

/// <summary>
/// Pure parser for the OpenAI Codex patch grammar:
/// <c>*** Begin Patch</c> / <c>*** Add File:</c> / <c>*** Update File:</c> (optional <c>*** Move to:</c>) /
/// <c>*** Delete File:</c> / <c>@@ context</c> / <c>-</c>/<c>+</c>/<c> </c> lines / <c>*** End of File</c> /
/// <c>*** End Patch</c>. Codex's leniencies are kept: a heredoc wrapper (<c>&lt;&lt;'EOF' … EOF</c>) is stripped,
/// markers may carry extra surrounding whitespace, the first chunk of an update may skip its `@@` header and blank
/// context lines may be written as empty lines. Parse errors carry 1-based line numbers.
/// </summary>
public static class PatchParser
{
    private const int MaxErrors = 10;

    public static (List<PatchHunk> Hunks, List<string> Errors) Parse(string text)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return ([], ["The patch is empty."]);

        var lines = Text.NormalizeNewlines(text).Split('\n').ToList();
        StripHeredocWrapper(lines);

        // Find the begin marker: it must be the first non-blank line.
        var i = 0;
        while (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Count || !IsMarker(lines[i], "Begin Patch"))
            return ([], [Error(i + 1, "Expected '*** Begin Patch' as the first line.")]);

        var hunks = new List<PatchHunk>();
        i++;
        while (i < lines.Count && errors.Count < MaxErrors)
        {
            if (IsMarker(lines[i], "End Patch")) break;
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

            var line = i + 1;
            string kind, value;
            if (!TryReadMarker(lines[i], out kind, out value))
            {
                errors.Add(Error(line, $"Unexpected line in patch: {Snippet(lines[i])}"));
                i++;
                continue;
            }

            switch (kind)
            {
                case "Add File":
                    (var add, i) = ReadAdd(i, lines, value, errors);
                    if (add is not null) hunks.Add(add);
                    break;
                case "Delete File":
                    if (string.IsNullOrEmpty(value)) errors.Add(Error(line, "Missing file path after '*** Delete File:'."));
                    else hunks.Add(new PatchHunk.Delete(value) { Line = line });
                    i++;
                    break;
                case "Update File":
                    (var update, i) = ReadUpdate(i, lines, value, errors);
                    if (update is not null) hunks.Add(update);
                    break;
                default:
                    errors.Add(Error(line, $"Unknown patch marker '*** {kind}'. Expected Add File / Update File / Delete File."));
                    i++;
                    break;
            }
        }

        if (i < lines.Count && IsMarker(lines[i], "End Patch")) i++;
        while (i < lines.Count)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                errors.Add(Error(i + 1, $"Content after '*** End Patch' is ignored: {Snippet(lines[i])}"));
                break;
            }
            i++;
        }
        if (errors.Count == 0 && hunks.Count == 0) errors.Add("The patch contains no file operations.");

        return (hunks, errors.Take(MaxErrors).ToList());
    }

    /// <summary>Strips a heredoc wrapper (<c>&lt;&lt;'EOF'</c> … <c>EOF</c>) when the whole patch is wrapped in one.</summary>
    private static void StripHeredocWrapper(List<string> lines)
    {
        var first = 0;
        while (first < lines.Count && string.IsNullOrWhiteSpace(lines[first])) first++;
        if (first >= lines.Count) return;
        var open = lines[first].Trim();
        if (!open.StartsWith("<<", StringComparison.Ordinal)) return;
        var delim = open[2..].Trim().Trim('\'', '"');
        if (delim.Length == 0) return;
        var last = lines.Count - 1;
        while (last > first && string.IsNullOrWhiteSpace(lines[last])) last--;
        if (lines[last].Trim() != delim) return;
        // Only strip when the wrapped body really is a patch: this keeps `<<` content that merely
        // starts a file from being mistaken for a wrapper.
        var isPatch = lines.Skip(first + 1).Take(last - first - 1)
            .Any(l => l.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal));
        if (!isPatch) return;
        // Drop the opening line and everything from the closing delimiter on.
        lines.RemoveRange(0, first + 1);
        var close = last - first - 1;   // the closing delimiter's index after the removal above
        lines.RemoveRange(close, lines.Count - close);
    }

    private static bool IsMarker(string line, string name) => TryReadMarker(line, out var kind, out _) && kind == name;

    /// <summary>Reads a `*** Kind: value` marker with Codex's whitespace leniency. Returns false for non-markers.</summary>
    private static bool TryReadMarker(string line, out string kind, out string value)
    {
        var t = line.Trim();
        if (!t.StartsWith("***", StringComparison.Ordinal)) { kind = ""; value = ""; return false; }
        var rest = t[3..].TrimStart();
        var colon = rest.IndexOf(':');
        if (colon < 0) { kind = rest.Trim(); value = ""; return true; }
        kind = rest[..colon].Trim();
        value = rest[(colon + 1)..].Trim();
        return true;
    }

    private static bool IsHunkStart(string line) =>
        IsMarker(line, "Add File") || IsMarker(line, "Update File") || IsMarker(line, "Delete File") || IsMarker(line, "End Patch");

    private static (PatchHunk.Add? Hunk, int Next) ReadAdd(int i, List<string> lines, string path, List<string> errors)
    {
        var line = i + 1;
        if (string.IsNullOrEmpty(path))
        {
            errors.Add(Error(line, "Missing file path after '*** Add File:'."));
            return (null, i + 1);
        }
        var hunk = new PatchHunk.Add(path, []) { Line = line };
        i++;
        while (i < lines.Count && !IsHunkStart(lines[i]))
        {
            var raw = lines[i];
            if (raw.Length == 0) hunk.Lines.Add("");   // blank line: kept (a trailing one means a final newline)
            else if (raw[0] == '+') hunk.Lines.Add(raw[1..]);
            else errors.Add(Error(i + 1, $"Expected a '+' line in the added file {path}: {Snippet(raw)}"));
            i++;
        }
        return (hunk, i);
    }

    private static (PatchHunk.Update? Hunk, int Next) ReadUpdate(int i, List<string> lines, string path, List<string> errors)
    {
        var line = i + 1;
        if (string.IsNullOrEmpty(path))
        {
            errors.Add(Error(line, "Missing file path after '*** Update File:'."));
            return (null, i + 1);
        }
        i++;
        string? moveTo = null;
        if (i < lines.Count && IsMarker(lines[i], "Move to"))
        {
            TryReadMarker(lines[i], out _, out var dest);
            moveTo = dest;
            if (string.IsNullOrEmpty(dest)) errors.Add(Error(i + 1, "Missing destination path after '*** Move to:'."));
            i++;
        }

        var chunks = new List<PatchChunk>();
        PatchChunk? current = null;

        void Flush()
        {
            if (current is not null)
            {
                chunks.Add(current);
                current = null;
            }
        }

        while (i < lines.Count && !IsHunkStart(lines[i]))
        {
            var l = i + 1;
            var raw = lines[i];

            if (raw.TrimStart().StartsWith("@@", StringComparison.Ordinal))
            {
                // A new `@@` context: close the previous chunk and open a fresh one.
                Flush();
                current = new PatchChunk(raw.TrimStart()[2..].Trim(), [], []);
                i++;
                continue;
            }
            if (IsMarker(raw, "End of File"))
            {
                // Codex leniency: the eof marker may close a chunk that skipped its `@@` header.
                current ??= new PatchChunk(null, [], []);
                current.EndOfFile = true;
                i++;
                continue;
            }

            char prefix;
            if (raw.Length == 0) { prefix = ' '; }          // blank line inside a chunk = empty context line
            else if (raw[0] is '+' or '-' or ' ') { prefix = raw[0]; }
            else
            {
                errors.Add(Error(l, $"Expected '+', '-' or a context line in the update of {path}: {Snippet(raw)}"));
                i++;
                continue;
            }

            if (current is null)
            {
                // First chunk without an `@@` header: allowed by Codex.
                current = new PatchChunk(null, [], []);
            }
            var body = raw.Length > 1 ? raw[1..] : "";
            switch (prefix)
            {
                case '+': current.NewLines.Add(body); break;
                case '-': current.OldLines.Add(body); break;
                default: current.OldLines.Add(body); current.NewLines.Add(body); break;
            }

            i++;
        }
        Flush();

        if (chunks.Count == 0)
        {
            errors.Add(Error(line, $"The update of {path} has no changes."));
            return (null, i);
        }
        return (new PatchHunk.Update(path, moveTo, chunks) { Line = line }, i);
    }

    private static string Error(int line, string message) => $"Line {line}: {message}";

    private static string Snippet(string line)
    {
        var t = line.Trim();
        return t.Length > 80 ? t[..80] + "…" : t;
    }
}
