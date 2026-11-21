using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;

namespace Sofia.App;

/// <summary>
/// Shared Avalonia application for every head. The desktop head shows the shell in a real window; the browser head
/// (spec 044) boots the same instance, so nothing platform-specific may live here.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var options = HeadOptions.Current;

        // Appearance first: ThemeService republishes the scaled type tokens the shell is laid out with.
        RestoreAppearance();
        ThemeService.Current.ApplyAll();
        MotionService.Current.ReducedMotion = ReducedMotionDetector.Detect();
        SoundService.Current.Use(options.SoundPlayer ?? new NullSoundPlayer());
        RestoreSound();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(options.Fixture);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RestoreAppearance()
    {
        var state = LocalState.Current.Get<AppearanceState>(LocalState.AppearanceKey);
        if (state is null) return;
        var theme = ThemeService.Current;
        if (state.Accent is not null) theme.Accent = state.Accent;
        if (state.CustomColor is not null) theme.CustomAccent = state.CustomColor;
        if (state.FontSize is not null) theme.FontSize = state.FontSize;
        theme.CompactSidebar = state.CompactSidebar ?? false;
    }

    private static void RestoreSound()
    {
        var state = LocalState.Current.Get<SoundState>(LocalState.SoundKey);
        if (state is null) return;
        var sound = SoundService.Current;
        if (state.Enabled is not null) sound.Enabled = state.Enabled.Value;
        if (state.Volume is not null) sound.Volume = state.Volume.Value;
    }

    public sealed class AppearanceState
    {
        public string? Accent { get; set; }
        public string? CustomColor { get; set; }
        public string? FontSize { get; set; }
        public bool? CompactSidebar { get; set; }
    }

    public sealed class SoundState
    {
        public bool? Enabled { get; set; }
        public double? Volume { get; set; }
    }
}
