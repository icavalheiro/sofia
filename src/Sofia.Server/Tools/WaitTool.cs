using System.Text.Json.Nodes;
using Sofia.Server.Context;

namespace Sofia.Server.Tools;

/// <summary>
/// Pauses the agent for a short, bounded amount of time. Useful for waiting on a process, a deploy, or a rate limit
/// before retrying. The wait is cancellable through the run's cancellation token.
/// </summary>
public sealed class WaitTool : ITool
{
    private const int MaxSeconds = 600;

    public string Name => "wait";
    public string Description =>
        "Wait (sleep) for a number of seconds before continuing. Use to pause between retries, wait for a process or " +
        "deploy to settle, or throttle requests. Cancellable.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("seconds", Schema.Integer($"Number of seconds to wait (1-{MaxSeconds})."), true));

    public string Summarize(JsonObject args) => Args.Int(args, "seconds") is int s ? $"{s}s" : "";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var raw = Args.Optional(args, "seconds", "duration", "delay", "sec", "secs");
        if (raw is null) return ToolResult.Fail("Missing required argument 'seconds'.");

        int seconds;
        if (!int.TryParse(raw, out seconds))
        {
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return ToolResult.Fail($"Invalid number of seconds '{raw}'.");
            seconds = (int)Math.Round(d);
        }

        if (seconds < 0) return ToolResult.Fail("seconds must be non-negative.");
        if (seconds == 0) return ToolResult.Ok("Nothing to wait for (0 seconds).");
        seconds = Math.Clamp(seconds, 1, MaxSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Wait cancelled.");
        }

        return ToolResult.Ok($"Waited {seconds} second(s).");
    }
}