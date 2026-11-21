using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;

namespace Sofia.App.Controls;

/// <summary>Where a <see cref="SofiaPopover"/> unfolds relative to its anchor.</summary>
public enum PopoverPlacement
{
    Top,
    Bottom,
}

/// <summary>
/// In-window menu surface (chips, session menus, model picker). It is deliberately not a native popup: the web's menus
/// live inside the window on the z-index ladder (30-60), so the shell hosts them in its overlay panel and screenshots
/// match. Chrome: surface-2 fill, 1px border, 12px radius, menu shadow, 6px padding.
/// </summary>
public class SofiaPopover : Panel
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SofiaPopover, bool>(nameof(IsOpen));

    public static readonly StyledProperty<PopoverPlacement> PlacementProperty =
        AvaloniaProperty.Register<SofiaPopover, PopoverPlacement>(nameof(Placement), PopoverPlacement.Top);

    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<SofiaPopover, object?>(nameof(Content));

    private readonly Border _surface = new();
    private readonly ContentPresenter _presenter = new();

    public SofiaPopover()
    {
        _presenter.Margin = new Thickness(6);
        _surface.Child = _presenter;
        _surface.CornerRadius = new CornerRadius(12);
        _surface.BorderThickness = new Thickness(1);
        _surface.Padding = new Thickness(0);
        _surface.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 12,
            Blur = 32,
            Color = Color.FromArgb(0x47, 0, 0, 0),
        });
        Children.Add(_surface);
        UpdateChrome();
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public PopoverPlacement Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty || change.Property == PlacementProperty) UpdateChrome();
        if (change.Property == ContentProperty) _presenter.Content = Content;
    }

    private void UpdateChrome()
    {
        IsVisible = IsOpen;

        _surface.MinWidth = MinWidth;
        VerticalAlignment = Placement == PopoverPlacement.Top
            ? Avalonia.Layout.VerticalAlignment.Top
            : Avalonia.Layout.VerticalAlignment.Bottom;
        _surface.Background = Resource("BrushSurface2", Color.Parse("#242428"));
        _surface.BorderBrush = Resource("BrushBorder", Color.Parse("#33333A"));
        _surface.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = Placement == PopoverPlacement.Top ? -12 : 12,
            Blur = 32,
            Color = Color.FromArgb(0x47, 0, 0, 0),
        });
    }

    private static IBrush Resource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}

