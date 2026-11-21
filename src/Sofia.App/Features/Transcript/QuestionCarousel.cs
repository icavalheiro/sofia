using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sofia.App.Controls;
using Sofia.App.Features.Transcript.CardViewModels;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Multi-question carousel (web <c>.q-carousel</c>): one page per question plus a review page, never sent until the
/// user confirms there. The pages slide through <see cref="CarouselStage"/> (±32px + opacity, .22s ease, instant under
/// reduced motion) and the picks live in the draft store so a <c>message.updated</c> does not lose them.
/// </summary>
public sealed class QuestionCarousel : Border
{
    private readonly QuestionSetCardViewModel _vm;
    private readonly MessageViewModel _message;
    private readonly Action _onChanged;
    private readonly Grid _header;
    private readonly CarouselStage _stage = new();
    private readonly Grid _nav;
    private bool _rendering;

    public QuestionCarousel(QuestionSetCardViewModel vm, MessageViewModel message, Action onChanged)
    {
        _vm = vm;
        _message = message;
        _onChanged = onChanged;

        Background = ThemeLookup.Brush("BrushPrimarySoft");
        BorderBrush = ThemeLookup.Brush("BrushPrimary");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.6));
        Margin = new Thickness(0, TranscriptUi.Rem(0.5), 0, 0);
        MaxWidth = TranscriptUi.CardWidth;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _header = BuildHeader();
        _nav = BuildNav();
        Child = TranscriptUi.Column(0, _header, _stage, _nav);
        _stage.Show(BuildPage());
    }

    public CarouselStage Stage => _stage;

    public QuestionSetCardViewModel ViewModel => _vm;

    /// <summary>Moves the carousel one page forward/backward with the .22s transition (frame harness, tests).</summary>
    public void Step(int delta)
    {
        var target = Math.Clamp(_vm.Slide + delta, 0, _vm.TotalSlides - 1);
        if (target == _vm.Slide) return;
        _vm.GoTo(target);
        Refresh(animate: true);
    }

    public void GoTo(int index)
    {
        if (index == _vm.Slide) return;
        _vm.GoTo(index);
        Refresh(animate: true);
    }

    private Grid BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5)) };
        var title = TranscriptUi.Text(_vm.Title, TranscriptUi.Rem(0.8125), "BrushPrimary", FontWeight.SemiBold, wrap: false);
        var icon = TranscriptUi.Icon("help", TranscriptUi.Rem(0.95), "BrushPrimary");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.4), 0);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        left.Children.Add(icon);
        left.Children.Add(title);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var count = TranscriptUi.Text(_vm.CountLabel, TranscriptUi.Rem(0.7), "BrushTextFaint", wrap: false);
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);
        return grid;
    }

    private Grid BuildNav()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, TranscriptUi.Rem(0.6), 0, 0) };

        var back = Arrow("chevronLeft", _vm.Slide == 0, () => Step(-1));
        Grid.SetColumn(back, 0);
        grid.Children.Add(back);

        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.3), HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < _vm.TotalSlides; i++)
        {
            dots.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = ThemeLookup.Brush(i == _vm.Slide ? "BrushPrimary" : "BrushBorderStrong"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(dots, 1);
        grid.Children.Add(dots);

        var forward = Arrow("chevronRight", _vm.OnReview || _vm.Picks[_vm.Slide].Count == 0, () => Step(1));
        Grid.SetColumn(forward, 2);
        grid.Children.Add(forward);
        return grid;
    }

    private Button Arrow(string glyph, bool disabled, Action click)
    {
        var button = new Button
        {
            Content = new SofiaIcon { Glyph = glyph, Size = TranscriptUi.Rem(0.9), Foreground = ThemeLookup.Brush("BrushTextMuted") },
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = !disabled,
            Opacity = disabled ? 0.35 : 1,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Rebuilds the header/nav and swaps the page; <paramref name="animate"/> slides it.</summary>
    public void Refresh(bool animate)
    {
        if (!animate)
        {
            _stage.Show(BuildPage());
        }
        else
        {
            _stage.Step(BuildPage(), _vm.DirectionSign);
        }
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        var count = _header.Children.OfType<TextBlock>().LastOrDefault();
        if (count is not null) count.Text = _vm.CountLabel;
        var dots = ((StackPanel)((Grid)_nav).Children[1]).Children;
        for (var i = 0; i < dots.Count && i < _vm.TotalSlides; i++)
        {
            if (dots[i] is Border dot) dot.Background = ThemeLookup.Brush(i == _vm.Slide ? "BrushPrimary" : "BrushBorderStrong");
        }
        if (((Grid)_nav).Children[0] is Button back) back.IsEnabled = _vm.Slide > 0;
        if (((Grid)_nav).Children[2] is Button forward)
        {
            forward.IsEnabled = !_vm.OnReview && _vm.Picks[_vm.Slide].Count > 0;
            forward.Opacity = forward.IsEnabled ? 1 : 0.35;
        }
    }

    /// <summary>Called by the free-text boxes (they own the caret; no page rebuild while typing).</summary>
    private void OnFreeText(int index, TextBox box)
    {
        if (_rendering) return;
        _vm.SetFree(index, box.Text ?? "");
        _vm.GoTo(_vm.Slide); // no-op; keeps the draft store in sync without stealing focus
    }

    private Control BuildPage()
    {
        _rendering = true;
        try
        {
            return _vm.OnReview ? BuildReview() : BuildQuestion(_vm.Slide);
        }
        finally
        {
            _rendering = false;
        }
    }

    private Control BuildQuestion(int index)
    {
        var question = _vm.Card.Questions[index];
        var prompt = TranscriptUi.Text(question.Prompt, TranscriptUi.Rem(0.85), "BrushText", FontWeight.SemiBold);
        prompt.Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5));

        var options = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.3) };
        foreach (var option in question.Options)
        {
            options.Children.Add(OptionButton(index, option, question.Multi));
        }

        var free = new TextBox
        {
            Text = _vm.Free(index),
            PlaceholderText = _vm.FreePlaceholder,
            Background = Brushes.Transparent,
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.6), TranscriptUi.Rem(0.3)),
            FontSize = TranscriptUi.Rem(0.75),
            Foreground = ThemeLookup.Brush("BrushText"),
            CaretBrush = ThemeLookup.Brush("BrushPrimary"),
            SelectionBrush = ThemeLookup.Brush("BrushPrimarySoftStrong"),
        };
        free.TextChanged += (_, _) => OnFreeText(index, free);
        free.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _vm.SubmitFree(index);
            if (!question.Multi) Step(1);
            else Refresh(animate: false);
        };
        options.Children.Add(free);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        stack.Children.Add(prompt);
        stack.Children.Add(options);

        if (!question.Multi) return stack;

        var hint = TranscriptUi.Text(_vm.Hint, TranscriptUi.Rem(0.7), "BrushTextFaint", wrap: false);
        var next = TranscriptUi.Pill(_vm.NextLabel, TranscriptUi.Rem(0.75), "BrushPrimaryInk",
            ThemeLookup.Brush("BrushPrimary"), padding: new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.3)),
            weight: FontWeight.SemiBold);
        next.Click += (_, _) => Step(1);
        next.IsEnabled = _vm.Picks[index].Count > 0;
        next.Opacity = next.IsEnabled ? 1 : 0.4;
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, TranscriptUi.Rem(0.6), 0, 0) };
        Grid.SetColumn(hint, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(hint);
        Grid.SetColumn(next, 1);
        foot.Children.Add(next);
        stack.Children.Add(foot);
        return stack;
    }

    private Control BuildReview()
    {
        var prompt = TranscriptUi.Text(_vm.ReviewPrompt, TranscriptUi.Rem(0.85), "BrushPrimary", FontWeight.SemiBold);
        prompt.Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5));

        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.3), Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.6)) };
        for (var i = 0; i < _vm.Count; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var questionText = TranscriptUi.Text(_vm.Card.Questions[i].Prompt, TranscriptUi.Rem(0.78), "BrushTextMuted");
            Grid.SetColumn(questionText, 0);
            row.Children.Add(questionText);

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.25), VerticalAlignment = VerticalAlignment.Center };
            var picks = _vm.Picks[i];
            if (picks.Count == 0)
            {
                chips.Children.Add(TranscriptUi.Text("—", TranscriptUi.Rem(0.78), "BrushTextFaint", wrap: false));
            }
            else
            {
                foreach (var pick in picks)
                {
                    chips.Children.Add(TranscriptUi.Text("> " + pick, TranscriptUi.Rem(0.78), "BrushText", FontWeight.SemiBold, wrap: false));
                }
            }
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);

            var editIndex = i;
            var edit = new Button
            {
                Content = new SofiaIcon { Glyph = "code", Size = TranscriptUi.Rem(0.8), Foreground = ThemeLookup.Brush("BrushTextFaint") },
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(TranscriptUi.Rem(0.5), 0, 0, 0),
            };
            edit.Click += (_, _) => GoTo(editIndex);
            Grid.SetColumn(edit, 2);
            row.Children.Add(edit);
            list.Children.Add(row);
        }

        var confirm = TranscriptUi.Pill(_vm.ConfirmLabel, TranscriptUi.Rem(0.78), "#FFFFFF",
            ThemeLookup.Brush("BrushPrimary"), padding: new Thickness(TranscriptUi.Rem(0.8), TranscriptUi.Rem(0.4)),
            weight: FontWeight.SemiBold);
        confirm.HorizontalAlignment = HorizontalAlignment.Center;
        confirm.Margin = new Thickness(0, TranscriptUi.Rem(0.75), 0, TranscriptUi.Rem(0.5));
        confirm.IsEnabled = _vm.AllAnswered;
        confirm.Opacity = confirm.IsEnabled ? 1 : 0.4;
        confirm.Click += async (_, _) =>
        {
            await _message.ConfirmQuestionsAsync(_vm);
            _onChanged();
        };

        return TranscriptUi.Column(0, prompt, list, confirm);
    }

    private Control OptionButton(int index, string option, bool multi)
    {
        var chosen = _vm.IsPicked(index, option);
        var label = TranscriptUi.Text(option, TranscriptUi.Rem(0.75), chosen ? "#FFFFFF" : "BrushText");
        label.VerticalAlignment = VerticalAlignment.Center;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.45) };
        if (multi)
        {
            var box = new Border
            {
                Width = TranscriptUi.Rem(0.95),
                Height = TranscriptUi.Rem(0.95),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.5),
                BorderBrush = chosen ? Brushes.White : ThemeLookup.Brush("BrushBorderStrong"),
                Background = chosen ? Brushes.White : ThemeLookup.Brush("BrushSurface"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = chosen
                    ? new SofiaIcon { Glyph = "check", Size = TranscriptUi.Rem(0.65), StrokeWidth = 3, Foreground = ThemeLookup.Brush("BrushPrimary") }
                    : null,
            };
            content.Children.Add(box);
        }
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Background = chosen ? ThemeLookup.Brush("BrushPrimary") : ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = chosen ? ThemeLookup.Brush("BrushPrimary") : ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.6), TranscriptUi.Rem(0.3)),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) =>
        {
            if (_vm.Confirmed) return;
            var before = _vm.Slide;
            _vm.Pick(index, option);
            Refresh(animate: _vm.Slide != before);
            if (_vm.Slide == before) Refresh(animate: false);
        };
        return button;
    }
}
