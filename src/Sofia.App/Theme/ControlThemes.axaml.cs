using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>Button/chip control themes (see <c>ControlThemes.axaml</c>); merged by <c>App.axaml</c>.</summary>
public partial class ControlThemes : ResourceDictionary
{
    public ControlThemes() => AvaloniaXamlLoader.Load(this);
}

