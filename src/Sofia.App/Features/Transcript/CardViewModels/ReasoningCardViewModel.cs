using Sofia.App.Services;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>
/// The beige reasoning block (web <c>.reasoning</c>): a foldable panel whose header reads "Reasoning · {mode}".
/// Finished reasoning starts collapsed; live reasoning starts open and folds itself once the agent stops reasoning.
/// </summary>
public sealed class ReasoningCardViewModel
{
    public ReasoningCardViewModel(string text, string? mode, bool live)
    {
        Text = text;
        Mode = string.IsNullOrWhiteSpace(mode) ? "medium" : mode!;
        IsLive = live;
        Expanded = live;
    }

    public string Text { get; private set; }

    public string Mode { get; private set; }

    public bool IsLive { get; private set; }

    /// <summary>Header label exactly like the web: "Reasoning · medium" in uppercase via the style class.</summary>
    public string Label => Localization.T("chat.reasoning") + " · " + Mode;

    public bool Expanded { get; set; }

    public void Update(string text, string? mode, bool live)
    {
        Text = text;
        Mode = string.IsNullOrWhiteSpace(mode) ? "medium" : mode!;
        if (IsLive && !live) Expanded = false; // the model moved on to its answer: fold it like the web
        IsLive = live;
    }

    public void Toggle() => Expanded = !Expanded;
}
