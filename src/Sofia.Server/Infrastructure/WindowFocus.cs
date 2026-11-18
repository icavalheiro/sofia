using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sofia.Server.Infrastructure;

/// <summary>
/// Brings a browser window Sofia started to the foreground. Playwright's <c>BringToFront</c> only activates the *tab*
/// inside the browser; when Sofia itself sits in a full-screen window the user would not see the freshly opened
/// browser at all, so the OS window is raised too. Best effort and Windows-only (elsewhere the window manager decides).
/// </summary>
public static class WindowFocus
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    /// <summary>Restores and focuses the process' main window; does nothing when it has none (headless) or is gone.</summary>
    public static void TryFocus(Process? process)
    {
        if (process is null || !OperatingSystem.IsWindows()) return;
        try
        {
            if (process.HasExited) return;
            var handle = process.MainWindowHandle;
            for (var attempt = 0; attempt < 10 && handle == IntPtr.Zero; attempt++)
            {
                Thread.Sleep(100);
                process.Refresh();
                handle = process.MainWindowHandle;
            }
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The window is gone or Windows refused the focus change; the caller does not depend on it.
        }
    }
}
