namespace Sofia.App.Services;

/// <summary>
/// The web's local settings object (<c>sofia.settings.v1</c> in <c>useSettings.ts</c>): UI language, the local general
/// toggles and the appearance choices. Stored in <see cref="LocalState"/> with camelCase names, so the same file keeps
/// making sense once the browser head owns <c>localStorage</c> (044). Server-side settings never live here.
/// </summary>
public sealed class LocalPreferences
{
    public static LocalPreferences Current { get; } = new();

    private LocalSettings _data;

    public LocalPreferences()
    {
        _data = LocalState.Current.Get<LocalSettings>(LocalState.SettingsKey) ?? new LocalSettings();
    }

    public LocalSettings Data => _data;

    /// <summary>Reads the stored language/toggles and applies them to the services that own them.</summary>
    public void Apply()
    {
        Localization.SetLocale(_data.Language);
        var sound = SoundService.Current;
        sound.Enabled = _data.General.SoundEffects;
        sound.Volume = _data.General.SoundVolume;

        var theme = Theme.ThemeService.Current;
        theme.Accent = _data.Appearance.Accent;
        theme.CustomAccent = _data.Appearance.CustomColor;
        theme.FontSize = _data.Appearance.FontSize;
        theme.CompactSidebar = _data.Appearance.CompactSidebar;
        theme.ApplyAll();
    }

    /// <summary>Persists the object and re-applies it (every setter in the settings dialog routes through here).</summary>
    public void Save(bool apply = true)
    {
        LocalState.Current.Set(LocalState.SettingsKey, _data);
        if (apply) Apply();
    }

    public void Reset()
    {
        _data = new LocalSettings();
        Save();
    }
}

/// <summary>Client-only settings (the web's <c>SettingsState</c> minus the profile, which the native app does not show).</summary>
public sealed class LocalSettings
{
    public string Language { get; set; } = "en";
    public LocalGeneral General { get; set; } = new();
    public LocalAppearance Appearance { get; set; } = new();
}

public sealed class LocalGeneral
{
    public bool StreamResponses { get; set; } = true;
    public bool ConfirmDestructive { get; set; } = true;
    public bool AutoUpdate { get; set; }
    public bool Telemetry { get; set; }
    public bool SoundEffects { get; set; } = true;
    /// <summary>0-100, default 60.</summary>
    public double SoundVolume { get; set; } = 60;
}

public sealed class LocalAppearance
{
    /// <summary>brown | beige | slate | custom.</summary>
    public string Accent { get; set; } = "brown";
    public string CustomColor { get; set; } = "#7c9cff";
    /// <summary>sm | md | lg.</summary>
    public string FontSize { get; set; } = "md";
    public bool CompactSidebar { get; set; }
}
