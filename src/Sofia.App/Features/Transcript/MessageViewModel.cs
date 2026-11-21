using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Features.Transcript.CardViewModels;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// One transcript row projection over <see cref="Message"/> (web <c>ChatMessageItem.vue</c>): the text/reasoning prose,
/// the tool cards, every card of the family, attachments, the prompt-cache warning and the row's own action state
/// (armed revert, busy flags, inline error). Streaming appends to the model and raises <see cref="Streamed"/>; a full
/// <c>message.updated</c> goes through <see cref="Apply"/> and raises <see cref="Changed"/>, keeping the user's fold
/// state, question picks and armed revert.
/// </summary>
public sealed class MessageViewModel : ObservableObject
{
    private readonly TranscriptActions _actions;
    private readonly QuestionDraftStore _drafts;
    private string? _previousText;

    public MessageViewModel(Message model, TranscriptActions actions, QuestionDraftStore drafts, bool streaming = false)
    {
        Model = model;
        _actions = actions;
        _drafts = drafts;
        IsStreaming = streaming;
        Rebuild(previous: null);
    }

    /// <summary>The wire message (replaced on every <c>message.updated</c>).</summary>
    public Message Model { get; private set; }

    public string Id => Model.Id;

    public string Role => Model.Role;

    public string SessionId => Model.SessionId;

    public string Text => Model.Text;

    public string? Reasoning => Model.Reasoning;

    public string? ModelLabel => Model.Model;

    /// <summary>Hidden while the agent is still reasoning/looking for its first tool call.</summary>
    public string TimeLabel => Model.CreatedAt == default ? "" : Model.CreatedAt.ToLocalTime().ToString("HH:mm");

    public bool IsAssistant => Model.Role == "assistant";

    public bool IsCompaction => Model.Compaction is not null;

    public bool IsStreaming { get; private set; }

    /// <summary>Steer/queue delivery of a follow-up the server has not sent to the model yet.</summary>
    public string? PendingDelivery { get; set; }

    public bool IsPending => PendingDelivery is not null;

    public bool IsFailed => !string.IsNullOrEmpty(Model.Error);

    public bool IsLocal { get; set; }

    public ReasonsCardBundle Cards { get; private set; } = new();

    public IReadOnlyList<ToolCardViewModel> Tools { get; private set; } = [];

    public IReadOnlyList<Attachment> ImageAttachments { get; private set; } = [];

    public IReadOnlyList<Attachment> FileAttachments { get; private set; } = [];

    public CacheBreakInfo? CacheBreak { get; set; }

    public string? ActionError { get; set; }

    public bool RevertArmed { get; set; }

    public bool Busy { get; private set; }

    /// <summary>True when the row has a text/notification body (knowledge-only rows hide it, like the web).</summary>
    public bool ShowsBody => Cards.Knowledge is null;

    /// <summary>Set by the transcript while this is the last row of a working run.</summary>
    public bool Streaming
    {
        get => IsStreaming;
        private set => IsStreaming = value;
    }

    public string Author => IsAssistant ? "Sofia" : "You";

    public event Action? Changed;

    public event Action? Streamed;

    /// <summary>Replaces the wire message; preserves the user's fold/open/pick state.</summary>
    public void Apply(Message model)
    {
        var previous = this;
        Model = model;
        Rebuild(previous);
        Changed?.Invoke();
    }

    /// <summary>Appends one coalesced <c>message.delta</c> (<c>text</c> or <c>reasoning</c>).</summary>
    public void AppendDelta(string field, string delta)
    {
        if (field == "reasoning") Model.Reasoning = (Model.Reasoning ?? "") + delta;
        else Model.Text = (Model.Text ?? "") + delta;
        Cards.Reasoning?.Update(Model.Reasoning ?? "", Model.ReasoningMode, ReasoningLive);
        Streamed?.Invoke();
    }

    /// <summary>Marks the row as the live one (or not) and recomputes the reasoning fold rule.</summary>
    public void SetStreaming(bool streaming)
    {
        IsStreaming = streaming;
        Cards.Reasoning?.Update(Model.Reasoning ?? "", Model.ReasoningMode, ReasoningLive);
    }

    private bool ReasoningLive => IsStreaming && string.IsNullOrWhiteSpace(Model.Text) && Tools.Count == 0;

    // --- actions ------------------------------------------------------------------------------------------

    public async Task<string?> ResolvePermissionAsync(string choice)
    {
        var permission = Model.Permission;
        if (permission is null || !string.IsNullOrEmpty(permission.Resolved) || Busy) return null;
        var previous = permission.Resolved;
        permission.Resolved = choice;
        Changed?.Invoke();
        Busy = true;
        var error = await _actions.ResolvePermissionAsync(Model.SessionId, permission.RequestId,
            PermissionCardViewModel.Decision(choice));
        Busy = false;
        if (error is not null)
        {
            permission.Resolved = previous;
            ActionError = error;
            Changed?.Invoke();
        }
        return error;
    }

    public async Task<string?> AnswerQuestionAsync(IReadOnlyList<string> picks)
    {
        var question = Model.Question;
        if (question is null || !string.IsNullOrEmpty(question.Resolved) || picks.Count == 0 || Busy) return null;
        var previous = question.Resolved;
        question.Resolved = string.Join(", ", picks);
        Changed?.Invoke();
        Busy = true;
        var answer = question.Multi
            ? JsonSerializer.SerializeToElement(picks)
            : JsonSerializer.SerializeToElement(picks[0]);
        var error = await _actions.AnswerQuestionAsync(Model.SessionId, question.FormId, [answer]);
        Busy = false;
        if (error is not null)
        {
            question.Resolved = previous;
            ActionError = error;
            Changed?.Invoke();
        }
        return error;
    }

    public async Task<string?> ConfirmQuestionsAsync(QuestionSetCardViewModel card)
    {
        var set = Model.QuestionSet;
        if (set is null || set.Confirmed == true || !card.AllAnswered || Busy) return null;
        var previous = set.Answers;
        set.Answers = card.BuildAnswers().Select(a => a?.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.Value.EnumerateArray().Select(v => v.GetString()))
                : a?.GetString())
            .ToList();
        set.Confirmed = true;
        Changed?.Invoke();
        Busy = true;
        var error = await _actions.AnswerQuestionAsync(Model.SessionId, set.FormId, card.BuildAnswers());
        Busy = false;
        if (error is not null)
        {
            set.Answers = previous;
            set.Confirmed = false;
            ActionError = error;
            Changed?.Invoke();
            return error;
        }
        card.ClearDraft();
        return null;
    }

    public async Task DeleteKnowledgeAsync(KnowledgeCardViewModel card, KnowledgeNoteViewModel note)
    {
        if (Busy) return;
        Busy = true;
        var error = await _actions.DeleteKnowledgeAsync(note.Id);
        Busy = false;
        if (error is not null)
        {
            card.Fail(error);
            Changed?.Invoke();
            return;
        }
        card.MarkDeleted(note.Id);
        Changed?.Invoke();
    }

    public async Task<string?> BranchAsync()
    {
        if (Busy || IsLocal) return null;
        Busy = true;
        var error = await _actions.BranchAsync(Model.SessionId, Model.Id);
        Busy = false;
        if (error is not null) ActionError = error;
        Changed?.Invoke();
        return error;
    }

    public async Task<string?> RetryAsync()
    {
        if (Busy || IsLocal) return null;
        Busy = true;
        var error = await _actions.RetryAsync(Model.SessionId, Model.Id);
        Busy = false;
        if (error is not null) ActionError = error;
        Changed?.Invoke();
        return error;
    }

    public async Task<string?> RevertAsync()
    {
        if (Busy || IsLocal) return null;
        Busy = true;
        var error = await _actions.RevertAsync(Model.SessionId, Model.Id);
        Busy = false;
        if (error is not null) ActionError = error;
        Changed?.Invoke();
        return error;
    }

    public async Task<string?> RemoveQueuedAsync()
    {
        if (Busy) return null;
        Busy = true;
        var error = await _actions.RemoveQueuedAsync(Model.SessionId, Model.Id);
        Busy = false;
        if (error is not null) ActionError = error;
        Changed?.Invoke();
        return error;
    }

    /// <summary>The text a copy action puts on the clipboard (web copies the source markdown).</summary>
    public string CopySource => (string.IsNullOrWhiteSpace(Model.Text) ? Model.Reasoning ?? "" : Model.Text).Trim();

    // --- projection ---------------------------------------------------------------------------------------

    private void Rebuild(MessageViewModel? previous)
    {
        var previousCards = previous?.Cards;
        var cards = new ReasonsCardBundle();

        if (Model.Compaction is not null)
        {
            cards.Compaction = new CompactionCardViewModel(Model.Compaction);
            if (previousCards?.Compaction is { } oldCompaction) cards.Compaction.Expanded = oldCompaction.Expanded;
            Cards = cards;
            Tools = [];
            ImageAttachments = [];
            FileAttachments = [];
            _previousText = Model.Text;
            return;
        }

        if (!string.IsNullOrEmpty(Model.Reasoning))
        {
            cards.Reasoning = previousCards?.Reasoning is { } oldReasoning
                ? UpdateReasoning(oldReasoning)
                : new ReasoningCardViewModel(Model.Reasoning!, Model.ReasoningMode, ReasoningLive);
        }

        if (Model.Summary == true && !string.IsNullOrEmpty(Model.Text)) cards.Summary = new SummaryCardViewModel(Model.Text);

        if (Model.Knowledge is { Count: > 0 })
        {
            cards.Knowledge = new KnowledgeCardViewModel(Model.Knowledge);
            if (previousCards?.Knowledge is { } oldKnowledge) cards.Knowledge.Expanded = oldKnowledge.Expanded;
        }

        if (Model.ContextNotes is { Count: > 0 })
        {
            cards.Context = new ContextCardViewModel(Model.ContextNotes);
            if (previousCards?.Context is { } oldContext) cards.Context.Expanded = oldContext.Expanded;
        }

        if (Model.Retry is not null) cards.Retry = new RetryCardViewModel(Model.Retry);
        if (!string.IsNullOrEmpty(Model.Error)) cards.Error = new ErrorCardViewModel(Model.Error!);
        if (Model.Permission is not null) cards.Permission = new PermissionCardViewModel(Model.Permission);

        if (Model.Question is not null)
        {
            cards.Question = new QuestionCardViewModel(Model.Question);
            if (previousCards?.Question is { } oldQuestion && oldQuestion.Card.FormId == Model.Question.FormId)
            {
                cards.Question.Picks.AddRange(oldQuestion.Picks);
                cards.Question.FreeText = oldQuestion.FreeText;
            }
        }

        if (Model.QuestionSet is not null) cards.QuestionSet = new QuestionSetCardViewModel(Model.QuestionSet, _drafts);

        Cards = cards;

        var tools = new List<ToolCardViewModel>();
        foreach (var call in Model.ToolCalls ?? [])
        {
            var card = new ToolCardViewModel(call);
            if (previous is not null && previous.Tools.FirstOrDefault(t => t.Id == call.Id) is { } old) card.CopyStateFrom(old);
            tools.Add(card);
        }
        Tools = tools;

        var attachments = Model.Attachments ?? [];
        ImageAttachments = attachments.Where(a => a.Kind == "image" && !string.IsNullOrEmpty(a.Url)).ToList();
        FileAttachments = attachments.Where(a => a.Kind != "image" || string.IsNullOrEmpty(a.Url)).ToList();
        _previousText = Model.Text;
    }

    private ReasoningCardViewModel UpdateReasoning(ReasoningCardViewModel card)
    {
        card.Update(Model.Reasoning ?? "", Model.ReasoningMode, ReasoningLive);
        return card;
    }

    /// <summary>The field group of the card family (all optional, so a row renders only what it has).</summary>
    public sealed class ReasonsCardBundle
    {
        public ReasoningCardViewModel? Reasoning { get; set; }
        public SummaryCardViewModel? Summary { get; set; }
        public KnowledgeCardViewModel? Knowledge { get; set; }
        public ContextCardViewModel? Context { get; set; }
        public RetryCardViewModel? Retry { get; set; }
        public ErrorCardViewModel? Error { get; set; }
        public CompactionCardViewModel? Compaction { get; set; }
        public PermissionCardViewModel? Permission { get; set; }
        public QuestionCardViewModel? Question { get; set; }
        public QuestionSetCardViewModel? QuestionSet { get; set; }
    }
}
