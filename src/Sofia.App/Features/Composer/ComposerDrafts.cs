using Sofia.App.Services;

namespace Sofia.App.Features.Composer;

/// <summary>
/// Per-conversation drafts, kept in the app's <see cref="LocalState"/> under the web's own key
/// (<c>sofia.drafts</c>) so a future import from the browser stays a copy. A draft is the text plus the pending
/// attachment chips and the referenced sessions of one conversation; nothing here ever touches <c>~/.sofia</c> or the
/// server.
/// </summary>
/// <remarks>
/// The web parks the open conversation's draft in memory and only persists the panel/flag state; the app persists
/// every draft, which is what makes the spec's "restart the app: drafts survive" check possible. The JSON shape is
/// the web's <c>drafts</c> map shape (<c>{ [sessionId]: { text, attachments, references } }</c>).
/// </remarks>
public sealed class ComposerDraftStore
{
    /// <summary>Serialized shape of one draft (public setters so <see cref="JsonSerializer"/> fills it).</summary>
    public sealed class DraftDto
    {
        public string Text { get; set; } = "";

        public List<AttachmentDto> Attachments { get; set; } = [];

        public List<ReferenceDto> References { get; set; } = [];
    }

    public sealed class AttachmentDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "file";
        public string Mime { get; set; } = "application/octet-stream";
        public long Bytes { get; set; }
        public string? UploadedId { get; set; }
        public string? PreviewUrl { get; set; }
    }

    public sealed class ReferenceDto
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private readonly LocalState _state;

    public ComposerDraftStore(LocalState? state = null) => _state = state ?? LocalState.Current;

    public LocalState State => _state;

    /// <summary>Reads one conversation's draft, or null when it has none.</summary>
    public DraftDto? Get(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var all = ReadAll();
        return all.TryGetValue(sessionId, out var draft) && (draft.Text.Length > 0 || draft.Attachments.Count > 0 ||
                                                             draft.References.Count > 0)
            ? draft
            : null;
    }

    /// <summary>Stores (or clears, when everything is empty) one conversation's draft and flushes it at once.</summary>
    public void Save(string? sessionId, string text, IEnumerable<ComposerAttachment> attachments,
        IEnumerable<ComposerReference> references)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var all = ReadAll();
        var draft = new DraftDto
        {
            Text = text,
            Attachments = attachments.Select(a => new AttachmentDto
            {
                Id = a.LocalId,
                Name = a.Name,
                Kind = a.Kind,
                Mime = a.Mime,
                Bytes = a.Bytes,
                UploadedId = a.UploadedId,
                PreviewUrl = a.PreviewUrl,
            }).ToList(),
            References = references.Select(r => new ReferenceDto { Id = r.Id, Title = r.Title }).ToList(),
        };
        if (draft.Text.Length == 0 && draft.Attachments.Count == 0 && draft.References.Count == 0)
        {
            if (!all.Remove(sessionId)) return;
        }
        else
        {
            all[sessionId] = draft;
        }
        _state.Set(LocalState.DraftsKey, all);
        // Drafts are the one thing a crash must not lose: write now instead of waiting for the debounce.
        _state.SaveNow();
    }

    /// <summary>Attachments of a draft as composer chips.</summary>
    public static IEnumerable<ComposerAttachment> ToAttachments(DraftDto draft) =>
        draft.Attachments.Select(a => new ComposerAttachment
        {
            LocalId = a.Id.Length > 0 ? a.Id : "a_" + Guid.NewGuid().ToString("N")[..8],
            Name = a.Name,
            Kind = a.Kind,
            Mime = a.Mime,
            Bytes = a.Bytes,
            UploadedId = a.UploadedId,
            PreviewUrl = a.PreviewUrl,
        });

    private Dictionary<string, DraftDto> ReadAll()
    {
        var map = _state.Get<Dictionary<string, DraftDto>>(LocalState.DraftsKey);
        return map is null ? new Dictionary<string, DraftDto>(StringComparer.Ordinal) : new(map, StringComparer.Ordinal);
    }
}
