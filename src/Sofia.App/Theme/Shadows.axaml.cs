using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>Box shadow resources (see <c>Shadows.axaml</c>); merged by <c>App.axaml</c>.</summary>
public partial class Shadows : ResourceDictionary
{
    public Shadows() => AvaloniaXamlLoader.Load(this);
}

