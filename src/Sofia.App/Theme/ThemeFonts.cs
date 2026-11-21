using Avalonia.Media;

namespace Sofia.App.Theme;

/// <summary>
/// Platform UI font resolution. The web uses a CSS stack (<c>-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto…</c>);
/// Avalonia resolves fallbacks itself, but the default family must be named per OS so Segoe UI / SF Pro / Cantarell are
/// picked without bundling Inter (Avalonia.Fonts.Inter is deliberately absent: the web never used it).
/// </summary>
public static class ThemeFonts
{
    public const string MonoStack = "SFMono-Regular, Menlo, Consolas, Liberation Mono, monospace";
    public const string UiStack = "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif";

    /// <summary>Default family of the current OS, in the same order the CSS stack resolves it.</summary>
    public static string PlatformDefaultFamily
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "Segoe UI";
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) return "SF Pro Text";
            if (OperatingSystem.IsAndroid()) return "Roboto";
            return "Cantarell, DejaVu Sans, Liberation Sans, Ubuntu, Noto Sans";
        }
    }
}
