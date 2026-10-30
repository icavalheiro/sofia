using System.Text.Json.Nodes;
using Sofia.Server.Agent;
using Sofia.Server.Context;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Exposes the current date and time to the model. The system prompt only freezes the date, so a tool is the right
/// place to read the live clock (and convert it to a requested time zone).
/// </summary>
public class CurrentTimeTool : ITool
{
    public virtual string Name => "current_time";
    public string Description =>
        "Return the current date and time (local to the server by default, or in an optional time zone). " +
        "Use whenever the actual present moment matters: stamping records, computing ages, deadlines, expiry, scheduling, or answering 'what time is it'.";
    public ToolKind Kind => ToolKind.Read;
    public JsonObject Parameters => Schema.Object(
        ("timezone", Schema.String("Optional IANA or Windows time zone id (e.g. 'UTC', 'America/Sao_Paulo', 'Europe/Lisbon'). Defaults to the server's local time zone."), false),
        ("format", Schema.String("Optional .NET date/time format string. When given, returns only the formatted time."), false));

    public string Summarize(JsonObject args) => Args.Optional(args, "timezone") ?? "now";

    public Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var tzId = Args.Optional(args, "timezone", "timeZone", "tz");
        TimeZoneInfo tz;
        if (string.IsNullOrWhiteSpace(tzId))
        {
            tz = TimeZoneInfo.Local;
        }
        else
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(tzId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                return Task.FromResult(ToolResult.Fail($"Unknown time zone '{tzId}'. Use an IANA or Windows time zone id (e.g. 'UTC', 'America/Sao_Paulo')."));
            }
            catch (InvalidTimeZoneException)
            {
                return Task.FromResult(ToolResult.Fail($"Invalid time zone '{tzId}'."));
            }
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var format = Args.Optional(args, "format");
        if (!string.IsNullOrWhiteSpace(format))
        {
            try
            {
                return Task.FromResult(ToolResult.Ok(now.ToString(format)));
            }
            catch (FormatException)
            {
                return Task.FromResult(ToolResult.Fail($"Invalid date/time format string '{format}'."));
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(now.ToString("O"));
        sb.AppendLine($"Time zone: {tz.Id} ({tz.DisplayName})");
        sb.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        sb.Append($"Unix: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }
}

public sealed class TimeTool : CurrentTimeTool { public override string Name => "time"; }
public sealed class NowTool : CurrentTimeTool { public override string Name => "now"; }
public sealed class GetCurrentTimeTool : CurrentTimeTool { public override string Name => "get_current_time"; }
public sealed class DateTimeTool : CurrentTimeTool { public override string Name => "date_time"; }