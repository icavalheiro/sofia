using Avalonia;
using Avalonia.Browser;
using Sofia.App.Services;

namespace Sofia.App.Browser;

/// <summary>
/// WASM head (spec 044): boots the shared <c>Sofia.App</c> into the page, over <c>HttpSofiaBackend</c> against the
/// server that served the bundle. The head installs the four platform services the shared app cannot provide in a
/// browser — <c>localStorage</c> state, WebAudio playback, the reduced-motion media query and the HTTP resolver — and
/// hides the actions that act on the host (<see cref="HeadOptions.HostActions"/> = false).
/// </summary>
public static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Sofia.App.App>()
            // WASM has no OS font stack: the Inter files ship inside Avalonia.Fonts.Inter and become the default family
            // (documented substitution deviation: glyph shapes differ from Segoe UI / SF Pro, metrics keep the shell).
            .WithInterFont();

    public static async Task Main(string[] args)
    {
        // The WASM SDK bakes the reflection switch off; the shared app serializes with reflection-based Json.Options.
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);
        var options = HeadOptions.Current;
        options.HostActions = false;
        options.DefaultServerUrl = BrowserInterop.SafeOrigin();
        options.SoundPlayer = new WebAudioSoundPlayer();
        options.Resolver = new BrowserBackendResolver();
        options.ReducedMotionOverride = TryPrefersReducedMotion();

        LocalState.StorageFactory = () => new BrowserLocalStateStorage();
        BrowserInterop.WatchReducedMotion();

        await BuildAvaloniaApp().StartBrowserAppAsync("out", new BrowserPlatformOptions
        {
            // WebGL2, then WebGL1, then the CPU rasteriser: never assume a GPU (Software2D keeps every animation's
            // duration, only its frame pacing suffers — documented limitation).
            RenderingMode =
            [
                BrowserRenderingMode.WebGL2,
                BrowserRenderingMode.WebGL1,
                BrowserRenderingMode.Software2D,
            ],
        });
    }

    private static bool TryPrefersReducedMotion()
    {
        try
        {
            return BrowserInterop.PrefersReducedMotion();
        }
        catch
        {
            return false;
        }
    }
}
