using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sofia.App.Theme;

/// <summary>
/// Runtime appearance state: the accent preset, the root font size (sm/md/lg -> .875/1/1.125) and the compact-sidebar
/// dimensions. Everything it writes lands in <see cref="Application.Resources"/> and is consumed through
/// <c>DynamicResource</c>, so changing a setting repaints the whole shell live (motion contract/tokens stay in
/// Theme/*.axaml).
/// </summary>
public sealed class ThemeService : ObservableObject
{
    private const double BaseFontSize = 16;
    private static readonly string[] FontSizePresets = ["sm", "md", "lg"];

    /// <summary>Base key in Typography.axaml -> the runtime key components reference.</summary>
    private static readonly (string Base, string Target)[] ScaledKeys =
    [
        ("FontSizeXsBase", "FontSizeXs"),
        ("FontSizeSmBase", "FontSizeSm"),
        ("FontSizeSm2Base", "FontSizeSm2"),
        ("FontSizeMdBase", "FontSizeMd"),
        ("FontSizeMd2Base", "FontSizeMd2"),
        ("FontSizeLgBase", "FontSizeLg"),
        ("FontSizeXlBase", "FontSizeXl"),
        ("FontSizeXxlBase", "FontSizeXxl"),
        ("FontSizeDisplayBase", "FontSizeDisplay"),
        ("FontSizeHeroBase", "FontSizeHero"),
        ("PadRowBase", "PadRow"),
        ("PadBrandBase", "PadBrand"),
    ];

    public static ThemeService Current { get; } = new();

    private string _accent = "brown";
    private string _customAccent = AccentPalette.CustomDefault;
    private string _fontSize = "md";
    private bool _compactSidebar;
    private double _sidebarWidth = 288;

    /// <summary><c>brown</c> | <c>beige</c> | <c>slate</c> | <c>custom</c>.</summary>
    public string Accent
    {
        get => _accent;
        set
        {
            if (!AccentPalette.Presets.Contains(value)) value = "brown";
            if (SetProperty(ref _accent, value)) ApplyAccent();
        }
    }

    /// <summary>Hex used when <see cref="Accent"/> is <c>custom</c>.</summary>
    public string CustomAccent
    {
        get => _customAccent;
        set
        {
            if (!AccentPalette.IsValidHex(value)) return;
            if (SetProperty(ref _customAccent, value) && Accent == "custom") ApplyAccent();
        }
    }

    /// <summary><c>sm</c> (14px) | <c>md</c> (16px) | <c>lg</c> (18px), like the web root font-size setting.</summary>
    public string FontSize
    {
        get => _fontSize;
        set
        {
            if (!FontSizePresets.Contains(value)) value = "md";
            if (SetProperty(ref _fontSize, value))
            {
                OnPropertyChanged(nameof(FontScale));
                ApplyFontScale();
            }
        }
    }

    public bool CompactSidebar
    {
        get => _compactSidebar;
        set
        {
            if (SetProperty(ref _compactSidebar, value)) ApplyLayout();
        }
    }

    public double FontScale => Math.Round(FontSizeToScale(FontSize), 4);

    /// <summary>288px normally, 236px compact (AppSidebar's <c>--sofia-sidebar-width</c>).</summary>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        private set => SetProperty(ref _sidebarWidth, value);
    }

    public Color AccentColor => AccentPalette.Derive(AccentPalette.Resolve(Accent, CustomAccent)).Base;

    /// <summary>Writes every appearance token. Call once at startup (and after restoring settings).</summary>
    public void ApplyAll()
    {
        ApplyAccent();
        ApplyFontScale();
        ApplyLayout();
    }

    private static double FontSizeToScale(string preset) => preset switch
    {
        "sm" => 0.875,
        "lg" => 1.125,
        _ => 1.0,
    };

    private void ApplyAccent()
    {
        var accent = AccentPalette.Derive(AccentPalette.Resolve(Accent, CustomAccent));
        SetBrush("BrushPrimary", accent.Base);
        SetBrush("BrushPrimaryStrong", accent.Strong);
        SetBrush("BrushPrimaryInk", accent.Ink);
        SetBrush("BrushPrimarySoft", accent.Soft);
        SetBrush("BrushPrimarySoftStrong", accent.SoftStrong);
        // color-mix(primary 38%, transparent): the composer's idle send pill follows the accent like the web.
        SetBrush("BrushPrimaryIdle", Color.FromArgb(0x61, accent.Base.R, accent.Base.G, accent.Base.B));
        SetColor("ColorPrimary", accent.Base);
        SetColor("ColorPrimaryStrong", accent.Strong);
        SetColor("ColorPrimaryInk", accent.Ink);
        SetColor("ColorPrimarySoft", accent.Soft);
        SetColor("ColorPrimarySoftStrong", accent.SoftStrong);
        OnPropertyChanged(nameof(AccentColor));
    }

    private void ApplyFontScale()
    {
        var scale = FontScale;
        foreach (var (baseKey, targetKey) in ScaledKeys)
        {
            if (Find(baseKey) is not double value) continue;
            SetValue(targetKey, Math.Round(value * scale, 2));
        }

        // Line heights are unitless in the web; expose the resolved pixel values for TextBlock.LineHeight.
        if (Find("LineHeightBodyBase") is not double body) return;
        SetValue("LineHeightBody", Math.Round(BaseFontSize * scale * body, 1));
        if (Find("LineHeightMonoBase") is double mono) SetValue("LineHeightMono", Math.Round(BaseFontSize * scale * mono, 1));
        if (Find("LineHeightTightBase") is double tight) SetValue("LineHeightTight", Math.Round(BaseFontSize * scale * tight, 1));
        if (Find("LineHeightRelaxedBase") is double relaxed) SetValue("LineHeightRelaxed", Math.Round(BaseFontSize * scale * relaxed, 1));
    }

    private void ApplyLayout()
    {
        SidebarWidth = CompactSidebar ? 236 : 288;
        SetValue("SidebarWidth", SidebarWidth);
        // --sofia-row-pad: .4rem normal, .2rem compact; --sofia-brand-pad: 1rem / .65rem.
        var scale = FontScale;
        SetValue("RowPadding", Math.Round((CompactSidebar ? 3.2 : 6.4) * scale, 2));
        SetValue("BrandPadding", Math.Round((CompactSidebar ? 10.4 : 16) * scale, 2));
        SetValue("CompactSidebar", CompactSidebar);
    }

    private static object? Find(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    private static void SetValue(string key, object value)
    {
        if (Application.Current is null) return;
        Application.Current.Resources[key] = value;
    }

    private static void SetBrush(string key, Color color)
    {
        if (Find(key) is SolidColorBrush brush)
        {
            // Mutating the brush the DynamicResource already points at repaints without re-resolving.
            brush.Color = color;
            return;
        }
        SetValue(key, new SolidColorBrush(color));
    }

    private static void SetColor(string key, Color color) => SetValue(key, color);
}

