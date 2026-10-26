using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Context;

internal static class Frontmatter
{
    /// <summary>Parses a leading `---` block of `key: value` lines; returns the fields and the body.</summary>
    public static (Dictionary<string, string> Fields, string Body) Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = Text.NormalizeNewlines(text);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (fields, normalized);
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (fields, normalized);
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }
        var bodyStart = normalized.IndexOf('\n', end + 4);
        return (fields, bodyStart < 0 ? "" : normalized[(bodyStart + 1)..].TrimStart('\n'));
    }
}

public sealed class SkillInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>'global' | 'project'.</summary>
    public string Source { get; set; } = "global";
}

/// <summary>Discovers SKILL.md packages (progressive disclosure: only name + description go in the prompt).</summary>
public sealed class SkillService(SofiaOptions options)
{
    public List<SkillInfo> List(string? directory)
    {
        var byName = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        Scan(options.SkillsDir, "global", byName);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            foreach (var root in ConfigPaths.ProjectRoots(directory, "skills")) Scan(root, "project", byName);
        return byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Scan(string root, string source, Dictionary<string, SkillInfo> into)
    {
        if (!Directory.Exists(root)) return;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, MatchCasing = MatchCasing.CaseInsensitive });
        }
        catch (IOException)
        {
            return;
        }
        foreach (var file in files)
        {
            try
            {
                var (fields, body) = Frontmatter.Parse(File.ReadAllText(file));
                var name = fields.GetValueOrDefault("name") ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "skill";
                var description = fields.GetValueOrDefault("description")
                    ?? body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
                // Later roots (project) override earlier ones (global) with the same name.
                into[name] = new SkillInfo { Id = name, Name = name, Description = description, Path = file, Content = body, Source = source };
            }
            catch (IOException)
            {
                // Unreadable skill files are skipped.
            }
        }
    }
}

public sealed class CommandInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>'builtin' | 'global' | 'project'.</summary>
    public string Source { get; set; } = "builtin";
    /// <summary>Frontmatter `agent`: the agent that runs the command (a primary one is selected, a subagent gets the work delegated).</summary>
    public string? Agent { get; set; }
    /// <summary>Frontmatter `model` ('providerId/modelId'): switches the session to it for this command.</summary>
    public string? Model { get; set; }
    /// <summary>Frontmatter `subtask: true`: the work is delegated to a subagent instead of running in the main context.</summary>
    public bool Subtask { get; set; }
    public string? Path { get; set; }
    [JsonIgnore] public string Template { get; set; } = "";
}

/// <summary>
/// Custom /commands: Markdown prompt templates plus built-ins. Files live in `~/.sofia/commands`, `~/.sofia/prompts`,
/// `<workspace>/.agents/commands`, `.agents/prompts`, `<workspace>/.sofia/commands` and `.sofia/prompts` (later roots win,
/// so `.sofia` overrides `.agents`, which overrides global; other harnesses' folders are never read); a sub-folder
/// becomes part of the name (`git/pr.md` → `/git/pr`). Frontmatter: `description`, `agent`, `model`, `subtask`.
/// Templates support `$ARGUMENTS`, `$1`…`$9`, ``!`shell`` output injection and `@file` references.
/// </summary>
public sealed partial class CommandService(SofiaOptions options)
{
    public const string InitPrompt =
        "Analyze this codebase and create (or improve) an AGENTS.md file at the repository root for future coding agents. " +
        "Include: build/test/lint commands (and how to run a single test), the high-level architecture, code style and conventions, " +
        "and anything non-obvious. Keep it concise. If an AGENTS.md, CLAUDE.md or similar file already exists, improve it instead of replacing it. $ARGUMENTS";

    public IEnumerable<string> GlobalRoots()
    {
        yield return options.PromptsDir;
        yield return options.CommandsDir;
    }

    /// <summary>The workspace's own command folders, in override order (<c>.agents</c> then <c>.sofia</c>; prompts before commands).</summary>
    public static IEnumerable<string> ProjectRoots(string directory) => ConfigPaths.ProjectRoots(directory, "prompts", "commands");

    public List<CommandInfo> List(string? directory)
    {
        var byName = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["compact"] = new() { Name = "compact", Description = "Summarize the conversation to free context", Source = "builtin" },
            ["init"] = new() { Name = "init", Description = "Create or update AGENTS.md for this project", Source = "builtin", Template = InitPrompt },
        };
        foreach (var root in GlobalRoots()) Scan(root, "global", byName);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            foreach (var root in ProjectRoots(directory)) Scan(root, "project", byName);
        return byName.Values.OrderBy(c => c.Source == "builtin" ? 0 : 1).ThenBy(c => c.Name).ToList();
    }

    private static void Scan(string root, string source, Dictionary<string, CommandInfo> into)
    {
        if (!Directory.Exists(root)) return;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (IOException)
        {
            return;
        }
        foreach (var file in files)
        {
            try
            {
                var name = Path.ChangeExtension(Path.GetRelativePath(root, file), null).Replace('\\', '/');
                if (name.Any(char.IsWhiteSpace)) continue;
                var (fields, body) = Frontmatter.Parse(File.ReadAllText(file));
                into[name] = new CommandInfo
                {
                    Name = name,
                    Description = fields.GetValueOrDefault("description") ?? Text.FirstLine(body, 90),
                    Source = source,
                    Agent = fields.GetValueOrDefault("agent") is { Length: > 0 } agent ? agent : null,
                    Model = fields.GetValueOrDefault("model") is { Length: > 0 } model ? model : null,
                    Subtask = fields.TryGetValue("subtask", out var subtask) && bool.TryParse(subtask, out var isSubtask) && isSubtask,
                    Path = file,
                    Template = body,
                };
            }
            catch (IOException)
            {
                // Skip unreadable templates.
            }
        }
    }

    [GeneratedRegex(@"!`([^`\n]+)`")]
    private static partial Regex ShellInjection();

    [GeneratedRegex(@"(?<![\w`@])@([\w.\-/\\]+)")]
    private static partial Regex FileReference();

    /// <summary>
    /// Resolves the dynamic parts of an expanded template: ``!`cmd` `` becomes the command's output (only when
    /// <paramref name="runShell"/> allows it, otherwise it stays literal for the model to run under the normal
    /// permission rules) and `@path` references to files inside the workspace are appended as `&lt;file&gt;` blocks.
    /// </summary>
    public static async Task<string> ExpandDynamicAsync(string text, string? cwd, bool runShell, ShellSpec shell, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return text;

        if (runShell && text.Contains("!`", StringComparison.Ordinal))
        {
            var sb = new System.Text.StringBuilder();
            var last = 0;
            foreach (Match match in ShellInjection().Matches(text))
            {
                sb.Append(text, last, match.Index - last);
                var result = await ProcessRunner.RunShellAsync(shell, match.Groups[1].Value, cwd, TimeSpan.FromSeconds(30), null, 20_000, ct);
                sb.Append(Text.Truncate(result.Output.TrimEnd(), 20_000));
                if (result.TimedOut) sb.Append("\n(command timed out)");
                last = match.Index + match.Length;
            }
            sb.Append(text, last, text.Length - last);
            text = sb.ToString();
        }

        var files = new List<(string Path, string Content)>();
        foreach (Match match in FileReference().Matches(text))
        {
            if (files.Count >= 8) break;
            var candidate = match.Groups[1].Value.TrimEnd('.');
            if (candidate.Length == 0 || ReadReference(cwd, candidate) is not { } file || files.Any(f => f.Path == file.Path)) continue;
            files.Add(file);
        }
        if (files.Count == 0) return text;
        var blocks = files.Select(f => $"<file path=\"{f.Path}\">\n{f.Content}\n</file>");
        return text + "\n\n" + string.Join("\n", blocks);
    }

    /// <summary>Reads a text file that sits inside the workspace; anything else is not a reference.</summary>
    private static (string Path, string Content)? ReadReference(string cwd, string candidate)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(cwd, candidate));
            var relative = Path.GetRelativePath(cwd, full);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || !File.Exists(full)) return null;
            var sample = new byte[4096];
            int read;
            using (var stream = File.OpenRead(full)) read = stream.Read(sample, 0, sample.Length);
            if (PathUtil.IsBinary(sample.AsSpan(0, read))) return null;
            return (relative.Replace('\\', '/'), Text.Truncate(File.ReadAllText(full), 40_000));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Expand(string template, string arguments)
    {
        var args = SplitArgs(arguments);
        var result = template.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
        for (var i = 9; i >= 1; i--)
            result = result.Replace("$" + i, i <= args.Count ? args[i - 1] : "", StringComparison.Ordinal);
        if (!template.Contains("$ARGUMENTS", StringComparison.Ordinal) && !template.Contains("$1", StringComparison.Ordinal) && arguments.Length > 0)
            result += "\n\n" + arguments;
        return result.Trim();
    }

    private static List<string> SplitArgs(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var c in value)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}

public sealed record ContextFile(string Path, string Content);

/// <summary>AGENTS.md / CLAUDE.md discovery (global, then every parent down to the workspace) and SYSTEM.md overrides.</summary>
public sealed class ContextFiles(SofiaOptions options)
{
    private const int MaxFileChars = 40_000;

    public List<ContextFile> Load(string? directory)
    {
        var files = new List<ContextFile>();
        TryAdd(files, options.GlobalAgentsFile);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return files;

        var chain = new List<string>();
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent) chain.Add(dir.FullName);
        chain.Reverse();
        foreach (var dir in chain)
        {
            if (!TryAdd(files, Path.Combine(dir, "AGENTS.md"))) TryAdd(files, Path.Combine(dir, "CLAUDE.md"));
        }
        return files;
    }

    /// <summary>
    /// Returns (replacement, addendum) from the workspace's `SYSTEM.md` / `APPEND_SYSTEM.md` — Sofia's own
    /// <see cref="ConfigPaths.ProjectFolder"/> first, then the shared <see cref="ConfigPaths.SharedProjectFolder"/> —
    /// over the harness-home ones.
    /// </summary>
    public (string? Replace, string? Append) SystemOverrides(string? directory)
    {
        string? Read(string name)
        {
            if (!string.IsNullOrEmpty(directory))
            {
                foreach (var path in ConfigPaths.ProjectFolders.Reverse().Select(f => Path.Combine(directory, f, name)))
                    if (File.Exists(path)) return File.ReadAllText(path);
            }
            var global = Path.Combine(options.DataDir, name);
            return File.Exists(global) ? File.ReadAllText(global) : null;
        }
        return (Read("SYSTEM.md"), Read("APPEND_SYSTEM.md"));
    }

    private static bool TryAdd(List<ContextFile> files, string path)
    {
        if (!File.Exists(path)) return false;
        if (files.Any(f => PathUtil.Same(f.Path, path))) return true;
        try
        {
            var content = File.ReadAllText(path);
            files.Add(new ContextFile(path, Text.Truncate(content, MaxFileChars)));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
