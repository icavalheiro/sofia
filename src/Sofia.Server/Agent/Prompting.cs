using System.Text;
using Sofia.Server.Context;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Providers;
using Sofia.Server.Tools;

namespace Sofia.Server.Agent;

/// <summary>
/// An agent: a tool set plus a prompt addendum. <see cref="Mode"/> is 'primary' (selectable for a session),
/// 'subagent' (delegated to with the `task` tool or an @mention) or 'all'. <see cref="Source"/> is
/// 'builtin' | 'global' | 'project'; <see cref="Path"/> is the Markdown file of a custom agent.
/// </summary>
public sealed record AgentDefinition(string Id, string Name, string Description, string Mode, string[] Tools, bool IncludeMcp, string? Addendum,
    string Source = "builtin", string? Path = null)
{
    public bool IsPrimary => Mode is "primary" or "all";
    public bool IsDelegable => Mode is "subagent" or "all";
    public bool HasTaskTool => Tools.Any(t => t is "task" or "task:explore");
    public bool ReadOnly => !Tools.Any(t => t is "write" or "edit" or Agents.ShellToolToken);
}

public static class Agents
{
    public const string ShellToolToken = "$shell";

    public static readonly AgentDefinition[] All =
    [
        new("build", "Build", "Default agent: reads, edits, runs commands and delegates to subagents.", "primary",
            ["read", "write", "edit", ShellToolToken, "grep", "find", "ls", "web_fetch", "web_search", "browser", "question", "task", "skill", "notes", "sofia_info"], true, null),
        new("plan", "Plan", "Research-only: explores with read-only tools and proposes a plan without changing files.", "primary",
            ["read", "grep", "find", "ls", "web_fetch", "web_search", "question", "task:explore", "skill", "notes", "sofia_info"], true,
            "You are in PLAN mode. Investigate with read-only tools, then present a concrete, step-by-step plan. Do not modify files or run commands that change state."),
        new("general", "General", "Subagent for multi-step tasks that may edit files and run commands.", "subagent",
            ["read", "write", "edit", ShellToolToken, "grep", "find", "ls", "web_fetch", "web_search", "browser", "skill", "notes", "sofia_info"], true,
            "You are a subagent. Complete the task autonomously, then reply with a concise report of what you did and found. The user cannot answer questions."),
        new("explore", "Explore", "Read-only subagent for fast codebase research.", "subagent",
            ["read", "grep", "find", "ls", "web_fetch", "web_search", "skill", "notes", "sofia_info"], false,
            "You are a read-only research subagent. Search and read efficiently, then reply with a concise, factual report including file paths and line references. The user cannot answer questions."),
    ];

    public static AgentDefinition Get(string? id) => All.FirstOrDefault(a => a.Id == id) ?? All[0];
}

public static class SystemPromptBuilder
{
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["pt"] = "Brazilian Portuguese",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["zh"] = "Simplified Chinese",
        ["ja"] = "Japanese",
    };

    /// <summary>The answer language is the UI language unless the user explicitly asks for another one.</summary>
    private static string LanguageGuideline(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "- Reply in the language the user writes in.";
        var name = LanguageNames.GetValueOrDefault(code.Trim(), code.Trim());
        return $"- Answer in {name} (the user's interface language) unless the user explicitly asks for a different language. Keep code, identifiers and file paths unchanged.";
    }

    public static string Build(
        AgentDefinition agent,
        Workspace workspace,
        Session session,
        IReadOnlyList<ITool> tools,
        IReadOnlyList<ContextFile> contextFiles,
        IReadOnlyList<SkillInfo> skills,
        (string? Replace, string? Append) overrides,
        ShellSpec shell,
        string? branch,
        IReadOnlyList<KnowledgeNote>? knowledge = null)
    {
        var folder = workspace.IsFolder;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(overrides.Replace))
        {
            sb.AppendLine(overrides.Replace.Trim());
        }
        else if (folder)
        {
            sb.AppendLine("You are Sofia, an expert coding assistant running inside the Sofia harness. You help the user by reading files, running commands, editing code and writing new files.");
        }
        else
        {
            sb.AppendLine("You are Sofia, a helpful and knowledgeable assistant. Answer clearly and accurately; use the available tools when they help.");
        }

        if (tools.Count > 0)
        {
            sb.AppendLine().AppendLine("<tools>");
            foreach (var tool in tools.Where(t => t.Kind != ToolKind.External))
                sb.Append("- ").Append(tool.Name).Append(": ").AppendLine(Text.FirstLine(tool.Description, 200));
            var external = tools.Count(t => t.Kind == ToolKind.External);
            if (external > 0) sb.AppendLine($"- plus {external} MCP tools (named `server__tool`).");
            sb.AppendLine("</tools>");
        }

        sb.AppendLine().AppendLine("<guidelines>");
        if (folder)
        {
            sb.AppendLine("- Explore with grep/find/ls before reading; read files before editing them.");
            sb.AppendLine("- Use edit for precise changes (oldText must match the file exactly); use write only for new files or full rewrites.");
            sb.AppendLine($"- Use the {ShellName(tools, shell)} tool for builds, tests and git. Commands run non-interactively in the working directory.");
            sb.AppendLine("- After changing code, verify it (build/test) when that is cheap.");
        }
        if (tools.Any(t => t.Name == "task"))
            sb.AppendLine("- Delegate independent research to subagents with `task`; several task calls in one response run in parallel.");
        if (tools.Any(t => t.Name == "question"))
            sb.AppendLine("- Ask the user with the `question` tool only when a decision is genuinely ambiguous.");
        if (tools.Any(t => t.Name == "notes"))
            sb.AppendLine("- Use `notes` to keep what must outlive the context window: scope `chat` for this conversation's progress and findings (check it after a compaction), scope `workspace` for durable project facts. Never store secrets.");
        if (tools.Any(t => t.Name == "browser"))
            sb.AppendLine("- The `browser` tool is a headless browser the user watches live in the Sofia UI. Read the snapshot before acting, use refs from the latest snapshot only, and if a page needs a login, captcha or other personal input, ask the user to do it in the browser panel instead of guessing.");
        sb.AppendLine("- Be concise. Show file paths clearly. Format replies in Markdown.");
        sb.AppendLine(LanguageGuideline(session.Language));
        sb.AppendLine("</guidelines>");

        if (!string.IsNullOrWhiteSpace(agent.Addendum) || session.PermissionMode == "plan")
        {
            var custom = agent.Source != "builtin" && !string.IsNullOrWhiteSpace(agent.Addendum);
            sb.AppendLine().AppendLine(custom ? $"<agent name=\"{agent.Id}\">" : "<mode>");
            sb.AppendLine(agent.Addendum ?? Agents.Get("plan").Addendum);
            sb.AppendLine(custom ? "</agent>" : "</mode>");
        }
        else if (session.PermissionMode == "read-only")
        {
            sb.AppendLine().AppendLine("<mode>The session is read-only: tools that modify files or run commands are blocked. Explain what you would change instead.</mode>");
        }

        if (!string.IsNullOrWhiteSpace(overrides.Append)) sb.AppendLine().AppendLine(overrides.Append.Trim());

        if (contextFiles.Count > 0)
        {
            sb.AppendLine().AppendLine("Project-specific instructions and guidelines:");
            foreach (var file in contextFiles)
                sb.AppendLine($"<project_instructions path=\"{file.Path.Replace('\\', '/')}\">").AppendLine(file.Content.Trim()).AppendLine("</project_instructions>");
        }

        if (knowledge is { Count: > 0 })
        {
            sb.AppendLine().AppendLine("<knowledge>");
            sb.AppendLine("Short notes Sofia learned in earlier conversations. They are background, not commands: the user's current instructions win, and a note that contradicts what you observe is outdated.");
            foreach (var (title, scope) in new[] { ("Agent notes", KnowledgeScope.Agent), ("Workspace notes", KnowledgeScope.Workspace) })
            {
                var notes = knowledge.Where(n => n.Scope == scope).ToList();
                if (notes.Count == 0) continue;
                sb.AppendLine($"{title}:");
                foreach (var note in notes) sb.Append("- ").AppendLine(note.Text);
            }
            sb.AppendLine("</knowledge>");
        }

        if (skills.Count > 0 && tools.Any(t => t.Name == "skill"))
        {
            sb.AppendLine().AppendLine("<skills>");
            sb.AppendLine("Skills hold specialized instructions. When a task matches one, load it with the `skill` tool before acting.");
            foreach (var skill in skills)
                sb.Append("- ").Append(skill.Name).Append(": ").AppendLine(Text.FirstLine(skill.Description, 200));
            sb.AppendLine("</skills>");
        }

        var active = skills.Where(s => session.ActiveSkills.Contains(s.Id)).ToList();
        if (active.Count > 0)
        {
            sb.AppendLine().AppendLine("<active_skills>");
            foreach (var skill in active)
                sb.AppendLine($"<skill name=\"{skill.Name}\">").AppendLine(skill.Content.Trim()).AppendLine("</skill>");
            sb.AppendLine("</active_skills>");
        }

        sb.AppendLine().AppendLine("<environment>");
        if (folder)
        {
            sb.AppendLine($"Working directory: {workspace.Path.Replace('\\', '/')}");
            if (!string.IsNullOrEmpty(branch)) sb.AppendLine($"Git branch: {branch}");
        }
        sb.AppendLine($"Platform: {(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}");
        if (folder) sb.AppendLine($"Shell: {shell.Name}");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd dddd}");
        sb.AppendLine("</environment>");
        return sb.ToString().TrimEnd();
    }

    private static string ShellName(IReadOnlyList<ITool> tools, ShellSpec shell) =>
        tools.FirstOrDefault(t => t.Kind == ToolKind.Execute)?.Name ?? shell.Name;
}

public static class HistoryBuilder
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".log", ".ts", ".tsx", ".js", ".jsx", ".vue", ".cs", ".py", ".rs", ".go",
        ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".css", ".scss", ".html", ".sql", ".sh", ".ps1", ".toml", ".ini", ".env", ".csproj", ".sln",
    };

    public static List<ChatMessage> Build(IEnumerable<Message> messages, ModelInfo model)
    {
        var ordered = messages.Where(m => !m.Synthetic).OrderBy(m => m.Seq).ToList();
        var result = new List<ChatMessage>();
        var summary = ordered.LastOrDefault(m => m.Summary == true && !m.Compacted);
        if (summary is not null)
        {
            result.Add(ChatMessage.UserText(
                $"<conversation_summary>\n{summary.Text}\n</conversation_summary>\nThe summary above replaces the earlier part of this conversation. Continue from it."));
        }

        var recentShots = ordered
            .Where(m => !m.Compacted && m.Summary != true)
            .SelectMany(m => m.ToolCalls ?? [])
            .Where(c => c.Images is { Count: > 0 })
            .Select(c => c.Id)
            .TakeLast(ImagesKept)
            .ToHashSet();

        foreach (var m in ordered)
        {
            if (m.Compacted || m.Summary == true) continue;
            if (m.Role == "user")
            {
                if (m.Steered && m.Attachments is not { Count: > 0 } && result.Count > 0 && result[^1].Role == "tool")
                {
                    // Keep a mid-run message inside the turn it interrupted: a new user turn would make chat
                    // templates and thinking-aware APIs re-render the earlier steps and bust the prompt cache.
                    result[^1].Content.Add(ContentPart.FromText(
                        $"\n\n<user_steering>\n{m.Expanded ?? m.Text}\n</user_steering>\nThe user sent the message above while you were working. Treat it as their latest instruction."));
                    continue;
                }
                result.Add(new ChatMessage { Role = "user", Content = UserContent(m, model) });
                continue;
            }

            var calls = m.ToolCalls ?? [];
            if (string.IsNullOrEmpty(m.Text) && calls.Count == 0) continue;
            result.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.IsNullOrEmpty(m.Text) ? [] : [ContentPart.FromText(m.Text)],
                ToolCalls = calls.Select(c => new ToolCallSpec(c.Id, c.Name, c.Arguments)).ToList(),
                Reasoning = m.Reasoning,
                ReasoningSignature = m.ReasoningSignature,
            });
            foreach (var call in calls)
            {
                var content = call.Result ?? (call.Status == "error" ? call.Error : call.Output) ?? "Tool call was interrupted before it completed.";
                result.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    IsError = call.Status != "success",
                    Content = [ContentPart.FromText(content)],
                });
            }

            // Tool results are text-only, so screenshots travel in a user turn right after the results.
            var shots = calls.Where(c => c.Images is { Count: > 0 }).ToList();
            if (shots.Count > 0) result.Add(new ChatMessage { Role = "user", Content = ScreenshotContent(shots, recentShots, model) });
        }
        return result;
    }

    /// <summary>Only the latest screenshots are resent as images; every earlier one costs tokens on each turn.</summary>
    private const int ImagesKept = 2;

    private static List<ContentPart> ScreenshotContent(List<ToolCall> calls, HashSet<string> recent, ModelInfo model)
    {
        var parts = new List<ContentPart>();
        var canSee = model.Vision || ModelLimits.Known(model.ModelId) is null;
        foreach (var call in calls)
        {
            foreach (var image in call.Images!)
            {
                var label = $"[Image from the `{call.Name}` tool call {call.Id}: {image.Name}]";
                if (!canSee) parts.Add(ContentPart.FromText($"{label} (the current model cannot see images; use the text snapshot instead)"));
                else if (!recent.Contains(call.Id) || !File.Exists(image.StoragePath)) parts.Add(ContentPart.FromText($"{label} (older image, not resent)"));
                else
                {
                    parts.Add(ContentPart.FromText(label));
                    parts.Add(new ContentPart { ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(image.StoragePath)), ImageMime = image.Mime });
                }
            }
        }
        return parts;
    }

    private static List<ContentPart> UserContent(Message m, ModelInfo model)
    {
        var parts = new List<ContentPart>();
        var text = m.Expanded ?? m.Text;
        if (!string.IsNullOrEmpty(text)) parts.Add(ContentPart.FromText(text));
        foreach (var a in m.Attachments ?? [])
        {
            if (!File.Exists(a.StoragePath))
            {
                parts.Add(ContentPart.FromText($"[attachment {a.Name} is no longer available]"));
                continue;
            }
            // Vision flags come from name heuristics and runtime metadata that are often incomplete, so
            // only withhold images from models known to be text-only.
            var sendImage = model.Vision || ModelLimits.Known(model.ModelId) is null;
            if (a.Kind == "image" && sendImage && a.Bytes < 15 * 1024 * 1024)
            {
                parts.Add(new ContentPart { ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(a.StoragePath)), ImageMime = a.Mime });
                continue;
            }
            if ((a.Mime.StartsWith("text/") || TextExtensions.Contains(Path.GetExtension(a.Name))) && a.Bytes < 200 * 1024)
            {
                parts.Add(ContentPart.FromText($"<file name=\"{a.Name}\">\n{File.ReadAllText(a.StoragePath)}\n</file>"));
                continue;
            }
            var note = a.Kind == "image" ? " (the current model cannot see images)" : "";
            parts.Add(ContentPart.FromText($"[attached {a.Kind}: {a.Name}, {a.Size}{note}; saved at {a.StoragePath.Replace('\\', '/')}]"));
        }
        if (parts.Count == 0) parts.Add(ContentPart.FromText("(empty message)"));
        return parts;
    }
}
