using Sofia.App.Services;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>Compaction summary card (web <c>.summary-card</c>): the earlier conversation, condensed.</summary>
public sealed class SummaryCardViewModel(string text)
{
    public string Text { get; } = text;

    public string Title => Localization.T("chat.summary");
}
