using Avalonia.Input;

namespace Sofia.App.Services;

/// <summary>
/// The web's shortcut table (<c>useShortcuts.ts</c>): fifteen actions with a default combo, user overrides persisted in
/// local state, conflict detection, per-binding and all-binding reset, browser-reserved warnings and one global route
/// for the whole app. Feature views register a handler by id; the shell asks <see cref="TryHandle"/> whether a key press
/// belongs to a binding, so a shortcut never depends on which control has focus.
/// </summary>
/// <remarks>
/// Combos use the web's canonical strings (<c>Ctrl+Shift+A</c>, <c>Alt+ArrowUp</c>, <c>Ctrl+\</c>, <c>Ctrl+.</c>).
/// A binding written for Ctrl also matches Cmd/Meta, exactly like the web, so the same defaults work on macOS.
/// </remarks>
public sealed class ShortcutService
{
    /// <summary>One action: default combo, group (settings section), icon and the web's label key.</summary>
    public sealed record Binding(string Id, string Combo, string Group, string Icon);

    /// <summary>Combos browsers reserve in regular tabs: a warning, never a hard block (the web only warns).</summary>
    private static readonly HashSet<string> BrowserReserved = new(StringComparer.Ordinal)
    {
        "Ctrl+N", "Ctrl+T", "Ctrl+W", "Ctrl+Shift+N", "Ctrl+Shift+T", "Ctrl+Shift+W",
        "Ctrl+Tab", "Ctrl+Shift+Tab", "Meta+N", "Meta+T", "Meta+W", "Meta+Shift+N",
    };

    /// <summary>The web's <c>SHORTCUTS</c> order, ids, groups and icons.</summary>
    public static readonly Binding[] Table =
    [
        new("agent.pick", "Ctrl+Shift+A", "composer", "bot"),
        new("model.pick", "Ctrl+M", "composer", "sparkles"),
        new("reasoning.pick", "Ctrl+B", "composer", "zap"),
        new("permission.pick", "Ctrl+P", "composer", "shield"),
        new("composer.focus", "Ctrl+L", "composer", "chat"),
        new("session.stop", "Ctrl+.", "composer", "stop"),
        new("session.new", "Ctrl+N", "session", "plus"),
        new("session.archive", "Ctrl+Shift+E", "session", "archive"),
        new("session.prev", "Alt+ArrowUp", "navigation", "chevronUp"),
        new("session.next", "Alt+ArrowDown", "navigation", "chevronDown"),
        new("workspace.add", "Ctrl+O", "navigation", "folder"),
        new("search", "Ctrl+K", "navigation", "search"),
        new("panel.toggle", @"Ctrl+\", "navigation", "panelRight"),
        new("settings.open", "Ctrl+,", "app", "gear"),
        new("shortcuts.show", "Ctrl+/", "app", "keyboard"),
    ];

    public static readonly string[] Groups = ["composer", "session", "navigation", "app"];

    public static ShortcutService Current { get; } = new();

    private readonly Dictionary<string, Func<Task>?> _handlers = new(StringComparer.Ordinal);

    /// <summary>Only user changes are stored (id → combo, '' = deliberately unbound), like the web.</summary>
    private readonly Dictionary<string, string> _overrides;

    private readonly LocalState _state;

    public ShortcutService(LocalState? state = null)
    {
        _state = state ?? LocalState.Current;
        _overrides = LoadOverrides();
    }

    /// <summary>Raised when a binding changed (the settings tab rebuilds its rows).</summary>
    public event Action? Changed;

    /// <summary>The Shortcuts tab is capturing a combo: nothing may fire.</summary>
    public bool Recording { get; set; }

    /// <summary>A modal (settings) owns the keyboard: the global route stays quiet.</summary>
    public bool Suspended { get; set; }

    public IReadOnlyList<Binding> Bindings => Table;

    public static Binding? BindingOf(string id) => Table.FirstOrDefault(b => b.Id == id);

    /// <summary>Effective combo of an action (override, else the default); '' when unbound.</summary>
    public string ComboOf(string id) =>
        _overrides.TryGetValue(id, out var value) ? value : BindingOf(id)?.Combo ?? "";

    public static string DefaultOf(string id) => BindingOf(id)?.Combo ?? "";

    public bool IsCustomized(string id) => _overrides.ContainsKey(id);

    /// <summary>The other action already using <paramref name="combo"/>, if any.</summary>
    public Binding? ConflictFor(string id, string combo)
    {
        if (string.IsNullOrEmpty(combo)) return null;
        return Table.FirstOrDefault(b => b.Id != id && ComboOf(b.Id) == combo);
    }

    public void SetBinding(string id, string combo)
    {
        if (combo == DefaultOf(id)) _overrides.Remove(id);
        else _overrides[id] = combo;
        Save();
    }

    public void ResetBinding(string id)
    {
        if (!_overrides.Remove(id)) return;
        Save();
    }

    public void ResetAllBindings()
    {
        if (_overrides.Count == 0) return;
        _overrides.Clear();
        Save();
    }

    /// <summary>Runs the handler attached to an action (e.g. focus the composer after a new session).</summary>
    public void Trigger(string id)
    {
        if (_handlers.TryGetValue(id, out var handler) && handler is not null) _ = handler();
    }

    /// <summary>Registers (or replaces) the handler for a shortcut id.</summary>
    public void Register(string id, Func<Task>? handler) => _handlers[id] = handler;

    public void Register(string id, Action handler) => Register(id, () =>
    {
        handler();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Runs the handler matching the key press; returns true when a binding consumed it (whether or not a handler is
    /// attached, so the key never falls through to the platform while the app owns it).
    /// </summary>
    public bool TryHandle(KeyEventArgs args)
    {
        if (Recording || Suspended) return false;
        var match = MatchFor(args.Key, args.KeyModifiers);
        if (match is null) return false;
        args.Handled = true;
        if (_handlers.TryGetValue(match.Id, out var handler) && handler is not null) _ = handler();
        return true;
    }

    /// <summary>The binding a key press maps to, or null (settings recorder and tests use it too).</summary>
    public Binding? MatchFor(Key key, KeyModifiers modifiers)
    {
        foreach (var binding in Table)
        {
            var combo = ComboOf(binding.Id);
            if (combo.Length == 0) continue;
            if (!TryParse(combo, out var expectedKey, out var expectedModifiers)) continue;
            if (key != expectedKey) continue;
            if (Normalize(modifiers) == Normalize(expectedModifiers)) return binding;
            // Ctrl and Cmd are interchangeable, like the web's matchShortcut.
            if (SwapCommand(expectedModifiers) is { } swapped && Normalize(modifiers) == Normalize(swapped))
                return binding;
        }
        return null;
    }

    /// <summary>
    /// Canonical combo of a key event (recorder): physical letters/digits/punctuation keep their glyph, modifiers are
    /// emitted Ctrl, Alt, Shift, Meta in the web's order.
    /// </summary>
    public static string? ComboFromKey(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        var name = KeyName(key);
        if (name is null) return null;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(name);
        return string.Join('+', parts);
    }

    /// <summary>A combo is acceptable when it cannot be confused with plain typing (needs Ctrl/Alt/Meta or F1-F24).</summary>
    public static bool IsValidCombo(string combo)
    {
        var parts = combo.Split('+');
        if (parts.Length < 2) return parts.Length == 1 && IsFunctionKey(parts[0]);
        var hasModifier = false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] is "Ctrl" or "Alt" or "Meta") hasModifier = true;
        }
        return hasModifier || IsFunctionKey(parts[^1]);
    }

    public static bool IsBrowserReserved(string combo) => BrowserReserved.Contains(combo);

    /// <summary>"Ctrl+Shift+N" → ["Ctrl", "Shift", "N"] for key-cap rendering.</summary>
    public static string[] ComboKeys(string combo) =>
        combo.EndsWith("++", StringComparison.Ordinal)
            ? [.. combo[..^2].Split('+'), "+"]
            : combo.Split('+');

    /// <summary>Parses a canonical combo into the Avalonia key and modifier set.</summary>
    public static bool TryParse(string combo, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrEmpty(combo)) return false;
        var parts = combo.EndsWith("++", StringComparison.Ordinal)
            ? [.. combo[..^2].Split('+'), "+"]
            : combo.Split('+');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i] switch
            {
                "Ctrl" => KeyModifiers.Control,
                "Alt" => KeyModifiers.Alt,
                "Shift" => KeyModifiers.Shift,
                "Meta" => KeyModifiers.Meta,
                _ => KeyModifiers.None,
            };
        }
        var name = parts[^1] switch
        {
            "ArrowUp" => nameof(Key.Up),
            "ArrowDown" => nameof(Key.Down),
            "ArrowLeft" => nameof(Key.Left),
            "ArrowRight" => nameof(Key.Right),
            "Space" => nameof(Key.Space),
            _ => parts[^1],
        };
        return Enum.TryParse(name, ignoreCase: false, out key) && key != Key.None;
    }

    private static bool IsFunctionKey(string name) =>
        name.Length is >= 2 and <= 3 && name[0] == 'F'
        && int.TryParse(name[1..], out var number) && number is >= 1 and <= 24;

    /// <summary>Only Shift/Control/Alt/Meta count; CapsLock and NumLock never change a binding.</summary>
    private static KeyModifiers Normalize(KeyModifiers modifiers) =>
        modifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);

    /// <summary>The same combo with Ctrl replaced by Meta (or the other way round), for the macOS equivalence.</summary>
    private static KeyModifiers? SwapCommand(KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control)) return (modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta;
        if (modifiers.HasFlag(KeyModifiers.Meta)) return (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control;
        return null;
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemBackslash => @"\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemTilde => "`",
        Key.Space => "Space",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Back => "Backspace",
        Key.None => null,
        _ => key.ToString(),
    };

    // --- persistence --------------------------------------------------------------------------------------

    private Dictionary<string, string> LoadOverrides()
    {
        var stored = _state.Get<Dictionary<string, string>>(LocalState.ShortcutsKey);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stored is null) return result;
        foreach (var binding in Table)
        {
            if (stored.TryGetValue(binding.Id, out var combo) && combo is not null) result[binding.Id] = combo;
        }
        return result;
    }

    private void Save()
    {
        _state.Set(LocalState.ShortcutsKey, new Dictionary<string, string>(_overrides, StringComparer.Ordinal));
        Changed?.Invoke();
    }
}
