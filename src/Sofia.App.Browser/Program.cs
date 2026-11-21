using Avalonia;
using Avalonia.Browser;

namespace Sofia.App.Browser;

/// <summary>
/// WASM boot smoke (spec 039). Avalonia 12 configures the browser platform inside
/// <see cref="BrowserAppBuilder.StartBrowserAppAsync"/>, so <see cref="BuildAvaloniaApp"/> only creates the shared app.
/// The real browser host (static assets, backend over HTTP, sounds through WebAudio) is wired in spec 044.
/// </summary>
public static class Program
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::Sofia.App.App>();

    public static async Task Main(string[] args) => await BuildAvaloniaApp().StartBrowserAppAsync("out");
}
