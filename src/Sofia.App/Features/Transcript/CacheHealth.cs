using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>Why one assistant step was flagged as a prompt-cache break, with the numbers to show.</summary>
public sealed record CacheBreakInfo(double Pct, long Cached, long Input, IReadOnlyList<string> Reasons);

/// <summary>
/// Prompt-cache health port of <c>services/cacheHealth.ts</c>: providers only report how many input tokens came from
/// the cache, so a low ratio turns into an on-message warning with local suspects read off the transcript itself.
/// </summary>
public static class CacheHealth
{
    /// <summary>Steps below this cache-hit percentage get a warning card (a session's first step is exempt).</summary>
    public const double LowCacheThreshold = 10;

    /// <summary>Prompts smaller than this carry no useful ratio (and are never real turns).</summary>
    public const long MinPromptTokens = 200;

    /// <summary>A tool result bigger than this share of the prompt is worth calling out.</summary>
    private const double BigToolOutputShare = 0.25;

    /// <summary>One warning per assistant step, indexed like the transcript, or null where there is nothing to warn about.</summary>
    public static IReadOnlyList<CacheBreakInfo?> Annotate(IReadOnlyList<Message> messages)
    {
        var result = new CacheBreakInfo?[messages.Count];

        // A provider that never reuses cache reports 0 everywhere; a session needs at least one hit to be worth judging.
        var reportsCache = messages.Any(m => (m.Usage?.CacheRead ?? 0) > 0);
        if (!reportsCache) return result;

        Message? previous = null;
        var compactedBefore = false;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var usage = message.Usage;
            var pct = message.Role == "assistant" && message.Summary != true && usage is { Input: > 0 }
                ? HitPercent(usage.CacheRead, usage.Input)
                : (double?)null;

            if (pct is not null && usage is not null && usage.Input >= MinPromptTokens &&
                pct < LowCacheThreshold && previous is not null)
            {
                result[i] = new CacheBreakInfo(pct.Value, usage.CacheRead, usage.Input,
                    Reasons(message, previous, compactedBefore));
            }

            if (message.Summary == true) compactedBefore = true;
            if (message.Role == "assistant") previous = message;
        }
        return result;
    }

    /// <summary>A 100% hit is impossible: whole numbers below 99, then up to 3 decimals capped at 99.999.</summary>
    public static double HitPercent(long cached, long input)
    {
        if (input <= 0) return 0;
        var raw = (double)cached / input * 100;
        return raw < 99 ? Math.Round(raw) : Math.Min(99.999, Math.Floor(raw * 1000) / 1000);
    }

    /// <summary>Localisation keys of the suspects, most likely first. Never empty (the prefix itself is the fallback).</summary>
    private static List<string> Reasons(Message message, Message previous, bool compactedBefore)
    {
        var reasons = new List<string>();
        if ((message.Model ?? "") != (previous.Model ?? "")) reasons.Add("cache.reasonModel");
        if ((message.ReasoningMode ?? "") != (previous.ReasoningMode ?? "")) reasons.Add("cache.reasonReasoning");
        if (compactedBefore) reasons.Add("cache.reasonCompaction");

        // The tool results of the previous step are new prompt bytes: a big one appended after the cached prefix
        // re-bills everything that follows it.
        long appended = 0;
        foreach (var call in previous.ToolCalls ?? [])
        {
            appended += (long)Math.Ceiling((call.Output ?? call.Error ?? "").Length / 4.0);
        }
        if (message.Usage is not null && appended > message.Usage.Input * BigToolOutputShare) reasons.Add("cache.reasonToolOutput");

        if ((message.Attachments?.Count ?? 0) > 0 || (previous.Attachments?.Count ?? 0) > 0) reasons.Add("cache.reasonAttachments");
        reasons.Add("cache.reasonPrefixChanged");
        return reasons;
    }

    /// <summary>The prompt handed to the model (or another tool) to explain the break.</summary>
    public static string DiagnosisTemplate() => string.Join('\n',
    [
        "Sofia's prompt cache only covered {pct}% of the request that produced this reply ({cached} of {input} tokens).",
        "",
        "Suspects Sofia detected in the transcript:",
        "{reasons}",
        "",
        "Explain which of these actually broke the cached prefix for this request, how prompt caching works with this",
        "provider, and what I should change (message order, attachments, tool usage, model or reasoning switches) so the",
        "cache stays warm in this session.",
    ]);
}
