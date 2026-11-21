using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Sofia.App.Theme;

/// <summary>App-wide styles (see <c>Controls.axaml</c>); appended after the Fluent theme by <c>App.axaml</c>.</summary>
public partial class Controls : Styles
{
    public Controls() => AvaloniaXamlLoader.Load(this);
}
