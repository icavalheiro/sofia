using Avalonia.Input;

namespace Sofia.App.Features.Composer;

/// <summary>What the composer does with a key press (the web's <c>onKeydown</c> branch order).</summary>
public enum ComposerKeyAction
{
    None,
    Send,
    PaletteDown,
    PaletteUp,
    PalettePick,
    PaletteClose,
    MentionDown,
    MentionUp,
    MentionPick,
    MentionClose,
    MenuClose,
}

/// <summary>
/// The composer's key policy, extracted from the view so the rules are unit-tested: Escape closes the innermost
/// surface (follow-up menu, mention popup, then the command palette), arrows/Enter/Tab drive the palettes and Enter
/// sends while Shift+Enter (or an active IME composition) never does.
/// </summary>
/// <remarks>
/// IME safety: an IME-dispatched key arrives as <see cref="Key.ImeProcessed"/> on Windows and must never send, and
/// the caller can also pass <c>imeComposing</c> when it knows a composition is in progress (the web reads
/// <c>KeyboardEvent.isComposing</c>; Avalonia does not expose that flag on <c>TextBox</c>, so the view feeds
/// <see cref="Key.ImeProcessed"/>). Both guards are tested.
/// </remarks>
public static class ComposerKeys
{
    public static ComposerKeyAction Decide(
        Key key,
        KeyModifiers modifiers,
        bool mentionVisible,
        bool paletteOpen,
        bool hasFilteredCommands,
        bool menuOpen,
        bool imeComposing = false)
    {
        if (imeComposing || key == Key.ImeProcessed) return ComposerKeyAction.None;
        var shift = (modifiers & KeyModifiers.Shift) != 0;

        if (menuOpen && key == Key.Escape) return ComposerKeyAction.MenuClose;

        if (mentionVisible)
        {
            switch (key)
            {
                case Key.Down:
                    return ComposerKeyAction.MentionDown;
                case Key.Up:
                    return ComposerKeyAction.MentionUp;
                case Key.Enter when !shift:
                case Key.Tab:
                    return ComposerKeyAction.MentionPick;
            }
        }
        if (key == Key.Escape && mentionVisible) return ComposerKeyAction.MentionClose;

        if (key == Key.Escape && paletteOpen) return ComposerKeyAction.PaletteClose;

        if (paletteOpen && hasFilteredCommands)
        {
            switch (key)
            {
                case Key.Down:
                    return ComposerKeyAction.PaletteDown;
                case Key.Up:
                    return ComposerKeyAction.PaletteUp;
                case Key.Enter when !shift:
                    return ComposerKeyAction.PalettePick;
            }
        }

        if (key == Key.Enter && !shift) return ComposerKeyAction.Send;
        return ComposerKeyAction.None;
    }

    /// <summary>
    /// The <c>/</c> palette opens only while the whole text is a command word and no run is active
    /// (the web's <c>/^\/\S*$/</c> guard).
    /// </summary>
    public static bool PaletteAllowed(string text, bool isReplying) =>
        !isReplying && text.Length > 0 && text[0] == '/' && text.IndexOfAny([' ', '\t', '\n', '\r']) < 0;

    /// <summary>The palette filter: everything after the slash up to the first whitespace.</summary>
    public static string PaletteQuery(string text)
    {
        if (text.Length == 0 || text[0] != '/') return "";
        var rest = text[1..];
        var cut = rest.IndexOfAny([' ', '\t', '\n', '\r']);
        return cut < 0 ? rest : rest[..cut];
    }

    /// <summary>
    /// The <c>@</c> mention range: a <c>@word</c> that starts the text or follows whitespace, ending at the caret
    /// (the web's <c>/(^|\s)@([\w.\-/\\]*)$/</c>).
    /// </summary>
    public static (int Start, int End, string Query)? MentionRange(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length) return null;
        var head = text[..caret];
        var at = head.LastIndexOf('@');
        if (at < 0) return null;
        if (at > 0 && !char.IsWhiteSpace(head[at - 1])) return null;
        for (var i = at + 1; i < head.Length; i++)
        {
            var c = head[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '/' || c == '\\')) return null;
        }
        return (at, caret, head[(at + 1)..]);
    }
}
