using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>
/// The transient provider failure being retried (web <c>.msg-retry</c>): the step restarts, nothing has failed yet.
/// The retry glyph spins 1.8s linear and stops under reduced motion (motion contract #16).
/// </summary>
public sealed class RetryCardViewModel(RetryNotice notice)
{
    public int Attempt => notice.Attempt;

    public int Attempts => notice.Attempts;

    public string Text
    {
        get
        {
            var baseText = Localization.T("chat.providerRetrying")
                .Replace("{attempt}", notice.Attempt.ToString())
                .Replace("{attempts}", notice.Attempts.ToString());
            return string.IsNullOrWhiteSpace(notice.Error) ? baseText : $"{baseText} — {notice.Error}";
        }
    }
}

/// <summary>Terminal error banner (web <c>.msg-error</c>): err fill, err border, 3px left rule.</summary>
public sealed class ErrorCardViewModel(string text)
{
    public string Text { get; } = text;
}
