using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>Type scale and family resources (see <c>Typography.axaml</c>); merged by <c>App.axaml</c>.</summary>
public partial class Typography : ResourceDictionary
{
    public Typography() => AvaloniaXamlLoader.Load(this);
}

