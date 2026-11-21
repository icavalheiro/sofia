using System.Text.Json;
using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>
/// Single clarifying question (web <c>.q-card</c>): primary border on primary-soft, one button per option plus a
/// free-text field. Single-select answers on click; multi-select accumulates picks until the user confirms.
/// </summary>
public sealed class QuestionCardViewModel(QuestionCard card)
{
    public QuestionCard Card { get; } = card;

    public string Prompt => Card.Prompt;

    public bool Multi => Card.Multi;

    public bool Resolved => !string.IsNullOrEmpty(Card.Resolved);

    public string ResolvedText => Card.Resolved ?? "";

    public string FreeText { get; set; } = "";

    /// <summary>Multi-select picks, kept on the card so a server update does not lose them.</summary>
    public List<string> Picks { get; } = [];

    public bool IsPicked(string option) => Picks.Contains(option);

    public void TogglePick(string option)
    {
        if (!Picks.Remove(option)) Picks.Add(option);
    }

    /// <summary>Web <c>answerFree</c>: an extra option at the end of every question.</summary>
    public IReadOnlyList<string>? FreePicks()
    {
        var text = FreeText.Trim();
        if (text.Length == 0) return null;
        FreeText = "";
        if (!Multi) return [text];
        if (!Picks.Contains(text)) Picks.Add(text);
        return null;
    }

    public string Hint => Localization.T("q.multiHint");

    public string ConfirmLabel => Localization.T("q.confirm");

    public string FreePlaceholder => Localization.T("q.freePlaceholder");
}

/// <summary>Draft store for the multi-question carousel: picks survive the <c>message.updated</c> replacements.</summary>
public sealed class QuestionDraftStore
{
    private readonly Dictionary<string, List<List<string>>> _picks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _free = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _slides = new(StringComparer.Ordinal);

    public int Slide(string formId) => _slides.TryGetValue(formId, out var slide) ? slide : 0;

    public void SetSlide(string formId, int slide) => _slides[formId] = slide;

    public List<List<string>> Picks(string formId, int count)
    {
        if (!_picks.TryGetValue(formId, out var picks))
        {
            picks = [];
            _picks[formId] = picks;
        }
        while (picks.Count < count) picks.Add([]);
        return picks;
    }

    public string Free(string formId, int index)
    {
        if (!_free.TryGetValue(formId, out var free))
        {
            free = [];
            _free[formId] = free;
        }
        while (free.Count <= index) free.Add("");
        return free[index];
    }

    public void SetFree(string formId, int index, string value)
    {
        var free = _free.TryGetValue(formId, out var list) ? list : _free[formId] = [];
        while (free.Count <= index) free.Add("");
        free[index] = value;
    }

    public void Clear(string formId)
    {
        _picks.Remove(formId);
        _free.Remove(formId);
        _slides.Remove(formId);
    }
}

/// <summary>
/// Multi-question carousel (web <c>.q-carousel</c>): one page per question plus a review page, nothing is sent until
/// the user confirms there. Picks live in <see cref="QuestionDraftStore"/> because server updates replace the message.
/// </summary>
public sealed class QuestionSetCardViewModel
{
    private readonly QuestionDraftStore _drafts;

    public QuestionSetCardViewModel(QuestionSetCard card, QuestionDraftStore drafts)
    {
        Card = card;
        _drafts = drafts;
        Picks = drafts.Picks(card.FormId, card.Questions.Count);
        Slide = Math.Clamp(drafts.Slide(card.FormId), 0, Math.Max(0, card.Questions.Count));
    }

    public QuestionSetCard Card { get; }

    public List<List<string>> Picks { get; }

    public int Count => Card.Questions.Count;

    public int TotalSlides => Count + 1;

    public int Slide { get; private set; }

    /// <summary>Direction of the last step, for the ±32px carousel transition (motion contract #17).</summary>
    public string Direction { get; private set; } = "fwd";

    public bool OnReview => Slide >= Count;

    public bool Confirmed => Card.Confirmed == true;

    public string Title => Localization.T("q.title");

    public string CountLabel => OnReview ? Localization.T("q.review") : $"{Slide + 1} / {Count}";

    public string ReviewPrompt => Localization.T("q.reviewPrompt");

    public string Hint => Localization.T("q.multiHint");

    public string NextLabel => Localization.T("q.next");

    public string ConfirmLabel => Localization.T("q.confirm");

    public string FreePlaceholder => Localization.T("q.freePlaceholder");

    public bool AllAnswered => Count > 0 && Enumerable.Range(0, Count).All(i => Picks[i].Count > 0);

    public string Free(int index)
    {
        var value = _drafts.Free(Card.FormId, index);
        return value;
    }

    public void SetFree(int index, string value) => _drafts.SetFree(Card.FormId, index, value);

    public bool IsPicked(int index, string option) => Picks[index].Contains(option);

    public void GoTo(int index)
    {
        var next = Math.Clamp(index, 0, TotalSlides - 1);
        if (next == Slide) return;
        Direction = next > Slide ? "fwd" : "back";
        Slide = next;
        _drafts.SetSlide(Card.FormId, next);
    }

    /// <summary>Signed step of the next <see cref="GoTo"/> (1 forward, -1 back) for the carousel stage.</summary>
    public int DirectionSign => Direction == "fwd" ? 1 : -1;

    /// <summary>Web <c>pickOption</c>: multi toggles in place, single selects and advances.</summary>
    public void Pick(int index, string option)
    {
        if (index < 0 || index >= Count) return;
        var question = Card.Questions[index];
        var picks = Picks[index];
        if (question.Multi)
        {
            if (!picks.Remove(option)) picks.Add(option);
            return;
        }
        picks.Clear();
        picks.Add(option);
        GoTo(index + 1);
    }

    /// <summary>Web <c>submitFree</c>: free text is an extra option; multi keeps it, single advances.</summary>
    public void SubmitFree(int index)
    {
        if (index < 0 || index >= Count) return;
        var text = _drafts.Free(Card.FormId, index).Trim();
        if (text.Length == 0) return;
        _drafts.SetFree(Card.FormId, index, "");
        var picks = Picks[index];
        if (Card.Questions[index].Multi)
        {
            if (!picks.Contains(text)) picks.Add(text);
            return;
        }
        picks.Clear();
        picks.Add(text);
        GoTo(index + 1);
    }

    /// <summary>The <c>answers</c> array posted to <c>POST /api/sessions/{id}/questions/{formId}</c>.</summary>
    public List<JsonElement?> BuildAnswers()
    {
        var answers = new List<JsonElement?>(Count);
        for (var i = 0; i < Count; i++)
        {
            var chosen = Picks[i];
            if (chosen.Count == 0)
            {
                answers.Add(null);
                continue;
            }
            answers.Add(chosen.Count == 1
                ? JsonSerializer.SerializeToElement(chosen[0])
                : JsonSerializer.SerializeToElement(chosen));
        }
        return answers;
    }

    public void ClearDraft() => _drafts.Clear(Card.FormId);
}
