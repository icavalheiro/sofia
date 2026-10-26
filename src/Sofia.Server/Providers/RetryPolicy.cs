using System.Net.Sockets;

namespace Sofia.Server.Providers;

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


