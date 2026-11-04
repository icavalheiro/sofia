using System.Text.Json.Nodes;
using Sofia.Server.Changes;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Applies an OpenAI Codex-style patch (`*** Begin Patch` … `*** End Patch`) atomically: every hunk is validated and
/// the new content computed in memory first, so a bad hunk leaves no file on disk touched. Update matching uses the
/// same tolerant line-sequence search as `edit`'s fallback (exact → trim-end → trim → Unicode punctuation), starting
/// after the previous chunk like Codex's `seek_sequence`; `*** End of File` anchors at the end. CRLF and a missing
/// final newline are preserved. Every touched path is captured through the change tracker before writing, so the
/// session revert restores updated/deleted/moved files and deletes added ones. Permission gating is the same as
/// `write`/`edit` (`ToolKind.Write`).
/// </summary>
public sealed class ApplyPatchTool(bool readBeforeEdit = false) : ITool
{
    public string Name => "apply_patch";

    /// <summary>The read-before-edit sentence appears only while the policy is on, so toggling it re-freezes the prompt.</summary>
    public string Description =>
        "Apply a Codex-style patch to one or more files at once: *** Begin Patch / *** Add File: / *** Update File: (optional *** Move to:) / *** Delete File: / @@ context lines with -/+/space lines / *** End of File / *** End Patch. " +
        "Atomic: if any hunk is invalid, no file is changed and every error is listed. Updates match context loosely around whitespace." +
        (readBeforeEdit ? " Updating, deleting or moving a file requires reading it first in this conversation; the file must not have changed on disk since." : "");

    public ToolKind Kind => ToolKind.Write;

    public JsonObject Parameters => Schema.Object(
        ("input", Schema.String("The full patch text, from '*** Begin Patch' to '*** End Patch'."), true));

    /// <summary>The permission card summary: the number of files and the first few paths.</summary>
    public string Summarize(JsonObject args)
    {
        var input = Args.Raw(args, "input", "patch", "patchText") ?? "";
        var files = new List<string>();
        foreach (var line in Text.NormalizeNewlines(input).Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("*** Add File: ", StringComparison.Ordinal)) files.Add(t["*** Add File: ".Length..].Trim());
            else if (t.StartsWith("*** Update File: ", StringComparison.Ordinal)) files.Add(t["*** Update File: ".Length..].Trim());
            else if (t.StartsWith("*** Delete File: ", StringComparison.Ordinal)) files.Add(t["*** Delete File: ".Length..].Trim());
        }
        var shown = string.Join(", ", files.Take(5));
        return files.Count > 0 ? $"{files.Count} file{(files.Count == 1 ? "" : "s")}: {shown}{(files.Count > 5 ? ", …" : "")}" : "";
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var input = Args.Raw(args, "input", "patch", "patchText") ?? throw new ToolArgumentException("Missing required argument 'input'.");
        var (hunks, errors) = PatchParser.Parse(input);

        // --- plan phase: resolve paths, validate everything, compute the new content in memory --------------------
        var plan = new List<Planned>();
        var seen = new Dictionary<string, string>(PathComparer.Default);   // resolved path -> label that owns it
        foreach (var hunk in hunks)
        {
            Planned? p = null;
            try
            {
                switch (hunk)
                {
                    case PatchHunk.Add a:
                    {
                        var path = PathUtil.Resolve(ctx.Cwd, a.Path);
                        if (!Claim(seen, path, a.Path, hunk.Line, errors)) break;
                        if (File.Exists(path))
                        {
                            errors.Add($"Line {hunk.Line}: cannot add {a.Path}: the file already exists. Use an Update File hunk instead.");
                            break;
                        }
                        p = new Planned(a.Path, path, OpKind.Add, null, AddContent(a.Lines), null) { Hunk = hunk };
                        break;
                    }
                    case PatchHunk.Delete d:
                    {
                        var path = PathUtil.Resolve(ctx.Cwd, d.Path);
                        if (!Claim(seen, path, d.Path, hunk.Line, errors)) break;
                        p = new Planned(d.Path, path, OpKind.Delete, null, null, null) { Hunk = hunk };
                        break;
                    }
                    case PatchHunk.Update upd:
                    {
                        var path = PathUtil.Resolve(ctx.Cwd, upd.Path);
                        if (!Claim(seen, path, upd.Path, hunk.Line, errors)) break;
                        string? dest = null;
                        if (upd.MoveTo is not null)
                        {
                            dest = PathUtil.Resolve(ctx.Cwd, upd.MoveTo);
                            if (!seen.TryAdd(dest, upd.MoveTo))
                                errors.Add($"Line {hunk.Line}: the move destination {upd.MoveTo} is used by another hunk in the same patch.");
                        }
                        p = new Planned(upd.Path, path, OpKind.Update, null, null, dest) { Hunk = hunk };
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                errors.Add($"Line {hunk.Line}: bad path: {ex.Message}");
                continue;
            }
            if (p is not null) plan.Add(p);
        }

        // Existence, text-ness, the read-before-edit policy and the original content for Update/Delete/Move.
        foreach (var p in plan.Where(p => p.Op is OpKind.Update or OpKind.Delete).ToList())
        {
            if (!File.Exists(p.Path))
            {
                errors.Add($"{p.Label}: file not found: {Rel(ctx, p.Path)}.");
                continue;
            }
            // Size the buffer to the real file length: an oversized buffer would have trailing zeros and read as binary.
            var sample = new byte[Math.Min(8000, new FileInfo(p.Path).Length)];
            await using (var fs = File.OpenRead(p.Path)) _ = await fs.ReadAsync(sample, ct);
            if (PathUtil.IsBinary(sample))
            {
                errors.Add($"{p.Label}: is a binary file; only text files can be patched.");
                continue;
            }
            var denied = ctx.Reads?.Check(ctx.Session.Id, p.Path, ctx.Config.ReadBeforeEdit, ctx.Cwd);
            if (denied is not null) errors.Add(denied);
            try
            {
                p.Before = await File.ReadAllTextAsync(p.Path, ct);   // kept for the diff and for rollback
            }
            catch (IOException ex)
            {
                errors.Add($"{p.Label}: could not read the file: {ex.Message}");
            }
        }

        // Compute the new content of every update in memory.
        foreach (var p in plan.Where(p => p.Op == OpKind.Update).ToList())
        {
            if (p.Before is null) continue;   // already reported above
            var hunk = (PatchHunk.Update)p.Hunk!;
            p.After = ApplyChunks(p.Before, hunk.Chunks, out var chunkErrors);
            if (chunkErrors.Count > 0) errors.AddRange(chunkErrors.Select(e => $"{p.Label}: {e}"));
        }

        if (errors.Count > 0)
            return ToolResult.Fail("Patch is invalid:\n" + string.Join("\n", errors.Take(15)));

        // --- commit phase: capture everything first, then write/delete/move with rollback --------------------------
        foreach (var p in plan)
        {
            ctx.Changes.Capture(ctx.Session.Id, ctx.Message, p.Path);
            if (p.MovedTo is not null) ctx.Changes.Capture(ctx.Session.Id, ctx.Message, p.MovedTo);
        }

        var committed = new List<Planned>();
        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (p.Op)
                {
                    case OpKind.Add:
                        Directory.CreateDirectory(Path.GetDirectoryName(p.Path)!);
                        await File.WriteAllTextAsync(p.Path, p.After!, ct);
                        ctx.Reads?.RecordWrite(ctx.Session.Id, p.Path);
                        break;
                    case OpKind.Delete:
                        File.Delete(p.Path);
                        ctx.Reads?.RecordDelete(ctx.Session.Id, p.Path);
                        break;
                    case OpKind.Update when p.MovedTo is null:
                        await File.WriteAllTextAsync(p.Path, p.After!, ct);
                        ctx.Reads?.RecordWrite(ctx.Session.Id, p.Path);
                        break;
                    case OpKind.Update:
                        if (File.Exists(p.MovedTo!))
                            throw new IOException($"the move destination {Rel(ctx, p.MovedTo)} already exists.");
                        Directory.CreateDirectory(Path.GetDirectoryName(p.MovedTo)!);
                        await File.WriteAllTextAsync(p.MovedTo!, p.After!, ct);
                        File.Delete(p.Path);
                        ctx.Reads?.RecordMove(ctx.Session.Id, p.Path, p.MovedTo!);
                        break;
                }
                committed.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                Rollback(committed);
                return ToolResult.Fail($"The patch could not be applied ({ex.Message}). The files that were already changed have been restored.");
            }
        }

        // --- result: header with totals plus a unified diff per file -----------------------------------------------
        var add = 0;
        var del = 0;
        var changes = new List<FileEditResult.Change>();
        foreach (var p in plan)
        {
            if (p.Op == OpKind.Delete)
            {
                var (_, pd) = CountDiff(p.Before ?? "", null);
                del += pd;
                changes.Add(new FileEditResult.Change(Rel(ctx, p.Path), p.Before ?? "", null, $"Deleted {Rel(ctx, p.Path)}."));
                continue;
            }
            (var pa, var pd2) = CountDiff(p.Before, p.After!);
            add += pa;
            del += pd2;
            var rel = Rel(ctx, p.MovedTo ?? p.Path);
            var label = p.Op == OpKind.Add ? "Added" : p.MovedTo is null ? "Updated" : $"Moved {Rel(ctx, p.Path)} →";
            changes.Add(new FileEditResult.Change(rel, p.Before, p.After!, $"{label} {rel} (+{pa} -{pd2})."));
        }
        var header = $"Applied patch: {plan.Count} file{(plan.Count == 1 ? "" : "s")} (+{add} -{del}).";
        return await FileEditResult.BuildAsync(ctx, header, changes, ct);
    }

    private enum OpKind { Add, Update, Delete }

    /// <summary>One validated hunk ready to commit.</summary>
    private sealed class Planned(string label, string path, OpKind op, string? before, string? after, string? movedTo)
    {
        public string Label { get; } = label;
        public string Path { get; } = path;
        public OpKind Op { get; } = op;
        public string? Before { get; set; } = before;
        public string? After { get; set; } = after;
        public string? MovedTo { get; } = movedTo;
        public PatchHunk? Hunk { get; init; }
    }

    /// <summary>OS-aware path comparison (case-insensitive on Windows/macOS).</summary>
    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static readonly PathComparer Default = new();
        private static bool CaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        public bool Equals(string? a, string? b) =>
            string.Equals(a, b, CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        public int GetHashCode(string obj) =>
            obj.GetHashCode(StringComparison.OrdinalIgnoreCase);   // hash collisions are fine; Equals decides
    }

    private static bool Claim(Dictionary<string, string> seen, string resolvedPath, string label, int line, List<string> errors)
    {
        if (!seen.TryAdd(resolvedPath, label))
        {
            errors.Add($"Line {line}: {label} appears in more than one hunk of the patch; each file may be touched once.");
            return false;
        }
        return true;
    }

    /// <summary>Joins the lines of an Add File hunk; a trailing blank line yields a trailing newline.</summary>
    private static string AddContent(List<string> lines) => string.Join("\n", lines);

    /// <summary>
    /// Applies every chunk of an update in memory, Codex-style: chunks are matched in order, each search starting
    /// after the previous replacement; `*** End of File` anchors the old lines at the end. Returns the new content
    /// and, on failure, one error per unmatched chunk (the original content is returned unchanged).
    /// </summary>
    internal static string ApplyChunks(string original, List<PatchChunk> chunks, out List<string> errors)
    {
        errors = [];
        var crlf = original.Contains("\r\n");
        var eol = crlf ? "\r\n" : "\n";
        var hadFinalNewline = original.Length > 0 && original.EndsWith(eol);
        var lines = Text.NormalizeNewlines(original).Split('\n').ToList();
        if (hadFinalNewline) lines.RemoveAt(lines.Count - 1);

        var cursor = 0;   // search start for the next chunk, after the previous replacement
        foreach (var chunk in chunks)
        {
            int at;
            if (chunk.EndOfFile)
            {
                // Anchor: the old lines must sit at the end of the file.
                var n = chunk.OldLines.Count;
                if (n == 0)
                {
                    at = lines.Count;
                }
                else
                {
                    var tail = lines.Skip(Math.Max(0, lines.Count - n)).ToList();
                    at = TextMatch.SeekSequence(tail, chunk.OldLines.ToList(), 0);
                    if (at < 0)
                    {
                        errors.Add("End-of-file context not found: the last lines of the file do not match the patch.");
                        break;
                    }
                    at += Math.Max(0, lines.Count - n);
                }
            }
            else
            {
                at = TextMatch.SeekSequence(lines, chunk.OldLines.ToList(), cursor);
                if (at < 0)
                {
                    var head = string.Join(" | ", chunk.OldLines.Take(3).Select(l => l.Length > 60 ? l[..60] + "…" : l));
                    errors.Add($"Context not found: expected the lines \"{head}\"{(chunk.OldLines.Count > 3 ? $" (+{chunk.OldLines.Count - 3} more)" : "")} after the previous change. Read the file and fix the hunk.");
                    break;
                }
            }

            lines.RemoveRange(at, chunk.OldLines.Count);
            lines.InsertRange(at, chunk.NewLines);
            cursor = at + chunk.NewLines.Count;
        }

        if (errors.Count > 0) return original;
        var result = string.Join(eol, lines);
        if (hadFinalNewline && lines.Count > 0) result += eol;
        else if (lines.Count == 0) result = "";
        return result;
    }

    /// <summary>Best-effort undo of the committed steps after a mid-commit failure.</summary>
    private static void Rollback(List<Planned> committed)
    {
        foreach (var p in committed.AsEnumerable().Reverse())
        {
            try
            {
                switch (p.Op)
                {
                    case OpKind.Add:
                        if (File.Exists(p.Path)) File.Delete(p.Path);
                        break;
                    case OpKind.Delete:
                        if (p.Before is not null) File.WriteAllText(p.Path, p.Before);
                        break;
                    case OpKind.Update when p.MovedTo is null:
                        if (p.Before is not null) File.WriteAllText(p.Path, p.Before);
                        break;
                    case OpKind.Update:
                        if (File.Exists(p.MovedTo!)) File.Delete(p.MovedTo!);
                        if (p.Before is not null) File.WriteAllText(p.Path, p.Before);
                        break;
                }
            }
            catch (IOException)
            {
                // Best effort: the error message already says what was restored.
            }
        }
    }

    private static (int Add, int Del) CountDiff(string? before, string? after)
    {
        var (_, a, d) = UnifiedDiff.Create(before, after, "");
        return (a, d);
    }

    private static string Rel(ToolContext ctx, string path) => PathUtil.Relative(ctx.Cwd, path);
}
