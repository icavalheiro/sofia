using Sofia.App.Services;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>
/// Context-update card (web <c>.context-card</c>): the changes the server noticed since the system prompt was frozen,
/// attached to a user message and collapsed by default. The leading bullet marker the server puts on each line is
/// stripped for display.
/// </summary>
public sealed class ContextCardViewModel
{
    private static readonly char[] Bullets = ['-', '*'];

    public ContextCardViewModel(IEnumerable<string> notes)
    {
        Lines = notes.Select(Strip).Where(line => line.Length > 0).ToList();
    }

    public List<string> Lines { get; }

    public bool Expanded { get; set; }

    public string Title => Localization.T("contextUpdates.title");

    public string ToggleLabel => Localization.T(Expanded ? "contextUpdates.hide" : "contextUpdates.view");

    public void Toggle() => Expanded = !Expanded;

    private static string Strip(string note)
    {
        var trimmed = note.TrimStart();
        if (trimmed.Length == 0) return "";
        if (Bullets.Contains(trimmed[0])) trimmed = trimmed[1..].TrimStart();
        return trimmed;
    }
}
