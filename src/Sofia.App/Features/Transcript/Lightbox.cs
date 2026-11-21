using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Fullscreen image lightbox (web <c>.lightbox</c>, z200): <c>rgba(0,0,0,.88)</c> scrim, the image fit to the window,
/// a 42px circular close button top-right and the download pill at the bottom. It lives on the shell's overlay ladder
/// (never an OS window), so the frame harness can capture it.
/// </summary>
public sealed class Lightbox : Border
{
    private readonly Panel _host;
    private readonly Action? _closed;

    public Lightbox(Panel host, Bitmap bitmap, Attachment attachment, Func<Attachment, Task>? download, Action? closed = null)
    {
        _host = host;
        _closed = closed;
        Background = new SolidColorBrush(Color.Parse("#E0000000"));
        Focusable = true;
        ClipToBounds = true;

        var grid = new Grid();
        grid.Children.Add(new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(48),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        });

        var close = new Button
        {
            Content = new SofiaIcon { Glyph = "x", Size = 17.6, Foreground = Brushes.White },
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 20, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        close.Click += (_, _) => Close();
        grid.Children.Add(close);

        if (download is not null)
        {
            var pill = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6.4,
                    Children =
                    {
                        new SofiaIcon { Glyph = "upload", Size = 15.2, Foreground = Brushes.White },
                        new TextBlock
                        {
                            Text = Localization.T("img.download"),
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brushes.White,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Background = new SolidColorBrush(Color.Parse("#1FFFFFFF")),
                BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(16, 8.8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 24),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            pill.Click += async (_, _) => await download(attachment);
            grid.Children.Add(pill);
        }

        Child = grid;
        PointerPressed += (_, e) =>
        {
            // Clicking the scrim closes (the image and the buttons stop the event themselves).
            if (e.Source is Visual source && (ReferenceEquals(source, this) || ReferenceEquals(source, grid))) Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        _host.SizeChanged += OnHostSize;
        _host.Children.Add(this);
        UpdateBounds();
    }

    public bool IsOpen { get; private set; } = true;

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        _host.Children.Add(this);
        UpdateBounds();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _host.SizeChanged -= OnHostSize;
        _host.Children.Remove(this);
        _closed?.Invoke();
    }

    private void OnHostSize(object? sender, SizeChangedEventArgs e) => UpdateBounds();

    private void UpdateBounds()
    {
        Width = Math.Max(1, _host.Bounds.Width);
        Height = Math.Max(1, _host.Bounds.Height);
        Canvas.SetLeft(this, 0);
        Canvas.SetTop(this, 0);
    }
}
