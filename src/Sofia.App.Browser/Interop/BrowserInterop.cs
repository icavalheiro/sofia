using System.Runtime.InteropServices.JavaScript;
using Sofia.App.Services;

namespace Sofia.App.Browser;

/// <summary>
/// The browser head's one interop surface (spec 044). Every function is implemented by <c>wwwroot/main.js</c> and
/// registered as the <c>sofia</c> module before the managed entry point runs, so the calls below are safe from the
/// first line of <see cref="Program.Main"/>.
/// </summary>
/// <remarks>
/// Only what the browser genuinely lacks goes through here: the page origin, <c>localStorage</c>, WebAudio playback of
/// the pre-rendered WAVs and the <c>prefers-reduced-motion</c> media query. Clipboard, file pickers and blob downloads
/// are provided by the Avalonia browser platform itself (<c>TopLevel.Clipboard</c>, <c>StorageProvider</c>), so the
/// shared views keep using those APIs unchanged.
/// </remarks>
internal static partial class BrowserInterop
{
    public const string Module = "sofia";

    [JSImport("origin", Module)]
    internal static partial string Origin();

    [JSImport("storageGet", Module)]
    internal static partial string? StorageGet(string key);

    [JSImport("storageSet", Module)]
    internal static partial void StorageSet(string key, string value);

    [JSImport("playWav", Module)]
    internal static partial void PlayWav(string base64, double gain);

    [JSImport("prefersReducedMotion", Module)]
    internal static partial bool PrefersReducedMotion();

    [JSImport("watchReducedMotion", Module)]
    internal static partial void WatchReducedMotion();

    /// <summary>Called from <c>main.js</c> when the user changes the OS reduced-motion setting while the app is open.</summary>
    [JSExport]
    public static void OnReducedMotionChanged(bool reduced) => MotionService.Current.ReducedMotion = reduced;

    /// <summary>Page origin, or an empty string when the interop module is not available (native unit tests).</summary>
    internal static string SafeOrigin()
    {
        try
        {
            return Origin();
        }
        catch
        {
            return "";
        }
    }
}
