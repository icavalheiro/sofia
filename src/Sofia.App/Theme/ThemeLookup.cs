using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sofia.App.Theme;

/// <summary>
/// Typed access to the resource dictionary chain (<c>Application.Current.Resources</c>). Components must never
/// hardcode a colour or a size: they resolve a brush, a double or a shadow through here and fall back to the
/// documented default only when no Application exists (unit tests constructing a control directly).
/// </summary>
public static class ThemeLookup
{
    public static object? Find(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    public static IBrush Brush(string key)
    {
        if (Find(key) is IBrush brush) return brush;
        // Literal colours are accepted for the two places the web hardcodes white on a tinted fill (chosen question
        // options, carousel confirm) — never for a themed surface. Parsed by hand so the result is deterministic in
        // every headless/test host instead of depending on a parser's ambient culture.
        if (key.StartsWith('#') && key.Length is 7 or 9 &&
            uint.TryParse(key.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return new SolidColorBrush(key.Length == 7
                ? Avalonia.Media.Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value)
                : Avalonia.Media.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        }
        return Brushes.Transparent;
    }

    /// <summary>A colour from the <c>Color*</c> keys, or transparent when the dictionary is not loaded.</summary>
    public static Color Color(string key) => Find(key) is Color color ? color : Colors.Transparent;

    /// <summary>The current value of a scaled token (<c>FontSizeMd</c>, <c>PadRow</c>, …).</summary>
    public static double Double(string key, double fallback = 14) => Find(key) is double value ? value : fallback;

    public static BoxShadows BoxShadow(string key) => Find(key) is BoxShadows shadows ? shadows : default;

    /// <summary>Builds a solid brush from a <c>Color*</c> key (for computed tints such as the diff rows).</summary>
    public static IBrush Solid(string colorKey) => new SolidColorBrush(Color(colorKey));

    /// <summary>The colour of a <c>Color*</c> key with a CSS-like alpha (<c>color-mix(… N%, transparent)</c>).</summary>
    public static IBrush WithAlpha(string colorKey, double alpha)
    {
        var color = Color(colorKey);
        return new SolidColorBrush(new Color((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), color.R, color.G, color.B));
    }
}
