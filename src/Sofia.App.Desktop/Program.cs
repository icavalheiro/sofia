using Avalonia;
using Avalonia.Media;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var options = HeadOptions.Current;
        options.Screenshot = ScreenshotRequest.Parse(args);
        options.Fixture = options.Screenshot is not null || args.Contains("--fixture");
        options.SoundPlayer = new SoundFlowSoundPlayer();
        if (!options.Fixture) options.Resolver = new DesktopBackendResolver();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// No <c>WithInterFont()</c>: the web renders the platform UI stack, so the default family is resolved per OS
    /// (Segoe UI / SF Pro / Cantarell) and mono is a second resource (Typography.axaml).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Sofia.App.App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ThemeFonts.PlatformDefaultFamily })
            .LogToTrace();
}
