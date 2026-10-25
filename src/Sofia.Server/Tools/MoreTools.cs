using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sofia.Server.Agent;
using Sofia.Server.Context;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

public sealed class ShellTool(ShellSpec shell) : ITool
{
    private const int DefaultTimeoutSeconds = 120;

    public string Name => shell.Name == "PowerShell" ? "powershell" : "bash";
    public string Description =>
        $"Run a {shell.Name} command in the working directory and return stdout+stderr. Use for builds, tests, git and file operations. " +
        $"Non-interactive only; default timeout {DefaultTimeoutSeconds}s (max 600).";
    public ToolKind Kind => ToolKind.Execute;
    public JsonObject Parameters => Schema.Object(
        ("command", Schema.String($"The {shell.Name} command to run."), true),
        ("timeout", Schema.Integer("Timeout in seconds."), false));

    public string Summarize(JsonObject args) => Text.FirstLine(Args.Optional(args, "command", "cmd") ?? "", 160);

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var command = Args.Required(args, "command", "cmd");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(Args.Int(args, "timeout") ?? DefaultTimeoutSeconds, 1, 600));
        var result = await ProcessRunner.RunShellAsync(shell, command, ctx.Cwd, timeout,
            chunk => ctx.Host.ReportProgress(ctx.Call, chunk), ctx.Config.MaxToolOutput, ct);
        var output = result.Output.Length == 0 ? "(no output)" : result.Output;
        if (result.TimedOut) return ToolResult.Fail($"{output}\n\n[Command timed out after {timeout.TotalSeconds:0}s and was killed.]");
        if (result.Cancelled) return ToolResult.Fail($"{output}\n\n[Command aborted.]");
        if (result.ExitCode is { } code && code != 0) return ToolResult.Fail($"{output}\n\n[Exit code {code}]");
        return ToolResult.Ok(output);
    }
}

public sealed partial class WebFetchTool(IHttpClientFactory httpFactory) : ITool
{
    public string Name => "web_fetch";
    public string Description => "Fetch a URL (http/https) and return its content as plain text. HTML is converted to readable text.";
    public ToolKind Kind => ToolKind.Network;
    public JsonObject Parameters => Schema.Object(("url", Schema.String("Absolute http(s) URL."), true));

    public string Summarize(JsonObject args) => Args.Optional(args, "url") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var url = Args.Required(args, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ToolResult.Fail("Only absolute http(s) URLs are supported.");
        var http = httpFactory.CreateClient("fetch");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(uri, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var text = type.Contains("html") || body.TrimStart().StartsWith('<') ? HtmlToText(body) : body;
        var header = $"{(int)response.StatusCode} {response.ReasonPhrase} · {type}\n\n";
        var output = header + Text.Truncate(text, ctx.Config.MaxToolOutput);
        return response.IsSuccessStatusCode ? ToolResult.Ok(output) : ToolResult.Fail(output);
    }

    internal static string HtmlToText(string html)
    {
        var text = NoiseRegex().Replace(html, " ");
        text = BlockRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = SpacesRegex().Replace(text, " ");
        text = BlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|head)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();
    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpacesRegex();
    [GeneratedRegex(@"\s*\n\s*\n\s*")]
    private static partial Regex BlankLinesRegex();
}

public sealed class QuestionTool : ITool
{
    public string Name => "question";
    public string Description =>
        "Ask the user one or more multiple-choice questions and wait for the answers. Use only when a decision is genuinely needed; offer 2-6 short options.";
    public ToolKind Kind => ToolKind.Interactive;
    public JsonObject Parameters => Schema.Object(("questions", Schema.Array("Questions to ask.", Schema.Object(
        ("question", Schema.String("The question."), true),
        ("options", Schema.Array("Answer options.", new JsonObject { ["type"] = "string" }), true),
        ("multiple", Schema.Boolean("Allow picking several options."), false))), true));

    public string Summarize(JsonObject args) =>
        args["questions"] is JsonArray q && q.Count > 0 ? Text.FirstLine(q[0]?["question"]?.ToString() ?? "", 120) : "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var list = new List<QuestionRequest>();
        if (args["questions"] is JsonArray questions)
        {
            foreach (var q in questions)
            {
                if (q is not JsonObject obj) continue;
                var prompt = Args.Optional(obj, "question", "prompt") ?? "";
                var options = (obj["options"] as JsonArray)?.Select(o => o?.ToString() ?? "").Where(o => o.Length > 0).ToList() ?? [];
                if (prompt.Length > 0) list.Add(new QuestionRequest(prompt, options, Args.Bool(obj, "multiple")));
            }
        }
        else if (Args.Optional(args, "question", "prompt") is { } single)
        {
            var options = (args["options"] as JsonArray)?.Select(o => o?.ToString() ?? "").ToList() ?? [];
            list.Add(new QuestionRequest(single, options, Args.Bool(args, "multiple")));
        }
        if (list.Count == 0) return ToolResult.Fail("Provide at least one question.");

        var answers = await ctx.Host.AskAsync(ctx, list, ct);
        var lines = list.Select((q, i) => $"Q: {q.Prompt}\nA: {(i < answers.Count && !string.IsNullOrEmpty(answers[i]) ? answers[i] : "(no answer)")}");
        return ToolResult.Ok(string.Join("\n\n", lines));
    }
}

public sealed class TaskTool(IReadOnlyList<AgentDefinition> targets) : ITool
{
    public string Name => "task";
    public string Description =>
        "Delegate a self-contained task to a subagent with its own context. Several `task` calls in one response run in parallel. Agents: " +
        string.Join("; ", targets.Select(a => $"`{a.Id}` ({Text.FirstLine(a.Description, 120).TrimEnd('.')})")) + ". " +
        "The subagent only sees your prompt, so include every detail it needs. Returns the subagent's final report.";
    public ToolKind Kind => ToolKind.Agent;
    public JsonObject Parameters => Schema.Object(
        ("description", Schema.String("Short (3-6 words) label for the task."), true),
        ("prompt", Schema.String("Complete instructions for the subagent."), true),
        ("agent", Schema.Enum("Subagent type.", targets.Select(a => a.Id).ToArray()), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "description") ?? "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var description = Args.Optional(args, "description") ?? "Subtask";
        var prompt = Args.Required(args, "prompt");
        var requested = Args.Optional(args, "agent", "subagent_type");
        var agent = targets.FirstOrDefault(a => string.Equals(a.Id, requested, StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(a => a.Id == "explore") ?? targets[0];
        var report = await ctx.Host.RunSubagentAsync(ctx, agent.Id, description, prompt, ct);
        return ToolResult.Ok(report);
    }
}

public sealed class SkillTool(SkillService skills) : ITool
{
    public string Name => "skill";
    public string Description => "Load the full instructions of a skill listed in <skills> by name.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(("name", Schema.String("Skill name."), true));

    public string Summarize(JsonObject args) => Args.Optional(args, "name") ?? "";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var name = Args.Required(args, "name", "skill");
        var skill = skills.List(ctx.Workspace.IsFolder ? ctx.Workspace.Path : null)
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || s.Id == name);
        if (skill is null) return Task.FromResult(ToolResult.Fail($"Unknown skill '{name}'."));
        return Task.FromResult(ToolResult.Ok($"<skill name=\"{skill.Name}\" dir=\"{Path.GetDirectoryName(skill.Path)}\">\n{skill.Content}\n</skill>"));
    }
}
