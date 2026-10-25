using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sofia.Server.Data;

namespace Sofia.Server.Infrastructure;

/// <summary>
/// Keeps the display (and the machine) awake while an agent is working, when enabled in Settings.
/// Windows uses SetThreadExecutionState; macOS runs <c>caffeinate</c>; Linux holds a systemd inhibitor
/// (best effort: other desktops may ignore it).
/// </summary>
public sealed class KeepAwakeService(SofiaDb db, ILogger<KeepAwakeService> log) : IDisposable
{
    private readonly object _gate = new();
    private bool _busy;
    private IDisposable? _hold;

    /// <summary>True while an inhibitor is actually held.</summary>
    public bool Holding
    {
        get
        {
            lock (_gate) return _hold is not null;
        }
    }

    /// <summary>Called when the number of active runs goes between zero and non-zero.</summary>
    public void SetBusy(bool busy)
    {
        lock (_gate)
        {
            _busy = busy;
            Apply();
        }
    }

    /// <summary>Re-evaluates after the setting changed.</summary>
    public void Refresh()
    {
        lock (_gate) Apply();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _busy = false;
            Apply();
        }
    }

    private void Apply()
    {
        var wanted = _busy && db.GetConfig().KeepScreenAwake;
        if (wanted && _hold is null)
        {
            _hold = Acquire();
        }
        else if (!wanted && _hold is not null)
        {
            _hold.Dispose();
            _hold = null;
        }
    }

    private IDisposable? Acquire()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new WindowsHold();
            var pid = Environment.ProcessId.ToString();
            if (OperatingSystem.IsMacOS()) return ProcessHold.Start("caffeinate", "-d", "-i", "-w", pid);
            // `tail --pid` exits with the server, so the inhibitor never outlives it.
            return ProcessHold.Start("systemd-inhibit", "--what=idle:sleep", "--who=Sofia", "--why=An agent is working",
                "--mode=block", "tail", $"--pid={pid}", "-f", "/dev/null");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not keep the screen awake");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsHold : IDisposable
    {
        private const uint Continuous = 0x80000000;
        private const uint SystemRequired = 0x00000001;
        private const uint DisplayRequired = 0x00000002;

        private readonly ManualResetEventSlim _release = new(false);

        // The execution state belongs to the calling thread, so one thread sets it and clears it again.
        public WindowsHold() => new Thread(() =>
        {
            SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired);
            _release.Wait();
            SetThreadExecutionState(Continuous);
        })
        { IsBackground = true, Name = "sofia-keep-awake" }.Start();

        public void Dispose() => _release.Set();

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
    }

    private sealed class ProcessHold(Process process) : IDisposable
    {
        public static ProcessHold Start(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return new ProcessHold(Process.Start(psi) ?? throw new InvalidOperationException($"{file} did not start."));
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            process.Dispose();
        }
    }
}
