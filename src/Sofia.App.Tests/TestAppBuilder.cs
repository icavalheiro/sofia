using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Sofia.App.Tests;

// The theme, motion clock and sound services are process-wide singletons, so these UI tests must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Sofia.App.Tests;

/// <summary>
/// Headless Avalonia host for the UI tests: real drawing (so screenshots render), the platform UI font the app uses and
/// the same application instance the desktop head boots.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Sofia.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = "Segoe UI" });
}
