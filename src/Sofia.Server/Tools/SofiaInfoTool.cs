using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Agent;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;

namespace Sofia.Server.Tools;

/// <summary>
/// Read-only guide to Sofia's own configuration: where global and project skills, commands, agents, MCP servers,
/// context files and settings live, their formats, what is installed now and how to change it. It lets an agent
/// help the user edit Sofia's setup with the normal file/shell tools. Secrets (keys, tokens, env values) are never shown.
/// </summary>
public sealed class SofiaInfoTool(
    SofiaOptions options, SofiaDb db, McpManager mcp, SkillService skills, CommandService commands, AgentCatalog agents, Func<string> baseUrl) : ITool
{
    private static readonly string[] Topics = ["overview", "skills", "commands", "agents", "mcp", "providers", "config", "context"];

    public string Name => "sofia_info";
    public string Description =>
        "Explain how Sofia itself is configured: where global and project settings, skills, commands, agents, MCP servers and context files live, " +
        "their file formats, what is installed right now and how to change it. Call it before helping the user edit Sofia's own configuration. Read-only.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("topic", Schema.Enum($"What to explain (default overview). One of: {string.Join(", ", Topics)}.", Topics), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "topic") ?? "overview";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var topic = (Args.Optional(args, "topic") ?? "overview").Trim().ToLowerInvariant();
        var project = ctx.Workspace.IsFolder ? ctx.Workspace.Path : null;
        var text = topic switch
        {
            "overview" => Overview(project),
            "skills" => Skills(project),
            "commands" => Commands(project),
            "agents" => AgentsTopic(project),
            "mcp" => McpTopic(),
            "providers" => Providers(),
            "config" => Config(),
            "context" => Context(project),
            _ => null,
        };
        return Task.FromResult(text is null
            ? ToolResult.Fail($"Unknown topic '{topic}'. Use one of: {string.Join(", ", Topics)}.")
            : ToolResult.Ok(text.TrimEnd()));
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Mark(string path) => $"{path}{(File.Exists(path) || Directory.Exists(path) ? "" : " (not created yet)")}";

    private static string Api(string baseUrl) =>
        $"The HTTP API is at {baseUrl}/api (reference: docs/server_api.md in the Sofia repository). The server trusts requests from this machine, so no token is needed; " +
        "call it with the shell tool (curl, or Invoke-RestMethod on PowerShell).";

    private string Overview(string? project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sofia configuration");
        sb.AppendLine($"Data directory: {options.DataDir} (the SOFIA_HOME environment variable overrides it; default ~/.sofia)");
        sb.AppendLine($"Database: {options.DbPath} — LiteDB. Holds settings, providers, MCP servers, conversations and knowledge notes. Never edit it directly; use the HTTP API.");
        sb.AppendLine(Api(baseUrl()));
        sb.AppendLine();
        sb.AppendLine("## Files you can create or edit (global)");
        sb.AppendLine($"- Skills:   {Mark(options.SkillsDir)}/<name>/SKILL.md   (also ~/.agents/skills, ~/.claude/skills)");
        sb.AppendLine($"- Commands: {Mark(options.CommandsDir)}/<name>.md   (also {options.PromptsDir}, ~/.claude/commands, ~/.config/opencode/command)");
        sb.AppendLine($"- Agents:   {Mark(options.AgentsDir)}/<name>.md   (also ~/.claude/agents, ~/.config/opencode/agent)");
        sb.AppendLine($"- Instructions for every conversation: {Mark(options.GlobalAgentsFile)}");
        sb.AppendLine($"- System prompt: {Mark(Path.Combine(options.DataDir, "SYSTEM.md"))} (replaces the preamble), {Mark(Path.Combine(options.DataDir, "APPEND_SYSTEM.md"))} (appends to it)");
        sb.AppendLine();
        sb.AppendLine("## Stored in the database (change through the API)");
        sb.AppendLine("- MCP servers (topic: mcp), model providers and API keys (topic: providers), server settings (topic: config).");
        sb.AppendLine();
        sb.AppendLine("## Per project");
        if (project is null)
            sb.AppendLine("This conversation has no project folder. A project can add `.sofia/skills`, `.sofia/commands`, `.sofia/agents`, `.sofia/SYSTEM.md` and `AGENTS.md`; they override global entries with the same name.");
        else
            sb.AppendLine($"Current project: {project}. It can add `.sofia/skills`, `.sofia/commands`, `.sofia/agents`, `.sofia/SYSTEM.md` and `AGENTS.md`; they override global entries with the same name.");
        sb.AppendLine();
        sb.AppendLine($"Call sofia_info with topic = {string.Join(" | ", Topics.Skip(1))} for formats and what is installed. Skills, commands, agents and context files are re-read on every message, so edits apply without a restart.");
        return sb.ToString();
    }

    private string Skills(string? project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Skills");
        sb.AppendLine("Reusable instructions. Only name and description go into the system prompt; the `skill` tool loads the body on demand.");
        sb.AppendLine($"Global folders (all are read): {Mark(options.SkillsDir)} (preferred), {Path.Combine(Home, ".agents", "skills")}, {Path.Combine(Home, ".claude", "skills")}");
        sb.AppendLine("Project folders: <project>/.sofia/skills, .agents/skills, .claude/skills, .opencode/skills — a project skill overrides a global one of the same name.");
        sb.AppendLine("Layout: <folder>/<skill-name>/SKILL.md (supporting files may sit next to it).");
        sb.AppendLine();
        sb.AppendLine("SKILL.md:\n---\nname: my-skill\ndescription: One sentence saying when to use it.\n---\nMarkdown instructions…");
        sb.AppendLine();
        var found = skills.List(project);
        sb.AppendLine($"Installed now ({found.Count}):");
        foreach (var s in found) sb.AppendLine($"- {s.Name} [{s.Source}] {s.Path} — {Text.FirstLine(s.Description, 100)}");
        return sb.ToString();
    }

    private string Commands(string? project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Custom commands (/name)");
        sb.AppendLine("Markdown prompt templates the user runs by typing /name [arguments] in the chat (opencode-style).");
        sb.AppendLine($"Global folders: {Mark(options.CommandsDir)} (preferred), {options.PromptsDir}, {Path.Combine(Home, ".claude", "commands")}, {Path.Combine(Home, ".config", "opencode", "command")}");
        sb.AppendLine("Project folders: <project>/.sofia/commands, .sofia/prompts, .claude/commands, .opencode/command — a project command overrides a global one of the same name.");
        sb.AppendLine("The file name is the command name; sub-folders become part of it (git/pr.md → /git/pr).");
        sb.AppendLine();
        sb.AppendLine("Format:\n---\ndescription: Shown in the / menu\nagent: plan        # optional: agent that runs it (a primary agent takes over the session, a subagent gets the work delegated)\nmodel: provider/model   # optional: switches the session to this model\nsubtask: true      # optional: delegate to a subagent instead of the main context\n---\nTemplate text.");
        sb.AppendLine();
        sb.AppendLine("Template placeholders: $ARGUMENTS (all arguments), $1…$9 (single arguments), !`shell command` (replaced by its output; only runs without asking in full-access sessions, otherwise it stays as text), @path/to/file (the file inside the project is appended to the prompt).");
        sb.AppendLine("Built-in commands: /compact, /init.");
        sb.AppendLine();
        var found = commands.List(project).Where(c => c.Source != "builtin").ToList();
        sb.AppendLine($"Installed now ({found.Count}):");
        foreach (var c in found)
            sb.AppendLine($"- /{c.Name} [{c.Source}] {c.Path}{(c.Agent is null ? "" : $" agent={c.Agent}")}{(c.Subtask ? " subtask" : "")} — {Text.FirstLine(c.Description, 100)}");
        return sb.ToString();
    }

    private string AgentsTopic(string? project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Agents");
        sb.AppendLine("Custom agents are Markdown files: the body is the agent's system-prompt addendum. The user calls a subagent by typing @name in the chat; the `task` tool can delegate to it too. Primary agents are picked in the composer's agent dropdown.");
        sb.AppendLine($"Global folders: {Mark(options.AgentsDir)} (preferred), {Path.Combine(Home, ".claude", "agents")}, {Path.Combine(Home, ".config", "opencode", "agent")}");
        sb.AppendLine("Project folders: <project>/.sofia/agents, .claude/agents, .opencode/agent — a project agent overrides a global one (or a built-in) with the same name.");
        sb.AppendLine("The name is the file name (letters, digits, '.', '_', '-'), or the `name` field.");
        sb.AppendLine();
        sb.AppendLine("Format:\n---\ndescription: When to use this agent\nmode: subagent     # subagent (default) | primary | all\ntools: read, grep, find, ls, bash, write, edit, web_fetch, web_search, skill, task, question, notes, sofia_info\nmcp: false         # include MCP tools (default: only when `tools` is omitted)\n---\nYou are … (instructions)");
        sb.AppendLine("When `tools` is omitted the agent gets the default set for its mode. Subagents cannot ask the user questions or spawn further subagents.");
        sb.AppendLine();
        var found = agents.List(project);
        sb.AppendLine($"Available now ({found.Count}):");
        foreach (var a in found)
            sb.AppendLine($"- {a.Id} [{a.Mode}, {a.Source}]{(a.Path is null ? "" : " " + a.Path)} — {Text.FirstLine(a.Description, 100)}");
        return sb.ToString();
    }

    private string McpTopic()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MCP servers");
        sb.AppendLine("Global (shared by every workspace) and stored in the database, not in a file. Their tools appear to agents as `server__tool`. Enabled servers reconnect when Sofia starts.");
        sb.AppendLine(Api(baseUrl()));
        sb.AppendLine("- GET    /api/mcp                    list servers");
        sb.AppendLine("- POST   /api/mcp                    add and connect: { \"name\", \"transport\": \"command\"|\"web\", \"command\"?, \"url\"?, \"token\"?, \"env\"?: {KEY: value} }");
        sb.AppendLine("- POST   /api/mcp/{id}/connect | /disconnect");
        sb.AppendLine("- DELETE /api/mcp/{id}");
        sb.AppendLine("`command` is the full command line of a stdio server (for example `npx -y @modelcontextprotocol/server-filesystem C:/data`); `url` is the endpoint of an HTTP server.");
        sb.AppendLine("Never print the user's tokens or env values back; ask for them only when needed.");
        sb.AppendLine();
        var list = mcp.List();
        sb.AppendLine($"Configured now ({list.Count}):");
        foreach (var s in list)
        {
            var target = s.Transport == "web" ? Redact(s.Url) : Executable(s.Command);
            sb.AppendLine($"- {s.Name} (id {s.Id}) {s.Transport} {target} — {(s.Enabled ? "enabled" : "disabled")}, {s.Status}, {s.Tools} tools{(s.Error is null ? "" : $", error: {Text.FirstLine(s.Error, 120)}")}");
        }
        return sb.ToString();
    }

    private string Providers()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Model providers");
        sb.AppendLine("Stored in the database. Local runtimes (Unsloth, Ollama, LM Studio, llama.cpp, vLLM) are discovered automatically; cloud providers turn on from environment keys (OPENAI_API_KEY, ANTHROPIC_API_KEY, OPENCODE_API_KEY, QWENCLOUD_API_KEY, COPILOT_GITHUB_TOKEN, …) or keys saved through the API.");
        sb.AppendLine(Api(baseUrl()));
        sb.AppendLine("- GET /api/providers, GET /api/providers/presets, GET /api/models");
        sb.AppendLine("- POST /api/providers { \"name\", \"type\": \"openai\"|\"anthropic\", \"baseUrl\", \"apiKey\"?, \"models\"?: [..], \"presetId\"? }");
        sb.AppendLine("- PATCH /api/providers/{id} { \"enabled\"?, \"apiKey\"?, \"baseUrl\"?, \"name\"? }; DELETE /api/providers/{id}; POST /api/providers/refresh");
        sb.AppendLine("- POST /api/models/enabled { \"ids\": [..], \"enabled\" } shows or hides models");
        sb.AppendLine("API keys are never shown to you; do not ask the user to paste one into the chat unless they want you to save it through the API.");
        sb.AppendLine();
        var list = db.Providers.FindAll().OrderBy(p => p.Name).ToList();
        sb.AppendLine($"Configured now ({list.Count}):");
        foreach (var p in list)
            sb.AppendLine($"- {p.Name} (id {p.Id}) {p.Type}/{p.Kind} {Redact(p.BaseUrl)} — {(p.Enabled ? "enabled" : "disabled")}, key: {(string.IsNullOrEmpty(p.ResolveKey()) ? "none" : "set")}");
        return sb.ToString();
    }

    private string Config()
    {
        var c = db.GetConfig();
        var sb = new StringBuilder();
        sb.AppendLine("# Server settings");
        sb.AppendLine("Global, stored in the database. Read with GET /api/config, change with PATCH /api/config and a partial body.");
        sb.AppendLine(Api(baseUrl()));
        sb.AppendLine();
        sb.AppendLine("Current values:");
        sb.AppendLine($"- defaultModel: \"{c.DefaultModel}\" ('' = first connected model)");
        sb.AppendLine($"- shell: \"{c.Shell}\" ('' = pwsh/powershell on Windows, bash elsewhere)");
        sb.AppendLine($"- autoCompact: {Json(c.AutoCompact)}; compactThreshold: {c.CompactThreshold} (fraction of the context window, 0.3–0.98)");
        sb.AppendLine($"- discovery: {Json(c.Discovery)} (probe local runtimes)");
        sb.AppendLine($"- maxToolOutput: {c.MaxToolOutput}");
        sb.AppendLine($"- keepScreenAwake: {Json(c.KeepScreenAwake)}");
        sb.AppendLine($"- knowledge: {Json(c.Knowledge)} (learn short notes from finished conversations; each note must be justified as useful or it is dropped)");
        sb.AppendLine($"- searchProvider: \"{c.SearchProvider}\" (duckduckgo | searxng); searxngUrl: \"{Redact(c.SearxngUrl)}\"");
        sb.AppendLine($"- disabled models: {c.DisabledModels.Count}");
        return sb.ToString();
    }

    private string Context(string? project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Context files");
        sb.AppendLine($"- Global instructions added to every conversation: {Mark(options.GlobalAgentsFile)}");
        sb.AppendLine("- Project instructions: AGENTS.md (or CLAUDE.md) in the project folder and in every parent folder; they follow the global file.");
        sb.AppendLine($"- {Mark(Path.Combine(options.DataDir, "SYSTEM.md"))} replaces the system-prompt preamble; {Mark(Path.Combine(options.DataDir, "APPEND_SYSTEM.md"))} appends to it. A project can have its own in <project>/.sofia/.");
        if (project is not null) sb.AppendLine($"- Current project: {project}");
        sb.AppendLine("All of them are plain Markdown, re-read on every message. Keep them short: they cost context on every request.");
        return sb.ToString();
    }

    private static string Json(bool value) => value ? "true" : "false";

    /// <summary>Command lines and URLs can carry credentials: show only the executable.</summary>
    private static string Executable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return "(no command)";
        var quoted = trimmed[0] is '"' or '\'';
        var end = quoted ? trimmed.IndexOf(trimmed[0], 1) + 1 : trimmed.IndexOfAny([' ', '\t']);
        if (end <= 0) return quoted ? "(command)" : trimmed;
        var args = trimmed[end..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        return args == 0 ? trimmed[..end] : $"{trimmed[..end]} (+{args} args)";
    }

    private static string Redact(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(custom)";
        return $"{uri.Scheme}://{uri.Authority}{(uri.AbsolutePath == "/" ? "" : uri.AbsolutePath)}";
    }
}
