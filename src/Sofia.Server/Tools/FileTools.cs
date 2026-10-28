using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Sofia.Server.Changes;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

public sealed class ReadTool(Func<byte[], string, string, Attachment>? saveImage = null) : ITool
{
    private const int DefaultLimit = 2000;
    private const int MaxImageBytes = 15 * 1024 * 1024;
    private const int MaxMediaBytes = 20 * 1024 * 1024;

    public string Name => "read";
    public string Description =>
        "Read a text file. Returns the raw content (no line numbers). Large files are truncated; use offset/limit (1-based lines) to page. Reading a directory lists it. " +
        "Image, audio and video files (png, jpg, gif, webp, wav, mp3, ogg, flac, m4a, mp4, mov, webm, mkv, avi) are attached; you only perceive them if the model accepts that input, so say so if you cannot.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("path", Schema.String("File path, relative to the working directory or absolute."), true),
        ("offset", Schema.Integer("First line to read (1-based)."), false),
        ("limit", Schema.Integer("Maximum number of lines to read."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "path", "file_path", "filePath") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        if (Directory.Exists(path)) return ToolResult.Ok(LsTool.List(path, ctx.Cwd, 500));
        if (!File.Exists(path)) return ToolResult.Fail($"File not found: {path}");

        var info = new FileInfo(path);
        var sample = new byte[Math.Min(8000, info.Length)];
        await using (var fs = File.OpenRead(path)) _ = await fs.ReadAsync(sample, ct);
        if (Sniff(sample, Path.GetExtension(path).ToLowerInvariant()) is var (kind, mime))
        {
            if (saveImage is null) return ToolResult.Fail($"{path} is {kind} media; it cannot be attached in this setup.");
            var limit = kind == "image" ? MaxImageBytes : MaxMediaBytes;
            if (info.Length > limit) return ToolResult.Fail($"{path} is {kind} of {Text.HumanSize(info.Length)}; the limit is {Text.HumanSize(limit)}. Use a shell tool (e.g. ffmpeg) to shrink or convert it.");
            var media = saveImage(await File.ReadAllBytesAsync(path, ct), mime, Path.GetFileName(path));
            return ToolResult.WithImages($"{char.ToUpperInvariant(kind[0])}{kind[1..]} {path} ({media.Size}, {mime}) attached. If you cannot take {kind} input, say so instead of guessing its content.", [media]);
        }
        if (PathUtil.IsBinary(sample)) return ToolResult.Fail($"{path} is a binary file ({Text.HumanSize(info.Length)}); it cannot be read as text.");

        var lines = Text.NormalizeNewlines(await File.ReadAllTextAsync(path, ct)).Split('\n');
        var offset = Math.Max(1, Args.Int(args, "offset") ?? 1);
        var maxLines = Math.Max(1, Args.Int(args, "limit") ?? DefaultLimit);
        if (offset > lines.Length) return ToolResult.Fail($"Offset {offset} is past the end of the file ({lines.Length} lines).");

        var slice = lines.Skip(offset - 1).Take(maxLines).ToArray();
        var text = string.Join('\n', slice);
        var maxChars = ctx.Config.MaxToolOutput;
        var truncatedByBytes = false;
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
            var cut = text.LastIndexOf('\n');
            if (cut > 0) text = text[..cut];
            truncatedByBytes = true;
            slice = text.Split('\n');
        }
        var lastLine = offset + slice.Length - 1;
        if (lastLine < lines.Length || truncatedByBytes)
            text += $"\n\n[Showing lines {offset}-{lastLine} of {lines.Length}. Use offset={lastLine + 1} to continue.]";
        return ToolResult.Ok(text);
    }

    /// <summary>Detects image, audio and video files from magic bytes, so renamed files are still recognised.</summary>
    private static (string Kind, string Mime)? Sniff(byte[] b, string ext)
    {
        bool At(int offset, string text) => b.Length >= offset + text.Length && text.Select((c, i) => b[offset + i] == c).All(x => x);

        if (b.Length >= 8 && b[0] == 0x89 && At(1, "PNG")) return ("image", "image/png");
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("image", "image/jpeg");
        if (At(0, "GIF8")) return ("image", "image/gif");
        if (At(0, "RIFF"))
        {
            if (At(8, "WEBP")) return ("image", "image/webp");
            if (At(8, "WAVE")) return ("audio", "audio/wav");
            if (At(8, "AVI ")) return ("video", "video/x-msvideo");
            return null;
        }
        if (At(0, "ID3")) return ("audio", "audio/mpeg");
        // A bare MPEG frame sync is also how UTF-16 text starts, so it needs a matching extension.
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0 && ext is ".mp3" or ".mpga") return ("audio", "audio/mpeg");
        if (At(0, "OggS")) return ext == ".ogv" ? ("video", "video/ogg") : ("audio", "audio/ogg");
        if (At(0, "fLaC")) return ("audio", "audio/flac");
        if (At(4, "ftyp"))
        {
            if (At(8, "qt  ")) return ("video", "video/quicktime");
            return At(8, "M4A ") || ext is ".m4a" or ".m4b" ? ("audio", "audio/mp4") : ("video", "video/mp4");
        }
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        {
            return ext switch
            {
                ".mka" => ("audio", "audio/x-matroska"),
                ".weba" => ("audio", "audio/webm"),
                ".mkv" => ("video", "video/x-matroska"),
                _ => ("video", "video/webm"),
            };
        }
        return null;
    }
}

public sealed class WriteTool : ITool
{
    public string Name => "write";
    public string Description => "Create a new file or completely overwrite an existing one. Parent directories are created. Prefer `edit` for changes to existing files.";
    public ToolKind Kind => ToolKind.Write;
    public JsonObject Parameters => Schema.Object(
        ("path", Schema.String("File path to write."), true),
        ("content", Schema.String("Full file content."), true));

    public string Summarize(JsonObject args) => Args.Optional(args, "path", "file_path", "filePath") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        var content = Args.Raw(args, "content", "text") ?? throw new ToolArgumentException("Missing required argument 'content'.");
        var before = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        ctx.Changes.Capture(ctx.Session.Id, ctx.Message, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Keep the file's existing line-ending style.
        if (before?.Contains("\r\n") == true && !content.Contains("\r\n")) content = content.Replace("\n", "\r\n");
        await File.WriteAllTextAsync(path, content, ct);
        var rel = PathUtil.Relative(ctx.Cwd, path);
        var (_, add, del) = UnifiedDiff.Create(before, content, rel);
        return ToolResult.Ok($"{(before is null ? "Created" : "Overwrote")} {rel} ({Encoding.UTF8.GetByteCount(content)} bytes, +{add} -{del}).");
    }
}

public sealed class EditTool : ITool
{
    public string Name => "edit";
    public string Description =>
        "Replace an exact text fragment in a file. `oldText` must match the file exactly (including indentation) and be unique unless `replaceAll` is true. Read the file first.";
    public ToolKind Kind => ToolKind.Write;
    public JsonObject Parameters => Schema.Object(
        ("path", Schema.String("File path to edit."), true),
        ("oldText", Schema.String("Exact text to find."), true),
        ("newText", Schema.String("Replacement text."), true),
        ("replaceAll", Schema.Boolean("Replace every occurrence instead of exactly one."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "path", "file_path", "filePath") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        var oldText = Args.Raw(args, "oldText", "old_string", "oldString", "old_text") ?? throw new ToolArgumentException("Missing required argument 'oldText'.");
        var newText = Args.Raw(args, "newText", "new_string", "newString", "new_text") ?? throw new ToolArgumentException("Missing required argument 'newText'.");
        var replaceAll = Args.Bool(args, "replaceAll") || Args.Bool(args, "replace_all");
        if (!File.Exists(path)) return ToolResult.Fail($"File not found: {path}. Use `write` to create new files.");
        if (oldText.Length == 0) return ToolResult.Fail("oldText must not be empty.");
        if (oldText == newText) return ToolResult.Fail("oldText and newText are identical; nothing to change.");

        var original = await File.ReadAllTextAsync(path, ct);
        var (updated, count, error) = Apply(original, oldText, newText, replaceAll);
        if (error is not null) return ToolResult.Fail(error);

        ctx.Changes.Capture(ctx.Session.Id, ctx.Message, path);
        await File.WriteAllTextAsync(path, updated, ct);
        var rel = PathUtil.Relative(ctx.Cwd, path);
        var (patch, add, del) = UnifiedDiff.Create(original, updated, rel);
        return ToolResult.Ok($"Edited {rel}: {count} replacement{(count == 1 ? "" : "s")} (+{add} -{del}).\n\n{Text.Truncate(patch, 4000)}");
    }

    /// <summary>Exact match first, then line-ending tolerant, then trailing-whitespace tolerant.</summary>
    internal static (string Updated, int Count, string? Error) Apply(string content, string oldText, string newText, bool replaceAll)
    {
        var crlf = content.Contains("\r\n");
        var candidates = new List<(string Old, string New)> { (oldText, newText) };
        if (crlf)
            candidates.Add((Text.NormalizeNewlines(oldText).Replace("\n", "\r\n"), Text.NormalizeNewlines(newText).Replace("\n", "\r\n")));
        else
            candidates.Add((Text.NormalizeNewlines(oldText), Text.NormalizeNewlines(newText)));

        foreach (var (o, n) in candidates)
        {
            var count = CountOccurrences(content, o);
            if (count == 0) continue;
            if (count > 1 && !replaceAll)
                return ("", 0, $"oldText was found {count} times. Include more surrounding lines to make it unique, or set replaceAll=true.");
            return (replaceAll ? content.Replace(o, n) : ReplaceFirst(content, o, n), replaceAll ? count : 1, null);
        }

        // Whitespace-tolerant fallback: compare line by line ignoring trailing spaces/indent drift.
        var newline = crlf ? "\r\n" : "\n";
        var fileLines = Text.NormalizeNewlines(content).Split('\n');
        var findLines = Text.NormalizeNewlines(oldText).Trim('\n').Split('\n');
        var matches = new List<int>();
        for (var i = 0; i + findLines.Length <= fileLines.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < findLines.Length && ok; j++)
                ok = fileLines[i + j].Trim() == findLines[j].Trim();
            if (ok) matches.Add(i);
        }
        if (matches.Count == 0)
            return ("", 0, "oldText was not found in the file. Read the file again and copy the exact text (including indentation).");
        if (matches.Count > 1 && !replaceAll)
            return ("", 0, $"oldText matched {matches.Count} places (ignoring whitespace). Include more context to make it unique.");

        var replacement = Text.NormalizeNewlines(newText).Trim('\n').Split('\n');
        var result = new List<string>();
        var cursor = 0;
        foreach (var start in matches)
        {
            if (start < cursor) continue;
            result.AddRange(fileLines[cursor..start]);
            result.AddRange(replacement);
            cursor = start + findLines.Length;
        }
        result.AddRange(fileLines[cursor..]);
        return (string.Join(newline, result), matches.Count, null);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}

public sealed class LsTool : ITool
{
    public string Name => "ls";
    public string Description => "List the entries of a directory (directories end with '/').";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(("path", Schema.String("Directory to list (defaults to the working directory)."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "path") ?? ".";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Optional(args, "path") ?? ".");
        if (!Directory.Exists(path)) return Task.FromResult(ToolResult.Fail($"Directory not found: {path}"));
        return Task.FromResult(ToolResult.Ok(List(path, ctx.Cwd, 1000)));
    }

    internal static string List(string path, string cwd, int limit)
    {
        var dir = new DirectoryInfo(path);
        var entries = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.Name + "/")
            .Concat(dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.Name))
            .ToList();
        var sb = new StringBuilder();
        sb.Append(PathUtil.Relative(cwd, path) is var rel && rel.Length > 0 && rel != path ? rel : path).Append(":\n");
        foreach (var e in entries.Take(limit)) sb.Append(e).Append('\n');
        if (entries.Count > limit) sb.Append($"… {entries.Count - limit} more entries\n");
        if (entries.Count == 0) sb.Append("(empty)\n");
        return sb.ToString().TrimEnd();
    }
}

public sealed class FindTool : ITool
{
    public string Name => "find";
    public string Description => "Find files by glob pattern (e.g. `**/*.cs`, `src/**/test_*.py`). Automatically skips dependency/build/cache folders (.git, node_modules, bin, obj, target, vendor, venv, __pycache__, dist, build and similar); to look inside one, pass it explicitly as `path`.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("pattern", Schema.String("Glob pattern relative to the search directory."), true),
        ("path", Schema.String("Directory to search (defaults to the working directory)."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "pattern", "glob") ?? "";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var pattern = Args.Required(args, "pattern", "glob");
        var root = PathUtil.Resolve(ctx.Cwd, Args.Optional(args, "path") ?? ".");
        if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Fail($"Directory not found: {root}"));
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern.Contains('/') || pattern.StartsWith("**") ? pattern : "**/" + pattern);
        foreach (var ignored in PathUtil.IgnoredDirs) matcher.AddExclude($"**/{ignored}/**");
        var results = matcher.GetResultsInFullPath(root).Take(1001).ToList();
        if (results.Count == 0) return Task.FromResult(ToolResult.Ok("No files found."));
        var lines = results.Take(1000).Select(p => PathUtil.Relative(ctx.Cwd, p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var output = string.Join('\n', lines);
        if (results.Count > 1000) output += "\n… more than 1000 results; narrow the pattern.";
        return Task.FromResult(ToolResult.Ok(output));
    }
}

public sealed class GrepTool : ITool
{
    private const int MaxMatches = 200;
    private static readonly Lazy<string?> Ripgrep = new(() => ProcessRunner.FindOnPath(OperatingSystem.IsWindows() ? "rg.exe" : "rg"));

    public string Name => "grep";
    public string Description => "Search file contents with a regular expression. Returns `path:line: text` matches. Respects .gitignore when ripgrep is installed.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("pattern", Schema.String("Regular expression to search for."), true),
        ("path", Schema.String("File or directory to search (defaults to the working directory)."), false),
        ("glob", Schema.String("Only search files matching this glob, e.g. `*.ts`."), false),
        ("ignoreCase", Schema.Boolean("Case-insensitive search."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "pattern") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var pattern = Args.Required(args, "pattern", "regex", "query");
        var root = PathUtil.Resolve(ctx.Cwd, Args.Optional(args, "path") ?? ".");
        var glob = Args.Optional(args, "glob", "include");
        var ignoreCase = Args.Bool(args, "ignoreCase") || Args.Bool(args, "-i");
        if (!File.Exists(root) && !Directory.Exists(root)) return ToolResult.Fail($"Path not found: {root}");

        if (Ripgrep.Value is { } rg)
        {
            var rgArgs = new List<string> { "--line-number", "--no-heading", "--color", "never", "--max-columns", "300", "--max-count", "50" };
            if (ignoreCase) rgArgs.Add("-i");
            if (!string.IsNullOrEmpty(glob)) { rgArgs.Add("-g"); rgArgs.Add(glob); }
            rgArgs.Add("-e");
            rgArgs.Add(pattern);
            rgArgs.Add(root);
            var (exit, stdout, stderr) = await ProcessRunner.ExecAsync(rg, rgArgs, ctx.Cwd, ct, TimeSpan.FromSeconds(60));
            if (exit == 1) return ToolResult.Ok("No matches found.");
            if (exit != 0) return ToolResult.Fail(stderr.Trim());
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => MakeRelative(l.TrimEnd('\r'), ctx.Cwd)).ToList();
            var shown = string.Join('\n', lines.Take(MaxMatches));
            if (lines.Count > MaxMatches) shown += $"\n… {lines.Count - MaxMatches} more matches; refine the pattern.";
            return ToolResult.Ok(Text.Truncate(shown, ctx.Config.MaxToolOutput));
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"Invalid regular expression: {ex.Message}");
        }

        Matcher? include = null;
        if (!string.IsNullOrEmpty(glob))
        {
            include = new Matcher(StringComparison.OrdinalIgnoreCase);
            include.AddInclude(glob.Contains('/') ? glob : "**/" + glob);
        }

        var results = new List<string>();
        var files = File.Exists(root) ? [root] : EnumerateFiles(root);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (include is not null && !include.Match(root, file).HasMatches) continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2 * 1024 * 1024) continue;
                var bytes = await File.ReadAllBytesAsync(file, ct);
                if (PathUtil.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                for (var i = 0; i < lines.Length && results.Count <= MaxMatches; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    var text = lines[i].TrimEnd('\r');
                    if (text.Length > 300) text = text[..300] + "…";
                    results.Add($"{PathUtil.Relative(ctx.Cwd, file)}:{i + 1}: {text}");
                }
            }
            catch (IOException)
            {
                // Unreadable files are skipped.
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological patterns skip the file rather than hanging the run.
            }
            if (results.Count > MaxMatches) break;
        }
        if (results.Count == 0) return ToolResult.Ok("No matches found.");
        var output = string.Join('\n', results.Take(MaxMatches));
        if (results.Count > MaxMatches) output += "\n… more matches; refine the pattern.";
        return ToolResult.Ok(output);
    }

    private static string MakeRelative(string line, string cwd)
    {
        // rg prints `C:\path\file:12:text`; keep the drive colon intact.
        var start = line.Length > 2 && line[1] == ':' ? 2 : 0;
        var colon = line.IndexOf(':', start);
        if (colon <= 0) return line;
        return PathUtil.Relative(cwd, line[..colon]) + line[colon..];
    }

    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!PathUtil.IgnoredDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}
