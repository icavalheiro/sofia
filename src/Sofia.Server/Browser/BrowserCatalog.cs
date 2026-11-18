using System.Diagnostics;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Browser;

/// <summary>An installed browser Sofia can drive: <c>Kind</c> is the automation family ("chromium" or "firefox").</summary>
public sealed record BrowserEngine(string Id, string Kind, string Path, string? Version = null)
{
    public bool IsChromium => Kind == BrowserEngine.ChromiumKind;
    public const string ChromiumKind = "chromium";
    public const string FirefoxKind = "firefox";
}

/// <summary>
/// Finds the browsers already installed on this machine, so Sofia never ships or downloads one. The ids are stable
/// and also used on the wire (settings, tool parameter): chrome, edge, brave, vivaldi, opera, chromium, firefox.
/// </summary>
public static class BrowserCatalog
{
    /// <summary>"Let Sofia pick": the first installed Chromium-family engine, else Firefox.</summary>
    public const string Auto = "auto";

    /// <summary>Canonical order: the Chromium family first (Chrome before everything), Firefox last.</summary>
    public static readonly string[] Ids = ["chrome", "edge", "brave", "vivaldi", "opera", "chromium", "firefox"];

    /// <summary>Environment override per engine, e.g. <c>SOFIA_BROWSER_CHROME=C:\tools\chrome.exe</c>.</summary>
    public static string OverrideVariable(string id) => $"SOFIA_BROWSER_{id.ToUpperInvariant()}";

    /// <summary>Engines installed on this machine, in <see cref="Ids"/> order.</summary>
    public static IReadOnlyList<BrowserEngine> Detect() =>
        Detect(Candidates(), id => Environment.GetEnvironmentVariable(OverrideVariable(id)));

    /// <summary>Testable detection: candidates per engine id, optional env overrides and file-existence probe.</summary>
    public static IReadOnlyList<BrowserEngine> Detect(
        IReadOnlyDictionary<string, IReadOnlyList<string>> candidates,
        Func<string, string?>? env = null,
        Func<string, bool>? exists = null,
        Func<string, string?>? version = null)
    {
        exists ??= File.Exists;
        version ??= VersionOf;
        var found = new List<BrowserEngine>();
        foreach (var id in Ids)
        {
            var kind = id == "firefox" ? BrowserEngine.FirefoxKind : BrowserEngine.ChromiumKind;
            var overridden = env?.Invoke(id)?.Trim();
            if (!string.IsNullOrEmpty(overridden))
            {
                // An explicit override wins over detection, but only when it points at a real file.
                if (exists(overridden)) found.Add(new BrowserEngine(id, kind, overridden, version(overridden)));
                continue;
            }
            if (!candidates.TryGetValue(id, out var paths)) continue;
            var path = paths.FirstOrDefault(exists);
            if (path is not null) found.Add(new BrowserEngine(id, kind, path, version(path)));
        }
        return found;
    }

    /// <summary>True when the id names an engine Sofia knows (not <see cref="Auto"/>).</summary>
    public static bool IsKnown(string? id) => Normalize(id) is { } normalized && Ids.Contains(normalized);

    /// <summary>
    /// Resolves what the user (or the agent) asked for to an installed engine. <c>auto</c> prefers the Chromium
    /// family; an unknown or missing engine fails with a message that lists what is installed.
    /// </summary>
    public static BrowserEngine Resolve(string? requested, IReadOnlyList<BrowserEngine> installed)
    {
        var normalized = Normalize(requested);
        if (normalized is null or Auto)
        {
            return installed.FirstOrDefault(e => e.IsChromium)
                ?? installed.FirstOrDefault()
                ?? throw new InvalidOperationException(MissingMessage(requested));
        }
        var match = installed.FirstOrDefault(e => e.Id == normalized);
        if (match is not null) return match;
        if (!Ids.Contains(normalized))
            throw new InvalidOperationException($"Unknown browser '{requested}'. Use one of: auto, {string.Join(", ", Ids)}.");
        var available = installed.Count > 0 ? string.Join(", ", installed.Select(e => e.Id)) : "none";
        throw new InvalidOperationException(
            $"'{normalized}' is not installed (installed browsers: {available}). Install it, set {OverrideVariable(normalized)}, or use another browser.");
    }

    /// <summary>Message used when nothing usable is installed at all.</summary>
    public static string MissingMessage(string? requested) =>
        $"No supported browser found for '{(string.IsNullOrWhiteSpace(requested) ? Auto : requested)}'. " +
        "Install Chrome, Edge, Brave, Vivaldi or Firefox, or point " + OverrideVariable("CHROME") + " at an executable.";

    /// <summary>Aliases and casing from the settings/tool surface, mapped to a catalog id.</summary>
    private static string? Normalize(string? id)
    {
        var text = id?.Trim().ToLowerInvariant();
        return text switch
        {
            null or "" or Auto => Auto,
            "google-chrome" or "googlechrome" or "chrome-stable" => "chrome",
            "msedge" or "microsoft-edge" or "edge-stable" => "edge",
            "brave-browser" or "bravebrowser" => "brave",
            "vivaldi-stable" => "vivaldi",
            "opera-stable" or "opera-gx" => "opera",
            "chromium-browser" or "chrome-browser" => "chromium",
            "moz-firefox" or "firefox-esr" or "mozilla-firefox" => "firefox",
            _ => text,
        };
    }

    /// <summary>Files an installed engine may live at on this OS, per engine id.</summary>
    private static Dictionary<string, IReadOnlyList<string>> Candidates()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            map["chrome"] = Join(
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
            map["edge"] = Join(
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
            map["brave"] = Join(
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            map["vivaldi"] = Join(
                Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(programFiles, "Vivaldi", "Application", "Vivaldi.exe"));
            map["opera"] = Join(
                Path.Combine(local, "Programs", "Opera", "opera.exe"),
                Path.Combine(roaming, "Programs", "Opera", "opera.exe"),
                Path.Combine(programFiles, "Opera", "opera.exe"));
            map["chromium"] = Join(
                Path.Combine(programFiles, "Chromium", "Application", "chrome.exe"),
                Path.Combine(local, "Chromium", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Chromium", "chrome.exe"));
            map["firefox"] = Join(
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(local, "Mozilla Firefox", "firefox.exe"));
            // A portable/extracted build on PATH is still a legitimate install.
            AddPathFallbacks(map, "chrome", "chrome.exe");
            AddPathFallbacks(map, "edge", "msedge.exe");
            AddPathFallbacks(map, "brave", "brave.exe");
            AddPathFallbacks(map, "vivaldi", "vivaldi.exe");
            AddPathFallbacks(map, "opera", "opera.exe");
            AddPathFallbacks(map, "chromium", "chromium.exe");
            AddPathFallbacks(map, "firefox", "firefox.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            const string apps = "/Applications";
            map["chrome"] = Join(
                $"{apps}/Google Chrome.app/Contents/MacOS/Google Chrome",
                $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            map["edge"] = Join($"{apps}/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            map["brave"] = Join($"{apps}/Brave Browser.app/Contents/MacOS/Brave Browser");
            map["vivaldi"] = Join($"{apps}/Vivaldi.app/Contents/MacOS/Vivaldi");
            map["opera"] = Join($"{apps}/Opera.app/Contents/MacOS/Opera");
            map["chromium"] = Join($"{apps}/Chromium.app/Contents/MacOS/Chromium");
            map["firefox"] = Join($"{apps}/Firefox.app/Contents/MacOS/firefox");
        }
        else
        {
            // Linux and other Unix-likes: PATH first (distro binaries are almost always there).
            AddPathFallbacks(map, "chrome", "google-chrome", "google-chrome-stable");
            AddPathFallbacks(map, "edge", "microsoft-edge", "microsoft-edge-stable");
            AddPathFallbacks(map, "brave", "brave-browser", "brave");
            AddPathFallbacks(map, "vivaldi", "vivaldi", "vivaldi-stable");
            AddPathFallbacks(map, "opera", "opera");
            AddPathFallbacks(map, "chromium", "chromium", "chromium-browser");
            AddPathFallbacks(map, "firefox", "firefox");
        }
        return map;

        static IReadOnlyList<string> Join(params string[] paths) => paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        static void AddPathFallbacks(Dictionary<string, IReadOnlyList<string>> map, string id, params string[] names)
        {
            var resolved = names.Select(ProcessRunner.FindOnPath).Where(p => p is not null).Cast<string>().ToList();
            if (resolved.Count == 0) return;
            map[id] = map.TryGetValue(id, out var existing) ? [.. existing, .. resolved] : resolved;
        }
    }

    /// <summary>File version of the executable, best effort (a portable build may report nothing).</summary>
    private static string? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
