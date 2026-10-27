using System.Diagnostics;

namespace Sofia.Server.Infrastructure;

/// <summary>Opens the operating system's own terminal emulator in a directory (Windows, macOS, Linux).</summary>
public static class NativeTerminal
{
    // Linux emulators and the arguments that set their working directory.
    private static readonly (string Exe, Func<string, string[]> Args)[] LinuxTerminals =
    [
        ("x-terminal-emulator", _ => []),
        ("gnome-terminal", d => [$"--working-directory={d}"]),
        ("konsole", d => ["--workdir", d]),
        ("xfce4-terminal", d => [$"--working-directory={d}"]),
        ("mate-terminal", d => [$"--working-directory={d}"]),
        ("kitty", d => ["--directory", d]),
        ("alacritty", d => ["--working-directory", d]),
        ("wezterm", d => ["start", "--cwd", d]),
        ("tilix", d => [$"--working-directory={d}"]),
        ("xterm", _ => []),
    ];

    /// <summary>Launches a terminal in <paramref name="path"/> (or its folder when it is a file).</summary>
    public static void Open(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new ArgumentException($"Folder not found: {path}");

        var psi = Build(dir) ?? throw new ArgumentException("No terminal emulator was found on this machine.");
        psi.UseShellExecute = false;
        psi.WorkingDirectory = dir;
        Process.Start(psi)?.Dispose();
    }

    private static ProcessStartInfo? Build(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            // `start` gives the console shell its own window; Windows Terminal is a GUI app and starts directly.
            if (ProcessRunner.FindOnPath("wt.exe") is { } wt) return new ProcessStartInfo(wt, ["-d", dir]);
            var shell = ProcessRunner.FindOnPath("pwsh.exe") ?? "powershell.exe";
            return new ProcessStartInfo("cmd.exe", ["/c", "start", "", shell, "-NoExit"]);
        }

        if (OperatingSystem.IsMacOS()) return new ProcessStartInfo("open", ["-a", "Terminal", dir]);

        var preferred = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrWhiteSpace(preferred) && ProcessRunner.FindOnPath(preferred) is { } custom)
            return new ProcessStartInfo(custom);

        foreach (var (exe, args) in LinuxTerminals)
        {
            if (ProcessRunner.FindOnPath(exe) is not { } found) continue;
            var psi = new ProcessStartInfo(found);
            foreach (var arg in args(dir)) psi.ArgumentList.Add(arg);
            return psi;
        }
        return null;
    }
}
