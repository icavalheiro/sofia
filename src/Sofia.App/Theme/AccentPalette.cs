using Avalonia.Media;

namespace Sofia.App.Theme;

/// <summary>
/// Accent derivation ported from <c>src/Sofia.UI/src/composables/useAppearance.ts</c>: <c>strong</c> is the base colour
/// lerped 12% toward white channel-wise, <c>ink</c> is the dark canvas when the relative luminance is above .55 (else
/// white), and the two softs are the base colour at .16 / .26 alpha.
/// </summary>
public static class AccentPalette
{
    public const string Brown = "#a9744f";
    public const string Beige = "#c9b489";
    public const string Slate = "#7b7b84";

    /// <summary>Colour used when the user picks "custom" without choosing one yet (the web default).</summary>
    public const string CustomDefault = "#7c9cff";

    /// <summary>Accessibility/setting token: <c>brown</c> (default), <c>beige</c>, <c>slate</c> or <c>custom</c>.</summary>
    public static readonly string[] Presets = ["brown", "beige", "slate", "custom"];

    /// <summary>Resolves a preset name (or <c>custom</c> + a hex) to the base colour the app should use.</summary>
    public static string Resolve(string? preset, string? customHex) => preset switch
    {
        "beige" => Beige,
        "slate" => Slate,
        "custom" => IsValidHex(customHex) ? customHex! : CustomDefault,
        _ => Brown,
    };

    public static bool IsValidHex(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && hex.Length is 4 or 7 && hex[0] == '#' &&
        IsHex(hex.AsSpan(1));

    private static bool IsHex(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>One derived accent: the five brushes the CSS exposes as <c>--sofia-primary*</c>.</summary>
    public sealed record Accent(Color Base, Color Strong, Color Ink, Color Soft, Color SoftStrong);

    public static Accent Derive(string hex)
    {
        if (!IsValidHex(hex)) hex = CustomDefault;
        var clean = hex[1..];
        var full = clean.Length == 3 ? string.Concat(clean.Select(c => new string(c, 2))) : clean;
        var value = Convert.ToUInt32(full, 16);
        var r = (byte)((value >> 16) & 255);
        var g = (byte)((value >> 8) & 255);
        var b = (byte)(value & 255);

        byte Lighten(byte channel) => (byte)Math.Round(channel + (255 - channel) * 0.12);
        var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        var ink = luminance > 0.55 ? Color.Parse("#171719") : Colors.White;

        return new Accent(
            Color.FromRgb(r, g, b),
            Color.FromRgb(Lighten(r), Lighten(g), Lighten(b)),
            ink,
            Color.FromArgb((byte)Math.Round(0.16 * 255), r, g, b),
            Color.FromArgb((byte)Math.Round(0.26 * 255), r, g, b));
    }
}
