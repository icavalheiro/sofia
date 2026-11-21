using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>
/// Lucide glyph geometry (see <c>Icons.axaml</c>, generated from lucide-vue-next 1.0.0 by
/// <c>tools/app/gen-icons.mjs</c>). Keys are <c>Icon.&lt;semantic name&gt;</c>, exactly like SofiaIcon.vue.
/// </summary>
public partial class Icons : ResourceDictionary
{
    public Icons() => AvaloniaXamlLoader.Load(this);

    /// <summary>Resource key for a semantic icon name, or null when the web renders nothing for it (e.g. <c>circle</c>).</summary>
    public static string? KeyFor(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : "Icon." + name;
}

