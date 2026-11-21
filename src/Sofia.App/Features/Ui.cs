using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Sofia.App.Controls;
using Sofia.App.Services;

namespace Sofia.App.Features;

/// <summary>
/// Shared builders for the in-code views (header, panels, settings): the web's row/surface vocabulary resolved against
/// the app's dynamic tokens, so every feature keeps the same brushes, radii and typography instead of re-declaring them.
/// </summary>
internal static class Ui
{
    public static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    public static double Font(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is double size ? size : 12;

    public static TextBlock Text(string text, double size = 12, string brush = "BrushTextMuted", FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Brush(brush),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight is { } value) block.FontWeight = value;
        return block;
    }

    /// <summary>The web's `.sofia-truncate`: ellipsis instead of wrapping, and free to shrink inside a flex row.</summary>
    public static TextBlock Truncate(string text, double size = 12, string brush = "BrushTextMuted", FontWeight? weight = null)
    {
        var block = Text(text, size, brush, weight);
        block.Classes.Add("truncate");
        return block;
    }

    /// <summary>A muted upper-case section label (`.rp-section-label`, 10px/600 uppercase).</summary>
    public static TextBlock SectionLabel(string text) =>
        new()
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Foreground = Brush("BrushTextFaint"),
            Margin = new Thickness(0, 0, 0, 8),
        };

    public static SofiaIcon Icon(string glyph, double size = 16, string brush = "BrushTextMuted")
    {
        var icon = new SofiaIcon { Glyph = glyph, Size = size };
        if (brush.Length > 0) icon.Foreground = Brush(brush);
        return icon;
    }

    /// <summary>Header panel toggle / ghost icon button of the web: 28 or 30 square, transparent until hover.</summary>
    public static Button IconButton(string glyph, double glyphSize, string tip, Action click, double size = 28)
    {
        var icon = Icon(glyph, glyphSize, "BrushTextMuted");
        var button = new Button
        {
            Theme = (ControlTheme?)Application.Current?.FindResource("IconButton"),
            Width = size,
            Height = size,
            Content = icon,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            click();
        };
        return button;
    }

    /// <summary>The web's `.panel-toggle`: 30 high, 8px radius, surface-2 fill, border-strong outline.</summary>
    public static Button PanelToggle(string glyph, double glyphSize, string tip, Action click)
    {
        var icon = Icon(glyph, glyphSize, "BrushTextMuted");
        var border = new Border
        {
            Child = icon,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5.6, 0),
            BorderThickness = new Thickness(1),
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            MinWidth = 30,
            Height = 30,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var button = new Button
        {
            Content = border,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MinWidth = 30,
            Height = 30,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            click();
        };
        button.Tag = icon;
        button.Classes.Add("panel-toggle");
        return button;
    }

    /// <summary>Highlights a <see cref="PanelToggle"/> in the web's active state (primary border + primary soft fill).</summary>
    public static void SetToggleActive(Button button, bool active)
    {
        if (button.Content is not Border border) return;
        border.BorderBrush = Brush(active ? "BrushPrimary" : "BrushBorderStrong");
        border.Background = Brush(active ? "BrushPrimarySoft" : "BrushSurface2");
        if (button.Tag is SofiaIcon icon) icon.Foreground = Brush(active ? "BrushPrimary" : "BrushTextMuted");
    }

    /// <summary>Menu surface (surface-2, border-strong, 12px radius, menu shadow) used by every popover in the shell.</summary>
    public static Border MenuSurface(double width = 240, double padding = 8.8) =>
        new()
        {
            Width = width,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(12),
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            BoxShadow = (BoxShadows?)Application.Current?.FindResource("ShadowMenu") ?? default,
        };

    /// <summary>A menu row: icon + label, 8px radius, surface-2 on hover (`.sess-item`, `.lang-item`).</summary>
    public static Button MenuRow(Control content, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Theme = (ControlTheme?)Application.Current?.FindResource("ComposerMenuRow"),
            Content = content,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Row of icon + text for a menu entry.</summary>
    public static StackPanel MenuRowContent(string glyph, string label, string brush = "BrushText", double iconBrush = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        row.Children.Add(Icon(glyph, 15, iconBrush > 0 ? (iconBrush >= 1 ? "BrushPrimary" : "BrushTextMuted") : "BrushTextMuted"));
        var text = Text(label, Font("FontSizeSm2"), brush);
        row.Children.Add(text);
        return row;
    }

    /// <summary>The web's small bordered button (`.mini-btn`): 8px radius, border-strong, 12px. </summary>
    public static Button MiniButton(string label, Action click, string glyph = "", bool primary = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        if (glyph.Length > 0) content.Children.Add(Icon(glyph, 13, primary ? "BrushPrimary" : "BrushTextMuted"));
        content.Children.Add(Text(label, Font("FontSizeSm"), primary ? "BrushPrimary" : "BrushTextMuted", FontWeight.Medium));
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(10.4, 4.8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(primary ? "BrushPrimary" : "BrushBorderStrong"),
            Background = Brush(primary ? "BrushSurface" : "BrushSurface2"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            click();
        };
        return button;
    }

    /// <summary>A one-line text field with the web's `.field-input` well (surface-2, border, 8px radius).</summary>
    public static TextBox Field(string? text, string placeholder = "", bool password = false)
    {
        var box = new TextBox
        {
            Text = text ?? "",
            PlaceholderText = placeholder,
            PasswordChar = password ? '•' : '\0',
            FontSize = Font("FontSizeSm2"),
            Padding = new Thickness(8, 6.4),
            CornerRadius = new CornerRadius(8),
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            Foreground = Brush("BrushText"),
        };
        return box;
    }

    /// <summary>Native-looking segmented control (openai / anthropic protocol, sm/md/lg text size).</summary>
    public static StackPanel Segment(IReadOnlyList<(string Id, string Label)> options, string selected, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (id, label) in options)
        {
            var active = id == selected;
            var button = new Button
            {
                Content = Text(label, Font("FontSizeSm"), active ? "BrushPrimary" : "BrushTextMuted",
                    active ? FontWeight.SemiBold : FontWeight.Normal),
                Padding = new Thickness(10.4, 4.8),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = Brush(active ? "BrushPrimarySoft" : "BrushSurface2"),
            };
            var captured = id;
            button.Click += (_, _) => pick(captured);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>Status dot + label (`.status` in the settings cards).</summary>
    public static StackPanel StatusChip(string status, string label)
    {
        var brush = status switch
        {
            "connected" or "running" => "BrushOk",
            "error" => "BrushErr",
            "pending" => "BrushWarn",
            "missing" => "BrushWarn",
            _ => "BrushTextFaint",
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush(brush),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(Text(label, Font("FontSizeXs"), brush));
        return row;
    }

    /// <summary>A borderless horizontal rule in the border colour.</summary>
    public static Border Divider(double top = 0, double bottom = 0) => new()
    {
        Height = 1,
        Background = Brush("BrushBorder"),
        Margin = new Thickness(0, top, 0, bottom),
    };

    /// <summary>Wires a <see cref="ToggleSwitch"/> to a setter without the control owning the state twice.</summary>
    public static Controls.ToggleSwitch Switch(bool value, Action<bool> changed)
    {
        var toggle = new Controls.ToggleSwitch { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        toggle.PropertyChanged += (_, args) =>
        {
            if (args.Property != Controls.ToggleSwitch.IsCheckedProperty) return;
            changed(toggle.IsChecked);
        };
        return toggle;
    }

    public static string Localize(string key) => Localization.T(key);

    /// <summary>Status label the settings cards show for MCP/LSP/provider states.</summary>
    public static string StatusLabel(string status) => status switch
    {
        "connected" or "running" or "starting" => Localization.T("status.connected"),
        "error" => Localization.T("status.error"),
        "stopped" => Localization.T("status.stopped"),
        "pending" => Localization.T("status.pending"),
        "missing" => Localization.T("status.missing"),
        _ => Localization.T("status.disconnected"),
    };
}
