using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sofia.App.Controls;
using Sofia.App.Theme;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Small construction helpers shared by the transcript rows and cards. Every size goes through <see cref="Rem"/> so the
/// web's rem values follow the user's font-size setting (root 14/16/18px), and every colour resolves through
/// <see cref="ThemeLookup"/> so an accent change repaints live.
/// </summary>
public static class TranscriptUi
{
    /// <summary>A CSS rem expressed in DIPs at the current root font size (16px at the md default).</summary>
    public static double Rem(double rem) => ThemeLookup.Double("FontSizeMd", 14) / 14.0 * 16.0 * rem;

    public static TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = ThemeLookup.Brush(brush),
        FontWeight = weight ?? FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    public static TextBlock Mono(string text, double size, string brush, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = ThemeLookup.Find("FontFamilyMono") as FontFamily,
        Foreground = ThemeLookup.Brush(brush),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    /// <summary>The web's uppercase micro label (10px/600/.06em).</summary>
    public static TextBlock Label(string text, double size, string brush) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = size * 0.06,
        Foreground = ThemeLookup.Brush(brush),
        TextWrapping = TextWrapping.NoWrap,
    };

    public static SofiaIcon Icon(string glyph, double size, string brush) => new()
    {
        Glyph = glyph,
        Size = size,
        Foreground = ThemeLookup.Brush(brush),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Card(IBrush background, IBrush border, double radius, Thickness padding, Control child) => new()
    {
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        Child = child,
    };

    /// <summary>A web-like inline element (button or chip) with the given fill/border/text and no OS chrome.</summary>
    public static Button Pill(string text, double size, string foreground, IBrush? background = null,
        IBrush? border = null, Thickness? padding = null, double radius = 8, FontWeight? weight = null) => new()
    {
        Content = Text(text, size, foreground, weight),
        Background = background ?? Brushes.Transparent,
        BorderBrush = border ?? Brushes.Transparent,
        BorderThickness = border is null ? new Thickness(0) : new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding ?? new Thickness(Rem(0.3), Rem(0.1)),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
    };

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>Constrains a card to the web's 74ch measure (no ch unit in Avalonia: .5em per character).</summary>
    public static double CardWidth => Rem(0.9375) * 0.5 * 74;

    /// <summary>Constrains assistant prose to the web's 104ch measure.</summary>
    public static double ProseWidth => Rem(0.9375) * 0.5 * 104;

    /// <summary>The pending tag of a steer/queued follow-up.</summary>
    public static string LocalizePending(string? delivery) =>
        Localization.T(delivery == "steer" ? "chat.pendingSteer" : "chat.pendingQueue");
}
