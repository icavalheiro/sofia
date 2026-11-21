using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sofia.App.Theme;

/// <summary>Timing tokens (see <c>Motion.axaml</c>); merged by <c>App.axaml</c> and read by <c>MotionService</c>.</summary>
public partial class Motion : ResourceDictionary
{
    public Motion() => AvaloniaXamlLoader.Load(this);
}

