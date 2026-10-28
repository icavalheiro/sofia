using System.Text.RegularExpressions;
using Sofia.Server.Context;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Agent;

/// <summary>
/// The built-in agents plus custom ones from Markdown files (`~/.sofia/agents`, `<workspace>/.agents/agents` and
/// `<workspace>/.sofia/agents`; other harnesses' folders are never read). Frontmatter: `name`, `description`, `mode`
/// (primary | subagent | all, default subagent), `tools` (comma list; omitted = the default set for the mode) and `mcp`
/// (true/false). The body is the agent's system-prompt addendum. Later roots win: project over global over built-in, and
/// `.sofia` over `.agents`, so a file can override a built-in.
/// </summary>
public sealed partial class AgentCatalog(SofiaOptions options)
{

    private static readonly Dictionary<string, string> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "read", ["write"] = "write", ["edit"] = "edit", ["multiedit"] = "edit",
        ["bash"] = Agents.ShellToolToken, ["shell"] = Agents.ShellToolToken, ["powershell"] = Agents.ShellToolToken, ["terminal"] = Agents.ShellToolToken,
        ["grep"] = "grep", ["find"] = "find", ["glob"] = "find", ["ls"] = "ls", ["list"] = "ls",
        ["web_fetch"] = "web_fetch", ["webfetch"] = "web_fetch", ["web_search"] = "web_search", ["websearch"] = "web_search",
        ["browser"] = "browser", ["playwright"] = "browser",
        ["question"] = "question", ["askuserquestion"] = "question", ["task"] = "task", ["agent"] = "task",
        ["processes"] = "processes", ["process"] = "processes", ["jobs"] = "processes", ["skill"] = "skill", ["sofia_info"] = "sofia_info", ["sofia"] = "sofia_info", ["notes"] = "notes", ["note"] = "notes",
        ["memory"] = "memory", ["mem"] = "memory",
    };

    public IEnumerable<string> GlobalRoots()
    {
        yield return options.AgentsDir;
    }

    /// <summary>The workspace's own agent folders, in override order (<c>.agents</c> then <c>.sofia</c>).</summary>
    public static IEnumerable<string> ProjectRoots(string directory) => ConfigPaths.ProjectRoots(directory, "agents");

    public List<AgentDefinition> List(string? directory)
    {
        var byId = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in Agents.All) byId[agent.Id] = agent;
        foreach (var root in GlobalRoots()) Scan(root, "global", byId);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            foreach (var root in ProjectRoots(directory)) Scan(root, "project", byId);
        return byId.Values.ToList();
    }

    public AgentDefinition? Find(string? id, string? directory) =>
        string.IsNullOrWhiteSpace(id) ? null : List(directory).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Unknown ids fall back to the default agent (a custom agent's file may have been removed).</summary>
    public AgentDefinition Get(string? id, string? directory) => Find(id, directory) ?? Agents.All[0];

    /// <summary>Agents the `task` tool and @mentions can hand work to.</summary>
    public List<AgentDefinition> Delegable(string? directory, bool readOnlyOnly = false) =>
        List(directory).Where(a => a.IsDelegable && (!readOnlyOnly || a.ReadOnly)).ToList();

    /// <summary>Delegable agents named as `@id` in a message, in order of appearance.</summary>
    public List<AgentDefinition> Mentions(string text, string? directory)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return [];
        var delegable = Delegable(directory);
        var found = new List<AgentDefinition>();
        foreach (Match match in MentionPattern().Matches(text))
        {
            var id = match.Groups[1].Value.TrimEnd('.', '-');
            var agent = delegable.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (agent is not null && !found.Contains(agent)) found.Add(agent);
        }
        return found;
    }

    [GeneratedRegex(@"(?<![\w@/.:-])@([A-Za-z0-9][\w.-]*)")]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][\w.-]*$")]
    private static partial Regex IdPattern();

    /// <summary>Asks the model to hand a request to subagents through the `task` tool.</summary>
    public static string DelegationHint(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        var names = string.Join(", ", list.Select(i => "@" + i));
        return $"The user mentioned the subagent{(list.Count > 1 ? "s" : "")} {names}. Delegate the relevant part of the request to " +
               $"{(list.Count > 1 ? "each one" : "it")} with the `task` tool (agent: {string.Join(" / ", list.Select(i => $"\"{i}\""))}), " +
               "passing every detail it needs, then use its report to answer.";
    }

    private static void Scan(string root, string source, Dictionary<string, AgentDefinition> into)
    {
        if (!Directory.Exists(root)) return;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (IOException)
        {
            return;
        }
        foreach (var file in files)
        {
            try
            {
                if (Parse(file, source) is { } agent) into[agent.Id] = agent;
            }
            catch (IOException)
            {
                // Unreadable agent files are skipped.
            }
        }
    }

    public static AgentDefinition? Parse(string file, string source)
    {
        var (fields, body) = Frontmatter.Parse(File.ReadAllText(file));
        var id = (fields.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(file)).Trim();
        if (!IdPattern().IsMatch(id)) return null;

        var declared = fields.GetValueOrDefault("mode")?.Trim().ToLowerInvariant();
        var mode = declared is "primary" or "all" ? declared : "subagent";
        var description = fields.GetValueOrDefault("description") ?? Text.FirstLine(body, 120);

        string[] tools;
        bool mcp;
        if (fields.TryGetValue("tools", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            tools = raw.Trim('[', ']').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => ToolAliases.GetValueOrDefault(t.Trim('"', '\'')))
                .Where(t => t is not null).Select(t => t!).Distinct().ToArray();
            mcp = fields.TryGetValue("mcp", out var m) && bool.TryParse(m, out var enabled) && enabled;
        }
        else
        {
            tools = mode == "subagent" ? Agents.Get("general").Tools : Agents.Get("build").Tools;
            mcp = !fields.TryGetValue("mcp", out var m) || !bool.TryParse(m, out var enabled) || enabled;
        }

        var prompt = body.Trim();
        if (mode == "subagent")
            prompt = (prompt + "\n\nYou are a subagent. Complete the task autonomously, then reply with a concise report of what you did and found. The user cannot answer questions.").Trim();
        return new AgentDefinition(id, id, description, mode, tools, mcp, prompt.Length > 0 ? prompt : null, source, file);
    }
}
