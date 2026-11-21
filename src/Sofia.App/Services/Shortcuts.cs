using Avalonia.Input;

namespace Sofia.App.Services;

/// <summary>
/// The web's shortcut table (<c>useShortcuts.ts</c>) reduced to what the shell and the sidebar need. One registry for
/// the whole app: feature views register a handler by id and the shell routes a key press through it, so a shortcut
/// never depends on which control happens to have focus. Specs 041-044 add their ids here instead of parsing keys
/// themselves.
/// </summary>
public sealed class ShortcutService
{
    public sealed record Binding(string Id, Key Key, KeyModifiers Modifiers, string Combo, string Group);

    public static ShortcutService Current { get; } = new();

    private static readonly Binding[] Table =
    [
        new("session.new", Key.N, KeyModifiers.Control, "Ctrl+N", "session"),
        new("session.archive", Key.E, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+E", "session"),
        new("session.prev", Key.Up, KeyModifiers.Alt, "Alt+ArrowUp", "navigation"),
        new("session.next", Key.Down, KeyModifiers.Alt, "Alt+ArrowDown", "navigation"),
        new("workspace.add", Key.O, KeyModifiers.Control, "Ctrl+O", "navigation"),
        new("search", Key.K, KeyModifiers.Control, "Ctrl+K", "navigation"),
        new("panel.toggle", Key.OemBackslash, KeyModifiers.Control, @"Ctrl+\", "navigation"),
        new("settings.open", Key.OemComma, KeyModifiers.Control, "Ctrl+,", "app"),
    ];

    private readonly Dictionary<string, Func<Task>?> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyList<Binding> Bindings => Table;

    public static Binding? BindingOf(string id) => Table.FirstOrDefault(b => b.Id == id);

    /// <summary>Registers (or replaces) the handler for a shortcut id.</summary>
    public void Register(string id, Func<Task>? handler) => _handlers[id] = handler;

    public void Register(string id, Action handler) => Register(id, () =>
    {
        handler();
        return Task.CompletedTask;
    });

    /// <summary>Runs the handler matching the key press; returns true when the shortcut was consumed.</summary>
    public bool TryHandle(KeyEventArgs args)
    {
        var key = args.Key;
        foreach (var binding in Table)
        {
            if (binding.Key != key || Normalize(binding.Modifiers) != Normalize(args.KeyModifiers)) continue;
            if (!_handlers.TryGetValue(binding.Id, out var handler) || handler is null) return false;
            _ = handler();
            args.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>Only Shift/Control/Alt/Meta count; CapsLock and NumLock never change a binding.</summary>
    private static KeyModifiers Normalize(KeyModifiers modifiers) =>
        modifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
}
