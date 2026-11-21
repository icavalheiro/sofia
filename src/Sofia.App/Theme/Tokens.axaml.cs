using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>Palette dictionary (see <c>Tokens.axaml</c>); merged by <c>App.axaml</c>.</summary>
public partial class Tokens : ResourceDictionary
{
    public Tokens() => AvaloniaXamlLoader.Load(this);
}

