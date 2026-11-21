using System.Diagnostics;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Services;

/// <summary>
/// Opens a folder or file in the OS (Explorer/Finder/xdg-open), in a terminal, or in VS Code. Only meaningful for a
/// host running on the user's machine; the browser head simply never offers it.
/// </summary>
public sealed class HostLauncher
{
    /// <summary>Runs the platform opener for <paramref name="path"/>; throws <see cref="ArgumentException"/> when it cannot.</summary>
    public void Open(string path, string? app)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathRooted(path))
            throw new ArgumentException("An absolute path is required.");
        var target = Path.GetFullPath(path);
        if (!Directory.Exists(target) && !File.Exists(target)) throw new ArgumentException($"Path not found: {target}");
        if (app == "terminal")
        {
            NativeTerminal.Open(target);
            return;
        }
        // Arguments go through ArgumentList (no shell); VS Code on Windows is launched via its code.cmd shim, never `cmd /c`.
        var codeExe = app == "vscode" ? ProcessRunner.FindOnPath(OperatingSystem.IsWindows() ? "code.cmd" : "code") : null;
        if (app == "vscode" && codeExe is null) throw new ArgumentException("VS Code ('code') was not found on PATH.");
        var psi = app == "vscode" ? new ProcessStartInfo(codeExe!, ["--", target])
            : OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", [target])
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [target])
            : new ProcessStartInfo("xdg-open", [target]);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        Process.Start(psi)?.Dispose();
    }
}
