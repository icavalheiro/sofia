using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>Start, list, read and kill background processes (dev servers, watchers) that outlive a single command.</summary>
public sealed class ProcessesTool(ProcessManager processes, ShellSpec shell) : ITool
{
    public string Name => "processes";
    public string Description =>
        $"Manage long-running background processes (dev servers, watchers) that would block a normal {shell.Name} command. " +
        "Actions: start (command) runs it detached and returns an id; list shows the ones started in this conversation; " +
        "output (id, optional tail lines) reads recent output; kill (id) stops it and its children.";
    public ToolKind Kind => ToolKind.Execute;
    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("What to do.", "start", "list", "output", "kill"), true),
        ("command", Schema.String("Command to run (start)."), false),
        ("id", Schema.String("Process id such as bg_1 (output, kill)."), false),
        ("tail", Schema.Integer("Lines of output to return (output, default 100)."), false));

    public string Summarize(JsonObject args) =>
        $"{Args.Optional(args, "action")} {Text.FirstLine(Args.Optional(args, "command", "id") ?? "", 120)}".Trim();

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        switch (Args.Required(args, "action").ToLowerInvariant())
        {
            case "start":
                var info = processes.Start(shell, Args.Required(args, "command", "cmd"), ctx.Cwd, ctx.Session.Id);
                return Task.FromResult(ToolResult.Ok($"Started {info.Id} (pid {info.Pid}). Use output to check it and kill to stop it."));
            case "list":
                var list = processes.List(ctx.Session.Id);
                if (list.Count == 0) return Task.FromResult(ToolResult.Ok("No background processes."));
                var sb = new StringBuilder();
                foreach (var p in list)
                    sb.AppendLine($"{p.Id}  pid {p.Pid}  {p.Status}{(p.ExitCode is { } c ? $" (exit {c})" : "")}  {Text.FirstLine(p.Command, 120)}");
                return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
            case "output":
                var (o, text) = processes.Output(Args.Required(args, "id"), Args.Int(args, "tail") ?? 100);
                var body = text.Length == 0 ? "(no output)" : Text.Truncate(text, ctx.Config.MaxToolOutput);
                return Task.FromResult(ToolResult.Ok($"[{o.Id} {o.Status}]\n{body}"));
            case "kill":
                var k = processes.Kill(Args.Required(args, "id"));
                return Task.FromResult(ToolResult.Ok($"{k.Id} {k.Status}."));
            default:
                return Task.FromResult(ToolResult.Fail("Unknown action. Use start, list, output or kill."));
        }
    }
}
