using System.Text;

namespace Sofia.Server.Browser;

/// <summary>
/// One console entry of a conversation's browser window: a console message, an uncaught exception or a crash.
/// <see cref="Seq"/> is monotonic per conversation, which is how the tool reports only what was logged since the
/// previous result instead of repeating the same error in every call.
/// </summary>
public sealed record BrowserConsoleEntry(long Seq, string Level, string Text, string? Location, DateTimeOffset At)
{
    public bool IsError => Level is "error" or "pageerror";
}

/// <summary>
/// The console of a conversation's browser window: console messages plus uncaught page errors and crashes, kept in a
/// bounded buffer so the agent reads what the page logged through the tool instead of injecting listeners with
/// JavaScript (which a navigation throws away). Errors are evicted last, so log spam can never hide them.
/// </summary>
public sealed class BrowserConsole
{
    /// <summary>Entries kept per conversation; when the buffer is full the oldest non-error entry makes room.</summary>
    public const int Capacity = 300;

    /// <summary>An error can carry a stack; one entry still has to stay readable inside a tool result.</summary>
    public const int MaxText = 600;

    /// <summary>Entries returned by one <see cref="Describe"/> call.</summary>
    public const int MaxLimit = 200;

    private readonly List<BrowserConsoleEntry> _entries = [];
    private readonly object _gate = new();
    private long _seq;

    /// <summary>Sequence number of the last recorded entry (0 when nothing was logged).</summary>
    public long Cursor
    {
        get { lock (_gate) return _seq; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Records a console message; <paramref name="level"/> is the browser's message type.</summary>
    public void Add(string level, string text, string? location = null) =>
        Record(Normalize(level), text, location);

    /// <summary>Records an uncaught exception (Playwright's <c>PageError</c>) or a page crash.</summary>
    public void AddError(string text, string? location = null) =>
        Record("pageerror", text, location);

    private void Record(string level, string text, string? location)
    {
        var flat = OneLine(text);
        if (flat.Length == 0) flat = "(empty message)";
        var where = string.IsNullOrWhiteSpace(location) ? null : OneLine(location, 300);
        lock (_gate)
        {
            if (_entries.Count >= Capacity) _entries.RemoveAt(OldestDroppable());
            _entries.Add(new BrowserConsoleEntry(++_seq, level, flat, where, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Index of the entry a full buffer drops: the oldest one that is not an error.</summary>
    private int OldestDroppable()
    {
        for (var i = 0; i < _entries.Count; i++)
            if (!_entries[i].IsError) return i;
        return 0;
    }

    /// <summary>
    /// The retained log, oldest first, filtered by a minimum severity and capped to the most recent
    /// <paramref name="limit"/> entries. <paramref name="level"/> is error, warning, info or all (null = all).
    /// Advances <paramref name="cursor"/> atomically with the snapshot, so nothing is reported twice and nothing
    /// logged after this read is swallowed by a later summary.
    /// </summary>
    public string Describe(string? level, int limit, ref long cursor)
    {
        var threshold = Threshold(level);
        limit = Math.Clamp(limit, 1, MaxLimit);
        List<BrowserConsoleEntry> kept;
        lock (_gate)
        {
            kept = [.. _entries];
            cursor = _seq;
        }

        var errors = kept.Count(entry => entry.IsError);
        var warnings = kept.Count(entry => entry.Level == "warning");
        var matching = kept.Where(entry => Severity(entry.Level) >= threshold).ToList();
        var shown = matching.Count > limit ? matching[^limit..] : matching;

        var sb = new StringBuilder();
        sb.Append($"Console: {kept.Count} message(s) retained ({errors} error(s), {warnings} warning(s))");
        if (threshold > 0) sb.Append($", filtered to {Name(threshold)} and above");
        sb.AppendLine($"; showing {shown.Count} of {matching.Count} matching, oldest first.");
        if (kept.Count == 0)
        {
            sb.Append("The page has not logged anything since this window was opened.");
            return sb.ToString();
        }
        if (matching.Count == 0)
        {
            sb.Append($"No message at or above '{Name(threshold)}' was logged. Use console with level 'all' to see everything.");
            return sb.ToString();
        }
        foreach (var entry in shown) sb.AppendLine(Format(entry));
        if (matching.Count > shown.Count)
            sb.AppendLine($"… {matching.Count - shown.Count} older message(s) not shown; raise 'limit' or filter with 'level'.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Text for the note section of a tool result: the errors recorded after <paramref name="cursor"/> (at most
    /// <paramref name="maxErrors"/> lines) and a count of new warnings. Empty when nothing new was logged. The cursor
    /// advances atomically with the snapshot, so every entry is auto-reported exactly once.
    /// </summary>
    public string TakeSummary(ref long cursor, int maxErrors = 5)
    {
        List<BrowserConsoleEntry> fresh;
        var since = cursor;
        lock (_gate)
        {
            fresh = [.. _entries.Where(entry => entry.Seq > since)];
            cursor = _seq;
        }
        var errors = fresh.Where(entry => entry.IsError).ToList();
        var warnings = fresh.Count(entry => entry.Level == "warning");
        if (errors.Count == 0 && warnings == 0) return "";

        var sb = new StringBuilder("Console: ");
        if (errors.Count > 0) sb.Append(errors.Count == 1 ? "1 new error" : $"{errors.Count} new errors");
        if (errors.Count > 0 && warnings > 0) sb.Append(", ");
        if (warnings > 0) sb.Append(warnings == 1 ? "1 new warning" : $"{warnings} new warnings");
        sb.AppendLine(" since the last call.");
        foreach (var entry in errors.Take(Math.Max(0, maxErrors))) sb.AppendLine(Format(entry));
        if (errors.Count > maxErrors)
            sb.AppendLine($"… {errors.Count - maxErrors} more error(s); run the console action to read them.");
        else
            sb.Append("The console action returns the full log, including warnings and console.log.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line of the report: time, level, text and where it was logged.</summary>
    public static string Format(BrowserConsoleEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append("- ").Append(entry.At.ToLocalTime().ToString("HH:mm:ss")).Append(' ')
          .Append(entry.Level).Append(": ").Append(entry.Text);
        if (entry.Location is { Length: > 0 }) sb.Append(" (").Append(entry.Location).Append(')');
        return sb.ToString();
    }

    /// <summary>Minimum severity of the entries to return; throws on an unknown level.</summary>
    public static int Threshold(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "all" => 0,
        "debug" or "trace" => 1,
        "log" or "info" => 2,
        "warning" or "warn" => 3,
        "error" or "pageerror" => 4,
        var other => throw new ArgumentException($"Unknown console level '{other}'. Use error, warning, info or all."),
    };

    /// <summary>Severity of a stored level: uncaught exceptions and errors highest, traces lowest.</summary>
    public static int Severity(string level) => level switch
    {
        "pageerror" or "error" => 4,
        "warning" => 3,
        "info" or "log" => 2,
        "debug" or "trace" => 1,
        _ => 2,
    };

    private static string Name(int threshold) => threshold switch
    {
        >= 4 => "error",
        3 => "warning",
        2 => "info",
        1 => "debug",
        _ => "all",
    };

    /// <summary>The browser's message type collapsed to what the tool reports (unknown types stay readable logs).</summary>
    private static string Normalize(string level) => level.Trim().ToLowerInvariant() switch
    {
        "error" or "assert" => "error",
        "warning" or "warn" => "warning",
        "info" => "info",
        "debug" => "debug",
        "trace" => "trace",
        "pageerror" => "pageerror",
        _ => "log",
    };

    /// <summary>Entries are single lines: a stack would otherwise turn one message into dozens in a tool result.</summary>
    private static string OneLine(string? value, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // A page can log an enormous object; bound the work before flattening and truncating.
        if (value.Length > 4000) value = value[..4000];
        var flat = value.ReplaceLineEndings(" ").Replace('\t', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        flat = flat.Trim();
        return flat.Length > max ? flat[..max] + "…" : flat;
    }
}
