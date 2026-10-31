using System.Diagnostics;
using Sofia.Server.Infrastructure;

namespace Sofia.Desktop.Services;

public static class ServerLocator
{
    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("SOFIA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var exeName = OperatingSystem.IsWindows() ? "Sofia.Server.exe" : "Sofia.Server";
        var baseExe = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(baseExe)) return baseExe;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Sofia.Server", "bin", config, "net10.0", exeName);
                if (File.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
