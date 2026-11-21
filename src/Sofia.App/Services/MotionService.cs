using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Sofia.App.Services;

/// <summary>Anything that animates from the single shared clock (motion contract: one clock, no timer per animation).</summary>
public interface IMotionParticipant
{
    /// <summary>Called on the UI thread once per rendered frame with the shared clock reading, in milliseconds.</summary>
    void TickMotion(double nowMs);
}

/// <summary>
/// The one clock every animation reads. In a running app a single 60 Hz <see cref="DispatcherTimer"/> ticks the
/// participants; in the screenshot/frame harness <see cref="EnableManualClock"/> turns it into a manual clock that only
/// advances when the test calls <see cref="Advance"/>, which is what makes stop-motion captures reproducible.
/// </summary>
public sealed class MotionService
{
    private readonly List<IMotionParticipant> _participants = [];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private double _manualNow;
    private bool _reducedMotion;

    public static MotionService Current { get; } = new();

    /// <summary>True while the harness drives time by hand.</summary>
    public bool IsManual { get; private set; }

    /// <summary>OS "reduce motion" setting (see <see cref="ReducedMotionDetector"/>); mirrors the web's three exceptions.</summary>
    public bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (_reducedMotion == value) return;
            _reducedMotion = value;
            if (!IsManual) Tick();
        }
    }

    public double NowMs => IsManual ? _manualNow : _stopwatch.Elapsed.TotalMilliseconds;

    public int ParticipantCount => _participants.Count;

    public void Attach(IMotionParticipant participant)
    {
        if (_participants.Contains(participant)) return;
        _participants.Add(participant);
        if (!IsManual) EnsureTimer();
    }

    public void Detach(IMotionParticipant participant) => _participants.Remove(participant);

    /// <summary>Switches to the deterministic clock used by the frame-sequence harness.</summary>
    public void EnableManualClock(double startMs = 0)
    {
        IsManual = true;
        _manualNow = startMs;
        _timer?.Stop();
        _timer = null;
        Tick();
    }

    /// <summary>Moves the manual clock and repaints every participant (no-op when the real clock is running).</summary>
    public void Advance(double deltaMs)
    {
        if (!IsManual) return;
        _manualNow += deltaMs;
        Tick();
    }

    /// <summary>Pins the clock to an absolute reading (frame harness: 0 / 275 / 550 / 825 / 1100 ms).</summary>
    public void Seek(double nowMs)
    {
        if (!IsManual) return;
        _manualNow = nowMs;
        Tick();
    }

    private void EnsureTimer()
    {
        if (_timer is not null) return;
        // ~60 Hz; DispatcherPriority.Render so updates land before the frame is composed.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 60.0), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }

    private void Tick()
    {
        var now = NowMs;
        // Snapshot: a participant may detach itself while ticking (e.g. becomes invisible).
        foreach (var participant in _participants.ToArray()) participant.TickMotion(now);
    }
}

/// <summary>
/// Reads the OS reduced-motion preference: Windows <c>SPI_GETCLIENTAREAANIMATION</c>, macOS
/// <c>NSAcessibilityDisplayShouldReduceMotion</c> (through <c>defaults</c>, since binding NSWorkspace needs an ObjC
/// bridge), Linux GNOME <c>org.gnome.desktop.interface enable-animations</c>. Every path fails open (animations on).
/// </summary>
public static class ReducedMotionDetector
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    public static bool Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animations, 0) && !animations;
            }
            if (OperatingSystem.IsMacOS())
            {
                var value = Run("defaults", "read com.apple.universalAccess reduceMotion");
                return value?.Trim() == "1";
            }
            if (OperatingSystem.IsLinux())
            {
                var value = Run("gsettings", "get org.gnome.desktop.interface enable-animations");
                return value?.Trim() == "false";
            }
        }
        catch
        {
            // No setting, no process, no permission: keep animations on, never fail the UI.
        }
        return false;
    }

    private static string? Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        return process.HasExited && process.ExitCode == 0 ? output : null;
    }
}
