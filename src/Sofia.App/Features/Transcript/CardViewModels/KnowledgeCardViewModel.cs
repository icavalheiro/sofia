using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>One knowledge note on the card, with the deleted strike-through state the server updates.</summary>
public sealed class KnowledgeNoteViewModel(KnowledgeEntry entry)
{
    public KnowledgeEntry Entry { get; } = entry;

    public string Id => Entry.Id;

    public string Text => Entry.Text;

    public string? Why => Entry.Why;

    public bool Deleted
    {
        get => Entry.Deleted;
        set => Entry.Deleted = value;
    }

    public string ScopeLabel => Localization.T($"knowledge.scope.{Entry.Scope}");
}

/// <summary>
/// Knowledge card (web <c>.knowledge-card</c>): notes the harness distilled from the conversation, viewable and
/// deletable through <c>DELETE /api/knowledge/{id}</c>; a deleted note stays listed and struck through.
/// </summary>
public sealed class KnowledgeCardViewModel
{
    public KnowledgeCardViewModel(IEnumerable<KnowledgeEntry> entries)
    {
        Notes = entries.Select(e => new KnowledgeNoteViewModel(e)).ToList();
    }

    public List<KnowledgeNoteViewModel> Notes { get; }

    public bool Expanded { get; set; }

    public string Title => Localization.T("knowledge.title");

    public string ToggleLabel => Localization.T(Expanded ? "knowledge.hide" : "knowledge.view");

    public int LiveCount => Notes.Count(n => !n.Deleted);

    public string? Error { get; private set; }

    public void Toggle() => Expanded = !Expanded;

    public void MarkDeleted(string id)
    {
        foreach (var note in Notes.Where(n => n.Id == id)) note.Deleted = true;
        Error = null;
    }

    public void Fail(string? error) => Error = error;
}
