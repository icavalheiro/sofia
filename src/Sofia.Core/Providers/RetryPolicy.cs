using System.Net.Sockets;

namespace Sofia.Core.Providers;

/// <summary>
/// Which provider failures mean "this request never got an answer" and are worth repeating, and how long to wait first.
/// Sofia repeats a model step before the failure becomes an error in the chat: local runtimes behind a proxy answer
/// <c>499</c> (nginx' "Client Closed Request") or drop the socket while they load a model, gateways shed load with 429
/// and 5xx, and one hiccup should not end a conversation. Everything else — a bad key, an unknown model, a context
/// window that is too small, a runtime that is simply not running — fails fast, because repeating it only delays the
/// message that tells the user what to fix.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Repeats made after the first try; Sofia attempts one step <see cref="MaxAttempts"/> times in total.</summary>
    public const int MaxRetries = 5;

    public const int MaxAttempts = MaxRetries + 1;

    /// <summary>
    /// Statuses that mean the request never reached the model or an intermediary gave up on it: 408/425 timeouts,
    /// 409 and 429 load shedding, 499 a hung-up client connection, 5xx and the Cloudflare family upstream failures.
    /// </summary>
    private static readonly HashSet<int> TransientStatuses = [408, 409, 425, 429, 499, 500, 502, 503, 504, 520, 521, 522, 524, 529, 598, 599];

    /// <summary>Markers of a temporary in-stream provider error, which arrives without a status code.</summary>
    private static readonly string[] TransientText =
    [
        "overloaded", "rate limit", "rate_limit", "too many requests", "timed out", "timeout", "temporarily unavailable",
        "try again", "reset by peer", "ended prematurely", "aborted", "server_error", "service unavailable",
    ];

    /// <summary>Whether repeating the exact same request can plausibly succeed. Never retries a user abort: callers
    /// check their own cancellation token first, so a cancellation reaching here came from a provider timeout.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        HttpRequestException or IOException or SocketException => true,
        ProviderException p => p.Status is { } status
            ? TransientStatuses.Contains(status)
            : IsTransientText(p.Message),
        _ => false,
    };

    /// <summary>Whether the failure means the request body (usually accumulated images) exceeded the provider's size
    /// or context limit. Such a request cannot succeed by repeating it unchanged, but it can succeed once the
    /// heaviest parts (old images) are dropped.</summary>
    private static readonly string[] TooLargeText =
    [
        "too large", "request too large", "request entity too large", "payload too large",
        "context length", "context_length", "prompt is too long", "maximum context", "max_tokens",
        "too many tokens", "token limit",
    ];

    public static bool IsRequestTooLarge(Exception ex)
    {
        if (ex is not ProviderException p) return false;
        if (p.Status is 413 or 400) { /* fall through to text check */ }
        else if (p.Status is not null) return false;
        var message = p.Message ?? "";
        foreach (var marker in TooLargeText)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        // A bare 413 is always "too large" regardless of wording.
        return p.Status == 413;
    }

    /// <summary>Whether the failure means the endpoint refused image input although the model was expected to take it
    /// (a text-only GGUF loaded under a vision-capable alias, a gateway with images disabled). Repeating such a request
    /// unchanged fails again; withholding the images — and describing them with the fallback vision model instead —
    /// lets the step succeed.</summary>
    /// <remarks>
    /// Providers word this in far too many ways to enumerate ("does not support vision", "current GGUF model does not
    /// support vision", "This model does not support image input", "image_url is not supported here", "multimodal
    /// content is disabled"), so the test is a refusal phrase next to an image concept instead of exact sentences.
    /// Over-triggering is cheap — the worst case is one retry that sends descriptions instead of pixels.
    /// </remarks>
    private static readonly string[] RefusalText =
    [
        "not supported", "does not support", "doesn't support", "don't support", "do not support", "unsupported",
        "cannot accept", "can't accept", "can not accept", "unable to process", "unable to handle", "invalid",
        "no support for", "is disabled", "not accepted", "only text", "text-only", "text only",
    ];

    private static readonly string[] ImageConceptText =
    [
        "image", "vision", "multimodal", "screenshot", "picture", "photo", "visual",
    ];

    public static bool IsImageRejected(Exception ex)
    {
        if (ex is not ProviderException p) return false;
        // An in-stream provider error carries no status code, and that is exactly how llama.cpp-family runtimes report
        // this — so a missing status must not veto the check. A present status has to be an input-level refusal:
        // 401/403/404 wording about images is an auth or routing problem, not a blind model.
        if (p.Status is not (null or 400 or 415 or 422)) return false;
        var message = p.Message ?? "";
        var refuses = false;
        foreach (var marker in RefusalText)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) { refuses = true; break; }
        if (!refuses) return false;
        foreach (var marker in ImageConceptText)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Fixed 5s wait between attempts.</summary>

    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(5);


    /// <summary>The one-line reason shown on the retry notice.</summary>
    public static string Reason(Exception ex) => Infrastructure.Text.FirstLine(ex.Message, 200);

    private static bool IsTransientText(string message)
    {
        foreach (var marker in TransientText)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}


