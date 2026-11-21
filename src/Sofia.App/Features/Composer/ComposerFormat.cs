using System.Globalization;

namespace Sofia.App.Features.Composer;

/// <summary>
/// Number formatting of the composer hint row, copied from the web helpers (<c>formatBytes</c> in
/// <c>useHarness.ts</c>, <c>compactTokens</c>/<c>formatDuration</c> in <c>ChatComposer.vue</c> and
/// <c>hitPercent</c> in <c>cacheHealth.ts</c>). Pure functions so the parity rules are unit-tested instead of being
/// baked into the view.
/// </summary>
public static class ComposerFormat
{
    /// <summary>Attachment size: bytes below 1 KiB stay raw, then one decimal in the largest fitting unit.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB"];
        var value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Token readout: 999, 21.5K, 125K, 1.2M (the web's <c>compactTokens</c>).</summary>
    public static string Tokens(long tokens)
    {
        if (tokens < 1000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1_000_000)
        {
            var value = tokens / 1000d;
            return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + "K";
        }
        return (tokens / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>Elapsed time in the largest unit that has started: 12.4s, 2.25m, 1.05h.</summary>
    public static string Duration(long milliseconds)
    {
        var hours = milliseconds / 3_600_000d;
        if (hours >= 1) return hours.ToString("0.00", CultureInfo.InvariantCulture) + "h";
        var minutes = milliseconds / 60_000d;
        if (minutes >= 1) return minutes.ToString("0.00", CultureInfo.InvariantCulture) + "m";
        return (milliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// Cache-hit percentage of one request. A 100% hit is impossible: whole numbers below 99, then up to three
    /// decimals capped at 99.999 (the web's <c>hitPercent</c>).
    /// </summary>
    public static double HitPercent(long cached, long input)
    {
        if (input <= 0) return 0;
        var raw = cached * 100d / input;
        if (raw < 99) return Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Min(99.999, Math.Floor(raw * 1000) / 1000d);
    }

    /// <summary>Label of the hit rate: whole percentages, three decimals only for the impossible-to-reach top.</summary>
    public static string HitPercentLabel(long cached, long input)
    {
        var value = HitPercent(cached, input);
        return Math.Abs(value % 1) < 0.0005
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Attachment kind from its MIME type (the web's <c>kindFromMime</c>).</summary>
    public static string KindOf(string? mime)
    {
        var value = mime ?? "";
        if (value.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (value.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        if (value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        return "file";
    }

    /// <summary>Lucide glyph of an attachment kind (the web's <c>iconForKind</c>).</summary>
    public static string IconForKind(string? kind) => kind switch
    {
        "image" => "image",
        "video" => "video",
        "audio" => "music",
        _ => "file",
    };
}

/// <summary>One slice of the context meter (web <c>contextSegments</c>).</summary>
/// <param name="Key">system, tools, messages, or total when the server sent no split.</param>
/// <param name="Color">The web's literal: <c>#94949B</c>, the accent, <c>#5A9BD8</c>.</param>
public sealed record ContextSegment(string Key, string Label, long Value, string Color, double Width);

/// <summary>Percent label plus the segments of the segmented context bar.</summary>
public sealed record ContextMeterView(int Percent, bool HasSplit, IReadOnlyList<ContextSegment> Segments);

/// <summary>
/// The composer's context meter, ported from <c>ChatComposer.vue</c>: a straight segmented bar over the model window
/// (grey = system prompt, accent = tool schemas, blue = messages) with the hover legend. Widths are proportional to
/// the window; when usage exceeds the limit (right before a compact) everything scales down so the fill never
/// overflows the track.
/// </summary>
public static class ContextMeter
{
    public const string SystemColor = "#94949B";
    public const string ToolsColor = "primary";
    public const string MessagesColor = "#5A9BD8";

    public static ContextMeterView? Build(long used, long limit, long system, long tools)
    {
        if (limit <= 0) return null;
        var hasSplit = system > 0 || tools > 0;
        var messages = Math.Max(0, used - system - tools);
        var percent = Math.Max(used > 0 ? 1 : 0, (int)Math.Round(used * 100d / limit));

        var raw = !hasSplit
            ? new List<(string Key, string Label, long Value, string Color)>
            {
                ("total", "composer.ctxTotal", used, ToolsColor),
            }
            : new List<(string Key, string Label, long Value, string Color)>
            {
                ("system", "composer.ctxSystem", system, SystemColor),
                ("tools", "composer.ctxTools", tools, ToolsColor),
                ("messages", "composer.ctxMessages", messages, MessagesColor),
            };

        var sum = raw.Sum(s => Math.Max(0, s.Value)) * 100d / limit;
        var scale = sum > 100 ? 100 / sum : 1;
        var segments = raw
            .Select(s => new ContextSegment(
                s.Key,
                s.Label,
                s.Value,
                s.Color,
                Math.Max(0, s.Value) / (double)limit * 100 * scale))
            .ToList();
        return new ContextMeterView(percent, hasSplit, segments);
    }
}
