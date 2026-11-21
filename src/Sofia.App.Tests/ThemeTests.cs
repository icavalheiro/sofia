using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Sofia.App.Theme;

namespace Sofia.App.Tests;

/// <summary>
/// Token parity: every value the app paints comes from Theme/*.axaml and must equal the web's CSS. These tests load the
/// real application resources and then exercise the runtime appearance switches (accent presets, font scale, compact
/// sidebar), which is what acceptance criterion #4 asks for.
/// </summary>
public class ThemeTests
{
    [AvaloniaFact]
    public void Palette_matches_the_web_tokens()
    {
        var theme = ThemeService.Current;
        theme.Accent = "brown";
        theme.FontSize = "md";
        theme.ApplyAll();

        Assert.Equal(Color.Parse("#171719"), Token("ColorBg"));
        Assert.Equal(Color.Parse("#1D1D20"), Token("ColorSurface"));
        Assert.Equal(Color.Parse("#242428"), Token("ColorSurface2"));
        Assert.Equal(Color.Parse("#2C2C31"), Token("ColorSurface3"));
        Assert.Equal(Color.Parse("#33333A"), Token("ColorBorder"));
        Assert.Equal(Color.Parse("#43434B"), Token("ColorBorderStrong"));
        Assert.Equal(Color.Parse("#E9E9EC"), Token("ColorText"));
        Assert.Equal(Color.Parse("#A0A0A8"), Token("ColorTextMuted"));
        Assert.Equal(Color.Parse("#6B6B74"), Token("ColorTextFaint"));
        Assert.Equal(Color.Parse("#A9744F"), Token("ColorPrimary"));
        // `strong` is the runtime derivation (channel-wise lerp to white by .12), not the CSS fallback #BC8A5E:
        // useAppearance.ts overwrites --sofia-primary-strong on startup exactly like ThemeService does.
        Assert.Equal(Color.Parse("#B38564"), Token("ColorPrimaryStrong"));
        Assert.Equal(Color.Parse("#B38564"), AccentPalette.Derive("#a9744f").Strong);
        Assert.Equal(Color.Parse("#E4D3AC"), Token("ColorDetail"));
        Assert.Equal(Color.Parse("#6F9FCC"), Token("ColorInfo"));
        Assert.Equal(Color.Parse("#74A672"), Token("ColorOk"));
        Assert.Equal(Color.Parse("#C9705F"), Token("ColorErr"));
        Assert.Equal(Color.Parse("#C9A94F"), Token("ColorWarn"));

        // Softs keep the web's alpha values (.16 / .26 / .10 / .14).
        Assert.Equal(0x29, Token("ColorPrimarySoft").A);
        Assert.Equal(0x42, Token("ColorPrimarySoftStrong").A);
        Assert.Equal(0x1A, Token("ColorDetailSoft").A);
        Assert.Equal(0x24, Token("ColorInfoSoft").A);
    }

    [AvaloniaFact]
    public void Type_scale_starts_at_the_web_rem_sizes()
    {
        Assert.Equal(11d, Value("FontSizeXs"));
        Assert.Equal(12d, Value("FontSizeSm"));
        Assert.Equal(13d, Value("FontSizeSm2"));
        Assert.Equal(14d, Value("FontSizeMd"));
        Assert.Equal(16d, Value("FontSizeLg"));
        Assert.Equal(22d, Value("FontSizeDisplay"));
        Assert.Equal(6.4d, Value("PadRow"));
        Assert.Equal(288d, Value("SidebarWidth"));
    }

    [AvaloniaFact]
    public void Accent_presets_and_derivation_follow_useAppearance()
    {
        var theme = ThemeService.Current;
        theme.Accent = "beige";
        Assert.Equal(Color.Parse("#C9B489"), theme.AccentColor);
        Assert.Equal(Color.Parse("#C9B489"), Token("ColorPrimary"));
        // strong = channel-wise lerp to white by .12
        var strong = Token("ColorPrimaryStrong");
        Assert.Equal((byte)Math.Round(0xC9 + (255 - 0xC9) * 0.12), strong.R);
        // light accent -> dark ink (luminance > .55)
        Assert.Equal(Color.Parse("#171719"), Token("ColorPrimaryInk"));

        theme.Accent = "slate";
        Assert.Equal(Color.Parse("#7B7B84"), theme.AccentColor);
        // dark accent -> white ink
        Assert.Equal(Colors.White, Token("ColorPrimaryInk"));

        theme.Accent = "custom";
        theme.CustomAccent = "#7C9CFF";
        Assert.Equal(Color.Parse("#7C9CFF"), theme.AccentColor);

        theme.Accent = "brown";
        Assert.Equal(Color.Parse("#A9744F"), Token("ColorPrimary"));
        Assert.Equal(0x29, Token("ColorPrimarySoft").A);
    }

    [AvaloniaFact]
    public void Font_size_setting_scales_every_token_by_14_16_18()
    {
        var theme = ThemeService.Current;
        theme.FontSize = "md";
        Assert.Equal(1.0, theme.FontScale);
        Assert.Equal(14d, Value("FontSizeMd"));

        theme.FontSize = "sm";
        Assert.Equal(0.875, theme.FontScale);
        Assert.Equal(12.25, Value("FontSizeMd"));
        Assert.Equal(14d, Value("FontSizeLg"));

        theme.FontSize = "lg";
        Assert.Equal(1.125, theme.FontScale);
        Assert.Equal(15.75, Value("FontSizeMd"));
        Assert.Equal(18d, Value("FontSizeLg"));
        Assert.Equal(7.2, Value("PadRow"));

        theme.FontSize = "md";
        Assert.Equal(14d, Value("FontSizeMd"));
    }

    [AvaloniaFact]
    public void Compact_sidebar_switches_the_web_dimensions()
    {
        var theme = ThemeService.Current;
        theme.CompactSidebar = false;
        theme.ApplyAll();
        Assert.Equal(288d, theme.SidebarWidth);
        Assert.Equal(288d, Value("SidebarWidth"));
        Assert.Equal(6.4, Value("RowPadding"));

        theme.CompactSidebar = true;
        Assert.Equal(236d, theme.SidebarWidth);
        Assert.Equal(236d, Value("SidebarWidth"));
        Assert.Equal(3.2, Value("RowPadding"));

        theme.CompactSidebar = false;
    }

    private static Color Token(string key)
    {
        if (Application.Current!.TryFindResource(key, out var value) && value is Color color) return color;
        throw new Xunit.Sdk.XunitException($"missing colour resource {key}");
    }

    private static double Value(string key)
    {
        if (Application.Current!.TryFindResource(key, out var value) && value is double number) return number;
        throw new Xunit.Sdk.XunitException($"missing number resource {key}");
    }
}
