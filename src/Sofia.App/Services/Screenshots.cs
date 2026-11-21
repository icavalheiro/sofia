using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sofia.App.Shell;
using Sofia.App.Theme;

namespace Sofia.App.Services;

/// <summary>
/// Head-specific services injected by the executable heads (desktop now, browser in spec 044) so the shared
/// <c>Sofia.App</c> never references a platform audio package or the engine itself.
/// </summary>
public sealed class HeadOptions
{
    public static HeadOptions Current { get; } = new();

    /// <summary>Desktop: SoundFlow. Browser: WebAudio wrapper (044). Null keeps the silent player.</summary>
    public ISoundPlayer? SoundPlayer { get; set; }

    /// <summary>Desktop: data-dir lock → in-process Core, else HTTP. Browser: always HTTP.</summary>
    public IBackendResolver? Resolver { get; set; }

    /// <summary>Render deterministic <see cref="Fixture"/> data instead of connecting (screenshots, tests).</summary>
    public bool Fixture { get; set; }

    /// <summary>Screenshot/frame harness request parsed from the command line, or null for a normal run.</summary>
    public ScreenshotRequest? Screenshot { get; set; }
}

/// <summary>One screenshot batch: sizes, device scale, optional frame sequence and where to write the PNGs.</summary>
public sealed class ScreenshotRequest
{
    /// <summary>The three logical sizes the epic compares at (1080p, 2k, 4k) with their Windows device scales.</summary>
    public static readonly (string Name, int Width, int Height, double Scale)[] DefaultSizes =
    [
        ("1080p", 1920, 1080, 1.0),
        ("2k", 2560, 1440, 1.333),
        ("4k", 3840, 2160, 2.0),
    ];

    public string OutputPath { get; set; } = "shell.png";

    public string OutputDirectory { get; set; } = ".";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public double Scale { get; set; } = 1.0;

    /// <summary>When set, also writes <c>frame-&lt;ms&gt;.png</c> at these manual-clock readings.</summary>
    public double[]? Frames { get; set; }

    /// <summary>Opens the right panel before capture (frame harness for motion #19).</summary>
    public bool RightPanel { get; set; }

    /// <summary>Closes the right panel (default) so the 1232px column is measured at full width.</summary>
    public bool CompactSidebar { get; set; }

    public string Accent { get; set; } = "brown";

    public string FontSize { get; set; } = "md";

    public bool ReducedMotion { get; set; }

    /// <summary>Sidebar state for the 040 evidence: <c>mixed</c> (default), <c>empty</c>, <c>working</c> or <c>search</c>.</summary>
    public string SidebarState { get; set; } = "mixed";

    /// <summary>Conversation row whose hover pill is revealed before the frames (040 motion #3).</summary>
    public string? HoverSession { get; set; }

    /// <summary>Transcript fixture state (041): idle, streaming, permission, questions, diff, lightbox, markdown, compaction, retry, queued, waiting.</summary>
    public string TranscriptState { get; set; } = "idle";

    /// <summary>Transcript animation to start at frame 0: hover, fold, carousel, pulse, retry or spinner.</summary>
    public string? TranscriptFrame { get; set; }

    /// <summary>Message whose actions/fold/carousel the frame harness drives (defaults to the transcript's last reply).</summary>
    public string? HoverMessage { get; set; }

    /// <summary>All three logical sizes in one run; individual width/height/scale override it.</summary>
    public bool AllSizes { get; set; }

    public static ScreenshotRequest? Parse(string[] args)
    {
        if (!args.Contains("--screenshot")) return null;
        var request = new ScreenshotRequest();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "--screenshot":
                    request.OutputPath = Next() ?? request.OutputPath;
                    break;
                case "--out-dir":
                    request.OutputDirectory = Next() ?? request.OutputDirectory;
                    break;
                case "--width":
                    if (int.TryParse(Next(), out var width)) request.Width = width;
                    break;
                case "--height":
                    if (int.TryParse(Next(), out var height)) request.Height = height;
                    break;
                case "--scale":
                    if (double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out var scale))
                        request.Scale = scale;
                    break;
                case "--frames":
                    request.Frames = (Next() ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0)
                        .ToArray();
                    break;
                case "--right-panel":
                    request.RightPanel = true;
                    break;
                case "--compact-sidebar":
                    request.CompactSidebar = true;
                    break;
                case "--accent":
                    request.Accent = Next() ?? request.Accent;
                    break;
                case "--font-size":
                    request.FontSize = Next() ?? request.FontSize;
                    break;
                case "--reduced-motion":
                    request.ReducedMotion = true;
                    break;
                case "--sidebar-state":
                    request.SidebarState = Next() ?? request.SidebarState;
                    break;
                case "--hover-row":
                    request.HoverSession = Next();
                    break;
                case "--transcript-state":
                    request.TranscriptState = Next() ?? request.TranscriptState;
                    break;
                case "--transcript-frame":
                    request.TranscriptFrame = Next();
                    break;
                case "--hover-message":
                    request.HoverMessage = Next();
                    break;
                case "--all-sizes":
                    request.AllSizes = true;
                    break;
            }
        }
        return request;
    }
}

/// <summary>
/// Render harness shared by the desktop <c>--screenshot</c> flag and the headless tests: lays the shell out at a logical
/// size with a device scale, renders it to PNG and (optionally) captures a stop-motion frame sequence from the manual
/// motion clock. The tests hook <see cref="FrameHook"/> to force a headless render tick between frames.
/// </summary>
public static class ScreenshotHarness
{
    /// <summary>Called before every capture; the headless tests force a render timer tick here.</summary>
    public static Action? FrameHook { get; set; }

    /// <summary>Called once after the manual clock is reset to 0, before the first frame of a batch (starts a reveal).</summary>
    public static Action? FrameBegin { get; set; }

    /// <summary>Applies appearance overrides so one run can capture accent/font variations.</summary>
    public static void Apply(ScreenshotRequest request)
    {
        var theme = ThemeService.Current;
        theme.Accent = request.Accent;
        theme.FontSize = request.FontSize;
        theme.CompactSidebar = request.CompactSidebar;
        theme.ApplyAll();
        MotionService.Current.ReducedMotion = request.ReducedMotion;
    }

    /// <summary>Resizes the window, lays it out and writes one PNG at <paramref name="scale"/> device pixels per DIP.</summary>
    public static void Capture(TopLevel top, string path, int width, int height, double scale)
    {
        top.Width = width;
        top.Height = height;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        FrameHook?.Invoke();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        var bitmap = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale))),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(top);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write to a unique temp file and swap it in: the evidence paths are fixed, so two harness runs (or a test
        // runner and the CLI) must never trip over each other's lock.
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            bitmap.Save(temp, new PngBitmapEncoderOptions());
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(60);
                }
            }
        }
        finally
        {
            bitmap.Dispose();
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Another process still holds it; the temp name is unique, so leaving it is harmless.
                }
            }
        }
    }

    /// <summary>
    /// Captures the shell at every requested size (all three by default) and, when <see cref="ScreenshotRequest.Frames"/>
    /// is set, the frame sequence at the same logical size using the manual clock.
    /// </summary>
    public static IReadOnlyList<string> Run(TopLevel top, ScreenshotRequest request)
    {
        Apply(request);
        var written = new List<string>();
        var sizes = request.AllSizes
            ? ScreenshotRequest.DefaultSizes
            : [(Path.GetFileNameWithoutExtension(request.OutputPath), request.Width, request.Height, request.Scale)];

        foreach (var (name, width, height, scale) in sizes)
        {
            var baseName = Path.GetFileNameWithoutExtension(request.OutputPath);
            var file = sizes.Length == 1
                ? Path.Combine(request.OutputDirectory, request.OutputPath)
                : Path.Combine(request.OutputDirectory, $"{baseName}-{name}.png");
            MotionService.Current.EnableManualClock();
            MotionService.Current.Seek(0);
            FrameBegin?.Invoke();
            Capture(top, file, width, height, scale);
            written.Add(file);

            if (request.Frames is not { Length: > 0 } frames) continue;
            var frameDirectory = Path.Combine(request.OutputDirectory, Path.GetFileNameWithoutExtension(file) + ".frames");
            Directory.CreateDirectory(frameDirectory);
            foreach (var ms in frames)
            {
                MotionService.Current.Seek(ms);
                var frame = Path.Combine(frameDirectory, $"frame-{(int)ms}.png");
                Capture(top, frame, width, height, scale);
                written.Add(frame);
            }
        }

        return written;
    }
}
