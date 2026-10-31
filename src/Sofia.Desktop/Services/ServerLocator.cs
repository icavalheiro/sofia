using System.Diagnostics;
using Sofia.Server.Infrastructure;

namespace Sofia.Desktop.Services;

public static class ServerLocator
{
    /// <summary>
    /// Resolves a Sofia.Server binary that can actually serve the web UI.
    /// In dev this prefers the Release publish output (which ships wwwroot)
    /// over the Debug build copied next to the desktop binary, which has no UI.
    /// </summary>
    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("SOFIA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var exeName = OperatingSystem.IsWindows() ? "Sofia.Server.exe" : "Sofia.Server";

        // 1. Release publish output (dev): has wwwroot next to the exe.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var config in new[] { "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Sofia.Server", "bin", config, "net10.0", "publish", exeName);
                if (File.Exists(candidate) && HasUi(candidate)) return candidate;
            }
            dir = dir.Parent;
        }

        // 2. Next to the desktop binary (production packaging): wwwroot ships alongside.
        var baseExe = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(baseExe) && HasUi(baseExe)) return baseExe;

        // 3. Debug build (dev fallback): only if it can resolve the UI (e.g. via ../Sofia.UI/dist).
        dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Sofia.Server", "bin", config, "net10.0", exeName);
                if (File.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }

        // 4. Last resort: the copied binary next to the desktop, even without UI check.
        if (File.Exists(baseExe)) return baseExe;

        return null;
    }

    private static bool HasUi(string exePath)
    {
        var exeDir = Path.GetDirectoryName(exePath)!;
        if (File.Exists(Path.Combine(exeDir, "wwwroot", "index.html"))) return true;
        // Dev fallback: ../Sofia.UI/dist (when content root resolves to exe dir)
        if (File.Exists(Path.Combine(exeDir, "..", "Sofia.UI", "dist", "index.html"))) return true;
        return false;
    }
}