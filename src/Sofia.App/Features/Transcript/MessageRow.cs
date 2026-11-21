using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Features.Transcript.CardViewModels;
using Sofia.App.Features.Transcript.Markdown;
using Sofia.App.Services;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// One transcript row (web <c>ChatMessageItem.vue</c>): avatar + meta line + the role's body, the reasoning block, the
/// markdown prose, the tool cards with their folds and diffs, the image/file attachments, the notice/card family, the
/// prompt-cache warning and the hover-revealed actions. The row subscribes to its view model while mounted; streamed
/// tokens refresh the prose at most once per 80ms (re-parsing markdown per token would stall the UI).
/// </summary>
public sealed class MessageRow : Border
{
    private readonly MessageViewModel _vm;
    private readonly TranscriptView _host;
    private readonly DispatcherTimer _streamTimer;
    private HoverReveal? _reveal;
    private MarkdownProse? _prose;
    private TextBlock? _reasoningText;
    private bool _hovered;
    private bool _subscribed;
    private readonly List<Action> _folds = [];
    private IMotionParticipant? _pulse;

    public MessageRow(MessageViewModel vm, TranscriptView host)
    {
        _vm = vm;
        _host = host;
        Background = Brushes.Transparent;
        Padding = new Thickness(0, TranscriptUi.Rem(0.75), 0, TranscriptUi.Rem(0.75));
        _streamTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background,
            (_, _) =>
            {
                _streamTimer.Stop();
                RefreshStreaming();
            });
        PointerEntered += (_, _) =>
        {
            _hovered = true;
            _reveal?.Begin(true);
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            _reveal?.Begin(false);
        };
        Build();
    }

    public MessageViewModel ViewModel => _vm;

    /// <summary>Opacity of the hover-revealed actions (frame-harness assertion for motion #4).</summary>
    public double ActionsOpacity => _reveal?.Opacity ?? 0;

    /// <summary>Reasoning fold panel of this row (frame-harness assertion for motion #14).</summary>
    public CollapsiblePanel? ReasoningPanel { get; private set; }

    /// <summary>Every tool fold panel of this row.</summary>
    public List<CollapsiblePanel> ToolPanels { get; } = [];

    /// <summary>Compaction notice text (motion #15 pulses it).</summary>
    public TextBlock? CompactionText { get; private set; }

    /// <summary>Retry glyph (motion #16 spins it at 1.8s).</summary>
    public SofiaIcon? RetryIcon { get; private set; }

    /// <summary>The question carousel of this row, when it has one.</summary>
    public QuestionCarousel? Carousel { get; private set; }

    /// <summary>Image thumbnails built for this row, keyed by attachment id (screenshot harness opens the lightbox).</summary>
    public Dictionary<string, Border> Thumbnails { get; } = new(StringComparer.Ordinal);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_subscribed) return;
        _subscribed = true;
        _vm.Changed += OnViewModelChanged;
        _vm.Streamed += OnStreamed;
        if (_vm.Cards.Compaction is { Running: true } running) Pulse(running);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            _vm.Changed -= OnViewModelChanged;
            _vm.Streamed -= OnStreamed;
        }
        _streamTimer.Stop();
        _reveal?.ResetForHarness();
        if (_pulse is not null)
        {
            MotionService.Current.Detach(_pulse);
            _pulse = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged()
    {
        // A full server snapshot: rebuild the row but keep the hover state and the armed revert the model holds.
        Dispatcher.UIThread.Post(() =>
        {
            var hovered = _hovered || IsPointerOver;
            Build();
            if (hovered) _reveal?.SetInstant(true);
        });
    }

    private void OnStreamed()
    {
        if (_streamTimer.IsEnabled) return;
        _streamTimer.Start();
    }

    /// <summary>Cheap streaming repaint: only the prose and the reasoning text change per token batch.</summary>
    public void RefreshStreaming()
    {
        _prose?.SetSource(_vm.Text, TranscriptUi.Rem(0.9375));
        if (_reasoningText is not null) _reasoningText.Text = _vm.Reasoning ?? "";
        if (_vm.Cards.Reasoning is { } reasoning && ReasoningPanel is not null) ReasoningPanel.IsExpanded = reasoning.Expanded;
    }

    /// <summary>Full rebuild from the view model (also used after a local UI toggle).</summary>
    public void Refresh() => Build();

    /// <summary>Starts the actions' hover reveal on the shared clock (frame harness, no pointer involved).</summary>
    public void Hover()
    {
        _hovered = true;
        _reveal?.Begin(true);
    }

    /// <summary>Toggles one fold of the row (reasoning first, then tools) for the frame harness.</summary>
    public bool ToggleFold(int index = 0)
    {
        if (index < 0 || index >= _folds.Count) return false;
        _folds[index]();
        return true;
    }

    // --- layout --------------------------------------------------------------------------------------------

    private void Build()
    {
        Thumbnails.Clear();
        ToolPanels.Clear();
        _folds.Clear();
        ReasoningPanel = null;
        CompactionText = null;
        RetryIcon = null;
        Carousel = null;
        _prose = null;
        _reasoningText = null;

        if (_vm.Cards.Compaction is { } compaction)
        {
            Child = BuildCompactionRow(compaction);
            _reveal = null;
            return;
        }

        var body = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        body.Children.Add(BuildMeta());
        if (_vm.Cards.Retry is { } retry) body.Children.Add(BuildRetry(retry));
        if (_vm.Cards.Error is { } error) body.Children.Add(BuildError(error.Text, "BrushErr"));
        if (!string.IsNullOrEmpty(_vm.ActionError)) body.Children.Add(BuildError(_vm.ActionError!, "BrushErr"));

        if (_vm.IsAssistant)
        {
            if (_vm.Cards.Reasoning is { } reasoning) body.Children.Add(BuildReasoning(reasoning));
            if (_vm.ShowsBody && !string.IsNullOrWhiteSpace(_vm.Text))
            {
                _prose = new MarkdownProse { MaxWidth = TranscriptUi.ProseWidth, HorizontalAlignment = HorizontalAlignment.Left };
                _prose.SetSource(_vm.Text, TranscriptUi.Rem(0.9375));
                body.Children.Add(_prose);
            }
            if (_vm.Tools.Count > 0) body.Children.Add(BuildToolList());
        }
        else if (_vm.ShowsBody && !string.IsNullOrWhiteSpace(_vm.Text))
        {
            body.Children.Add(BuildUserBubble());
        }

        if (_vm.ImageAttachments.Count > 0) body.Children.Add(BuildGallery(_vm.ImageAttachments));
        if (_vm.FileAttachments.Count > 0) body.Children.Add(BuildFileChips(_vm.FileAttachments));
        if (_vm.Cards.Summary is { } summary) body.Children.Add(BuildSummary(summary));
        if (_vm.Cards.Knowledge is { } knowledge) body.Children.Add(BuildKnowledge(knowledge));
        if (!_vm.IsAssistant && _vm.Cards.Context is { } context) body.Children.Add(BuildContext(context));
        if (_vm.Cards.Permission is { } permission) body.Children.Add(BuildPermission(permission));
        if (_vm.Cards.Question is { } question) body.Children.Add(BuildQuestion(question));
        if (_vm.Cards.QuestionSet is { } set)
        {
            Carousel = new QuestionCarousel(set, _vm, Refresh);
            body.Children.Add(Carousel);
        }
        if (_vm.CacheBreak is { } cache) body.Children.Add(BuildCacheWarning(cache));
        if (!_vm.IsPending && !_vm.IsCompaction) body.Children.Add(BuildActions());

        if (_vm.IsAssistant)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var avatar = BuildAvatar();
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);
            body.Margin = new Thickness(TranscriptUi.Rem(0.75), 0, 0, 0);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            Child = grid;
        }
        else
        {
            var grid = new Grid();
            body.HorizontalAlignment = HorizontalAlignment.Right;
            body.MaxWidth = Math.Min(952, _host.ContentWidth * 0.82);
            if (body.MaxWidth <= 0) body.MaxWidth = 760;
            grid.Children.Add(body);
            Child = grid;
        }
    }

    private Control BuildAvatar() => new Border
    {
        Width = 32,
        Height = 32,
        CornerRadius = new CornerRadius(10),
        ClipToBounds = true,
        BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
        BorderThickness = new Thickness(1),
        Background = ThemeLookup.Brush("BrushPrimarySoft"),
        VerticalAlignment = VerticalAlignment.Top,
        Child = new Image
        {
            Source = TranscriptView.LoadAsset("avares://Sofia.App/Assets/Logos/logo-31.png"),
            Stretch = Stretch.UniformToFill,
        },
    };

    private Control BuildMeta()
    {
        var author = TranscriptUi.Text(_vm.Author, TranscriptUi.Rem(0.75), _vm.IsAssistant ? "BrushText" : "BrushPrimary",
            FontWeight.SemiBold, wrap: false);
        author.VerticalAlignment = VerticalAlignment.Center;

        // The web aligns the user's meta as a group at the right edge of the bubble (flex-end), while the assistant
        // meta spans the body with the time pushed to the right.
        if (!_vm.IsAssistant)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = TranscriptUi.Rem(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.3)),
            };
            row.Children.Add(author);
            if (!string.IsNullOrEmpty(_vm.ModelLabel))
            {
                row.Children.Add(TranscriptUi.Text(_vm.ModelLabel!, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false));
            }
            if (_vm.IsPending)
            {
                row.Children.Add(TranscriptUi.Text(TranscriptUi.LocalizePending(_vm.PendingDelivery),
                    TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false));
                var remove = TranscriptUi.Pill(Localization.T("chat.removeQueued"), TranscriptUi.Rem(0.625), "BrushTextFaint",
                    padding: new Thickness(TranscriptUi.Rem(0.35), TranscriptUi.Rem(0.05)), radius: 999);
                remove.Click += async (_, _) => await _vm.RemoveQueuedAsync();
                row.Children.Add(remove);
                return row;
            }
            row.Children.Add(TranscriptUi.Text(_vm.TimeLabel, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false));
            return row;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.3)) };
        Grid.SetColumn(author, 0);
        grid.Children.Add(author);

        if (!string.IsNullOrEmpty(_vm.ModelLabel))
        {
            var model = TranscriptUi.Text(_vm.ModelLabel!, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false);
            model.Margin = new Thickness(TranscriptUi.Rem(0.5), 0, 0, 0);
            model.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(model, 1);
            grid.Children.Add(model);
        }

        var time = TranscriptUi.Text(_vm.TimeLabel, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false);
        time.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);
        return grid;
    }

    private Control BuildUserBubble() => new Border
    {
        Background = ThemeLookup.Brush("BrushSurface2"),
        BorderBrush = ThemeLookup.Brush("BrushBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(TranscriptUi.Rem(0.85), TranscriptUi.Rem(0.6)),
        Child = TranscriptUi.Text(_vm.Text, TranscriptUi.Rem(0.9375), "BrushText"),
    };

    // --- reasoning -----------------------------------------------------------------------------------------

    private Control BuildReasoning(ReasoningCardViewModel reasoning)
    {
        var chevron = TranscriptUi.Icon(reasoning.Expanded ? "chevronUp" : "chevronDown", TranscriptUi.Rem(0.75), "BrushDetail");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = TranscriptUi.Icon("zap", TranscriptUi.Rem(0.75), "BrushDetail");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.35), 0);
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);
        var label = TranscriptUi.Label(reasoning.Label, TranscriptUi.Rem(0.6875), "BrushDetail");
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        head.Children.Add(label);
        Grid.SetColumn(chevron, 2);
        head.Children.Add(chevron);

        _reasoningText = TranscriptUi.Text(reasoning.Text, TranscriptUi.Rem(0.8125), "BrushTextMuted");
        _reasoningText.FontStyle = FontStyle.Italic;
        _reasoningText.LineHeight = TranscriptUi.Rem(0.8125) * 1.55;
        _reasoningText.Padding = new Thickness(0, TranscriptUi.Rem(0.25), 0, 0);

        var panel = new CollapsiblePanel { IsExpanded = reasoning.Expanded, Child = _reasoningText };
        ReasoningPanel = panel;

        var toggle = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = head,
            Padding = new Thickness(0),
        };
        toggle.PointerPressed += (_, _) => ToggleReasoning();
        _folds.Add(ToggleReasoning);

        void ToggleReasoning()
        {
            reasoning.Toggle();
            panel.IsExpanded = reasoning.Expanded;
            chevron.Glyph = reasoning.Expanded ? "chevronUp" : "chevronDown";
        }

        return new Border
        {
            BorderBrush = ThemeLookup.Brush("BrushDetail"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Background = ThemeLookup.Brush("BrushDetailSoft"),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Padding = new Thickness(TranscriptUi.Rem(0.75), TranscriptUi.Rem(0.55)),
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5)),
            Child = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0, Children = { toggle, panel } },
        };
    }

    // --- tool cards ----------------------------------------------------------------------------------------

    private Control BuildToolList() => new StackPanel
    {
        Orientation = Orientation.Vertical,
        Spacing = TranscriptUi.Rem(0.5),
        Margin = new Thickness(0, TranscriptUi.Rem(0.6), 0, TranscriptUi.Rem(0.6)),
        Children = { },
    }.With(panel =>
    {
        foreach (var tool in _vm.Tools) panel.Children.Add(BuildToolCard(tool));
    });

    private Control BuildToolCard(ToolCardViewModel tool)
    {
        var tone = tool.Tone switch
        {
            "ok" => ThemeLookup.Brush("BrushOk"),
            "running" => ThemeLookup.Brush("BrushPrimary"),
            _ => ThemeLookup.Brush("BrushErr"),
        };
        var statusTone = tool.Tone switch
        {
            "ok" => ("BrushOk", "BrushOkSoft"),
            "running" => ("BrushPrimary", "BrushPrimarySoft"),
            _ => ("BrushErr", "BrushErrSoft"),
        };

        var status = new Border
        {
            Background = ThemeLookup.Brush(statusTone.Item2),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(TranscriptUi.Rem(0.4), TranscriptUi.Rem(0.05)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = TranscriptUi.Rem(0.2),
                Children =
                {
                    TranscriptUi.Icon(tool.StatusIcon, TranscriptUi.Rem(0.7), statusTone.Item1),
                    TranscriptUi.Text(tool.StatusLabel, TranscriptUi.Rem(0.6875), statusTone.Item1, FontWeight.SemiBold, wrap: false),
                },
            },
        };

        var chevron = TranscriptUi.Icon(tool.Expanded ? "chevronUp" : "chevronDown", TranscriptUi.Rem(0.75), "BrushTextFaint");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        var icon = TranscriptUi.Icon(tool.Icon, TranscriptUi.Rem(0.9), "BrushTextMuted");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.45), 0);
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);

        var name = TranscriptUi.Mono(tool.Name, TranscriptUi.Rem(0.75), "BrushText");
        name.FontWeight = FontWeight.SemiBold;
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        Grid.SetColumn(status, 2);
        status.Margin = new Thickness(TranscriptUi.Rem(0.45), 0, 0, 0);
        head.Children.Add(status);

        var duration = TranscriptUi.Text(tool.Duration, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false);
        duration.VerticalAlignment = VerticalAlignment.Center;
        duration.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(duration, 4);
        head.Children.Add(duration);

        Grid.SetColumn(chevron, 5);
        chevron.Margin = new Thickness(TranscriptUi.Rem(0.45), 0, 0, 0);
        head.Children.Add(chevron);

        var body = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        if (!string.IsNullOrWhiteSpace(tool.Summary))
        {
            var summary = TranscriptUi.Text(tool.Summary, TranscriptUi.Rem(0.8125), "BrushTextMuted");
            summary.Margin = new Thickness(0, TranscriptUi.Rem(0.25), 0, 0);
            body.Children.Add(summary);
        }
        if (tool.HasChild) body.Children.Add(BuildChildLink(tool));
        if (tool.HasInput) body.Children.Add(BuildToolBlock("tool.input", tool.Input!, tool.InputOpen, () => tool.InputOpen = !tool.InputOpen));
        if (tool.IsDiff) body.Children.Add(BuildDiffCard(tool));
        else if (tool.HasOutput) body.Children.Add(BuildToolBlock("tool.output", tool.Output!, tool.OutputOpen, () => tool.OutputOpen = !tool.OutputOpen));
        if (tool.HasImages) body.Children.Add(BuildGallery(tool.Images));
        if (tool.HasError) body.Children.Add(BuildToolBlock("tool.error", tool.Error!, true, null, error: true));

        var panel = new CollapsiblePanel { IsExpanded = tool.Expanded, Child = body };
        ToolPanels.Add(panel);

        head.PointerPressed += (_, _) => ToggleToolFold();
        _folds.Add(ToggleToolFold);

        void ToggleToolFold()
        {
            tool.Expanded = !tool.Expanded;
            panel.IsExpanded = tool.Expanded;
            chevron.Glyph = tool.Expanded ? "chevronUp" : "chevronDown";
        }

        // The 2px left rule of the web (border-left-color by status) needs its own border: a single Border can only
        // carry one brush for every side.
        var inner = new Border
        {
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = ThemeLookup.Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 9, 9, 0),
            Padding = new Thickness(TranscriptUi.Rem(0.65), TranscriptUi.Rem(0.5)),
            ClipToBounds = true,
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { head, panel } },
        };
        return new Border
        {
            BorderBrush = tone,
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = inner,
        };
    }

    private Control BuildChildLink(ToolCardViewModel tool)
    {
        var link = TranscriptUi.Pill(Localization.T("tool.openSubagent"), TranscriptUi.Rem(0.7), "BrushTextMuted",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushBorderStrong"),
            new Thickness(TranscriptUi.Rem(0.55), TranscriptUi.Rem(0.2)), 999, FontWeight.SemiBold);
        link.Margin = new Thickness(0, TranscriptUi.Rem(0.4), 0, 0);
        link.Click += (_, _) => _host.OpenSession(tool.ChildSessionId!);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.35) };
        row.Children.Add(link);
        if (tool.ChildModelId is { } model)
        {
            row.Children.Add(TranscriptUi.Text(model, TranscriptUi.Rem(0.7), "BrushTextFaint", wrap: false));
        }
        row.Children.Add(TranscriptUi.Text(tool.ChildMode, TranscriptUi.Rem(0.7),
            tool.Call.ChildBackground ? "BrushTextFaint" : "BrushPrimary", wrap: false));
        return row;
    }

    private Control BuildToolBlock(string labelKey, string body, bool open, Action? toggle, bool error = false)
    {
        var pre = new TextBlock
        {
            Text = body,
            FontFamily = ThemeLookup.Find("FontFamilyMono") as FontFamily,
            FontSize = TranscriptUi.Rem(0.75),
            Foreground = ThemeLookup.Brush(error ? "BrushErr" : "BrushTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(TranscriptUi.Rem(0.5), TranscriptUi.Rem(0.45)),
        };
        var scroll = new ScrollViewer
        {
            Content = pre,
            MaxHeight = 200,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var chevron = TranscriptUi.Icon(open ? "chevronUp" : "chevronDown", TranscriptUi.Rem(0.7), "BrushTextFaint");
        var labelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = TranscriptUi.Label(Localization.T(labelKey), TranscriptUi.Rem(0.625),
            error ? "BrushErr" : labelKey == "tool.output" ? "BrushOk" : "BrushTextFaint");
        Grid.SetColumn(label, 0);
        labelRow.Children.Add(label);
        Grid.SetColumn(chevron, 1);
        labelRow.Children.Add(chevron);

        var labelBorder = new Border
        {
            Padding = new Thickness(TranscriptUi.Rem(0.5), TranscriptUi.Rem(0.2)),
            Background = ThemeLookup.Brush("BrushSurface"),
            BorderBrush = ThemeLookup.Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = toggle is null ? null : new Cursor(StandardCursorType.Hand),
            Child = labelRow,
        };

        var panel = new CollapsiblePanel { IsExpanded = open, Child = scroll };
        if (toggle is not null)
        {
            labelBorder.PointerPressed += (_, _) =>
            {
                toggle();
                var nowOpen = !open;
                panel.IsExpanded = nowOpen;
                chevron.Glyph = nowOpen ? "chevronUp" : "chevronDown";
                labelBorder.BorderThickness = nowOpen ? new Thickness(0, 0, 0, 1) : new Thickness(0);
                open = nowOpen;
            };
        }

        return new Border
        {
            Margin = new Thickness(0, TranscriptUi.Rem(0.4), 0, 0),
            Background = ThemeLookup.Brush("BrushBg"),
            BorderBrush = ThemeLookup.Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { labelBorder, panel } },
        };
    }

    private Control BuildDiffCard(ToolCardViewModel tool)
    {
        var rows = tool.DiffRows ?? [];
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var row in rows)
        {
            var background = row.Kind switch
            {
                DiffRowKind.Add => ThemeLookup.Brush("BrushOkSoft"),
                DiffRowKind.Delete => ThemeLookup.Brush("BrushErrSoft"),
                _ => Brushes.Transparent,
            };
            var sign = row.Kind == DiffRowKind.Add ? "+" : row.Kind == DiffRowKind.Delete ? "-" : " ";
            var signBrush = row.Kind switch
            {
                DiffRowKind.Add => "BrushOk",
                DiffRowKind.Delete => "BrushErr",
                _ => "BrushTextFaint",
            };
            var signText = TranscriptUi.Text(sign, TranscriptUi.Rem(0.78), signBrush, FontWeight.Bold, wrap: false);
            signText.Width = TranscriptUi.Rem(0.78) * 1.2;
            signText.TextAlignment = TextAlignment.Center;
            var text = TranscriptUi.Mono(row.Text, TranscriptUi.Rem(0.78), "BrushTextMuted", wrap: true);
            text.Padding = new Thickness(0, TranscriptUi.Rem(0.1), TranscriptUi.Rem(0.5), TranscriptUi.Rem(0.1));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Background = background };
            line.Children.Add(signText);
            line.Children.Add(text);
            stack.Children.Add(new Border
            {
                Child = line,
                BorderBrush = ThemeLookup.WithAlpha("ColorBorder", 0.45),
                BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return new Border
        {
            Margin = new Thickness(0, TranscriptUi.Rem(0.4), 0, 0),
            Background = ThemeLookup.Brush("BrushBg"),
            BorderBrush = ThemeLookup.Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = scroll,
        };
    }

    // --- attachments ---------------------------------------------------------------------------------------

    private Control BuildGallery(IReadOnlyList<Attachment> attachments)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = TranscriptUi.Rem(0.5),
            LineSpacing = TranscriptUi.Rem(0.5),
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(_vm.IsAssistant ? 0.5 : 0.75)),
        };
        foreach (var attachment in attachments)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = 240, MaxHeight = 180 };
            var bitmap = _host.LoadAttachment(attachment);
            if (bitmap is not null) image.Source = bitmap;
            else SubscribeThumbnail(attachment, image);

            var thumb = new Border
            {
                MaxWidth = 240,
                MaxHeight = 180,
                Background = ThemeLookup.Brush("BrushSurface2"),
                BorderBrush = ThemeLookup.Brush("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = image,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            thumb.PointerPressed += (_, _) => _host.OpenLightbox(attachment);
            Thumbnails[attachment.Id] = thumb;

            var caption = TranscriptUi.Text($"{attachment.Name} · {attachment.Size}", TranscriptUi.Rem(0.6875), "BrushTextFaint", wrap: false);
            caption.Margin = new Thickness(0, TranscriptUi.Rem(0.25), 0, 0);
            caption.MaxWidth = 240;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Vertical,
                MaxWidth = 240,
                Children = { thumb, caption },
            });
        }
        return panel;
    }

    private void SubscribeThumbnail(Attachment attachment, Image image)
    {
        void OnLoaded(Attachment loaded)
        {
            if (loaded.Id != attachment.Id) return;
            var bitmap = _host.Attachments.BitmapFor(loaded);
            if (bitmap is not null) image.Source = bitmap;
        }

        _host.Attachments.Loaded += OnLoaded;
        image.DetachedFromVisualTree += (_, _) => _host.Attachments.Loaded -= OnLoaded;
    }

    private Control BuildFileChips(IReadOnlyList<Attachment> attachments)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = TranscriptUi.Rem(0.4),
            LineSpacing = TranscriptUi.Rem(0.4),
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(_vm.IsAssistant ? 0.5 : 0.75)),
        };
        foreach (var attachment in attachments)
        {
            var icon = attachment.Kind switch
            {
                "video" => "video",
                "audio" => "music",
                _ => "file",
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.4) };
            row.Children.Add(TranscriptUi.Icon(icon, TranscriptUi.Rem(0.9), "BrushTextMuted"));
            var name = TranscriptUi.Text(attachment.Name, TranscriptUi.Rem(0.75), "BrushText", wrap: false);
            name.MaxWidth = TranscriptUi.Rem(14 * 0.6);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            row.Children.Add(TranscriptUi.Text(attachment.Size, TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false));
            panel.Children.Add(new Border
            {
                Background = ThemeLookup.Brush("BrushSurface2"),
                BorderBrush = ThemeLookup.Brush("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(TranscriptUi.Rem(0.55), TranscriptUi.Rem(0.3)),
                Child = row,
            });
        }
        return panel;
    }

    // --- cards ---------------------------------------------------------------------------------------------

    private Control BuildSummary(SummaryCardViewModel summary)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.35), Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.3)) };
        head.Children.Add(TranscriptUi.Icon("book", TranscriptUi.Rem(0.85), "BrushTextFaint"));
        head.Children.Add(TranscriptUi.Label(summary.Title, TranscriptUi.Rem(0.6875), "BrushTextFaint"));
        var prose = new MarkdownProse { MaxWidth = TranscriptUi.CardWidth };
        prose.SetSource(summary.Text, TranscriptUi.Rem(0.8125));
        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(TranscriptUi.Rem(0.75), TranscriptUi.Rem(0.55)),
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { head, prose } },
        };
    }

    private Control BuildKnowledge(KnowledgeCardViewModel knowledge)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto") };
        var icon = TranscriptUi.Icon("sparkles", TranscriptUi.Rem(0.85), "BrushPrimary");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.35), 0);
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);
        var title = TranscriptUi.Label(knowledge.Title, TranscriptUi.Rem(0.6875), "BrushPrimary");
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var count = new Border
        {
            Background = ThemeLookup.Brush("BrushSurface3"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(TranscriptUi.Rem(0.4), 0),
            Margin = new Thickness(TranscriptUi.Rem(0.35), 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = TranscriptUi.Text(knowledge.LiveCount.ToString(), TranscriptUi.Rem(0.625), "BrushTextMuted", wrap: false),
        };
        Grid.SetColumn(count, 2);
        head.Children.Add(count);

        var toggle = TranscriptUi.Pill(knowledge.ToggleLabel, TranscriptUi.Rem(0.6875), "BrushTextMuted",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushBorderStrong"),
            new Thickness(TranscriptUi.Rem(0.5), TranscriptUi.Rem(0.15)), 7);
        Grid.SetColumn(toggle, 4);
        head.Children.Add(toggle);

        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.3), Margin = new Thickness(0, TranscriptUi.Rem(0.45), 0, 0) };
        foreach (var note in knowledge.Notes)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var scope = new Border
            {
                Background = ThemeLookup.Brush("BrushSurface3"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(TranscriptUi.Rem(0.4), TranscriptUi.Rem(0.05)),
                Margin = new Thickness(0, TranscriptUi.Rem(0.1), TranscriptUi.Rem(0.45), 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = TranscriptUi.Label(note.ScopeLabel, TranscriptUi.Rem(0.625), "BrushTextFaint"),
            };
            Grid.SetColumn(scope, 0);
            row.Children.Add(scope);

            var text = TranscriptUi.Text(note.Text, TranscriptUi.Rem(0.8125), note.Deleted ? "BrushTextFaint" : "BrushText");
            if (note.Deleted) text.TextDecorations = TextDecorations.Strikethrough;
            var body = new StackPanel { Orientation = Orientation.Vertical };
            body.Children.Add(text);
            if (!string.IsNullOrEmpty(note.Why) && !note.Deleted)
            {
                var why = TranscriptUi.Text(note.Why!, TranscriptUi.Rem(0.6875), "BrushTextFaint");
                why.Margin = new Thickness(0, TranscriptUi.Rem(0.1), 0, 0);
                body.Children.Add(why);
            }
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            if (note.Deleted)
            {
                var gone = TranscriptUi.Text(Localization.T("knowledge.deleted"), TranscriptUi.Rem(0.6875), "BrushTextFaint", wrap: false);
                Grid.SetColumn(gone, 2);
                row.Children.Add(gone);
            }
            else
            {
                var delete = new Button
                {
                    Content = TranscriptUi.Icon("trash", TranscriptUi.Rem(0.8), "BrushTextFaint"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(TranscriptUi.Rem(0.2)),
                    CornerRadius = new CornerRadius(6),
                };
                ToolTip.SetTip(delete, Localization.T("knowledge.delete"));
                var note1 = note;
                delete.Click += async (_, _) => await _vm.DeleteKnowledgeAsync(knowledge, note1);
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
            }
            list.Children.Add(row);
        }

        if (!string.IsNullOrEmpty(knowledge.Error))
        {
            var error = TranscriptUi.Text(knowledge.Error!, TranscriptUi.Rem(0.75), "BrushErr");
            error.Margin = new Thickness(0, TranscriptUi.Rem(0.35), 0, 0);
            list.Children.Add(error);
        }

        var panel = new CollapsiblePanel { IsExpanded = knowledge.Expanded, Child = list };
        toggle.Click += (_, _) =>
        {
            knowledge.Toggle();
            panel.IsExpanded = knowledge.Expanded;
            if (toggle.Content is TextBlock label) label.Text = knowledge.ToggleLabel;
        };

        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, TranscriptUi.Rem(_vm.IsAssistant ? 0 : 0.5), 0, 0),
            Background = ThemeLookup.Brush("BrushPrimarySoft"),
            BorderBrush = ThemeLookup.Brush("BrushPrimary"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(TranscriptUi.Rem(0.75), TranscriptUi.Rem(0.45)),
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { head, panel } },
        };
    }

    private Control BuildContext(ContextCardViewModel context)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto") };
        var icon = TranscriptUi.Icon("sparkles", TranscriptUi.Rem(0.85), "BrushTextMuted");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.35), 0);
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);
        var title = TranscriptUi.Label(context.Title, TranscriptUi.Rem(0.6875), "BrushTextMuted");
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var count = new Border
        {
            Background = ThemeLookup.Brush("BrushSurface3"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(TranscriptUi.Rem(0.4), 0),
            Margin = new Thickness(TranscriptUi.Rem(0.35), 0, 0, 0),
            Child = TranscriptUi.Text(context.Lines.Count.ToString(), TranscriptUi.Rem(0.625), "BrushTextMuted", wrap: false),
        };
        Grid.SetColumn(count, 2);
        head.Children.Add(count);
        var toggle = TranscriptUi.Pill(context.ToggleLabel, TranscriptUi.Rem(0.6875), "BrushTextMuted",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushBorderStrong"),
            new Thickness(TranscriptUi.Rem(0.5), TranscriptUi.Rem(0.15)), 7);
        Grid.SetColumn(toggle, 4);
        head.Children.Add(toggle);

        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.25), Margin = new Thickness(TranscriptUi.Rem(1.1), TranscriptUi.Rem(0.45), 0, 0) };
        foreach (var line in context.Lines)
        {
            list.Children.Add(TranscriptUi.Text("• " + line, TranscriptUi.Rem(0.8125), "BrushText"));
        }
        var panel = new CollapsiblePanel { IsExpanded = context.Expanded, Child = list };
        toggle.Click += (_, _) =>
        {
            context.Toggle();
            panel.IsExpanded = context.Expanded;
            if (toggle.Content is TextBlock label) label.Text = context.ToggleLabel;
        };

        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(TranscriptUi.Rem(0.75), TranscriptUi.Rem(0.45)),
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { head, panel } },
        };
    }

    private Control BuildRetry(RetryCardViewModel retry)
    {
        RetryIcon = new SofiaIcon
        {
            Glyph = "retry",
            Size = TranscriptUi.Rem(0.95),
            Foreground = ThemeLookup.Brush("BrushWarn"),
            Spin = !MotionService.Current.ReducedMotion,
            SpinMs = MotionTokens.RetrySpin,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TranscriptUi.Rem(0.1), 0, 0),
        };
        var text = TranscriptUi.Text(retry.Text, TranscriptUi.Rem(0.8125), "BrushWarn");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.5) };
        row.Children.Add(RetryIcon);
        row.Children.Add(text);
        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = ThemeLookup.Brush("BrushWarn"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.65), TranscriptUi.Rem(0.45)),
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5)),
            Child = row,
        };
    }

    private Control BuildError(string text, string brush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.5) };
        row.Children.Add(new SofiaIcon
        {
            Glyph = "alert",
            Size = TranscriptUi.Rem(0.95),
            Foreground = ThemeLookup.Brush(brush),
            VerticalAlignment = VerticalAlignment.Top,
        });
        row.Children.Add(TranscriptUi.Text(text, TranscriptUi.Rem(0.8125), brush));
        return new Border
        {
            BorderBrush = ThemeLookup.Brush("BrushErr"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            Background = ThemeLookup.Brush("BrushErrSoft"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.65), TranscriptUi.Rem(0.5)),
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.5)),
            Child = row,
        };
    }

    private Control BuildCacheWarning(CacheBreakInfo cache)
    {
        var title = Localization.T("cache.lowTitle").Replace("{pct}", cache.Pct.ToString("0.#"));
        var reasons = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.15) };
        foreach (var key in cache.Reasons)
        {
            reasons.Children.Add(TranscriptUi.Text("• " + Localization.T(key), TranscriptUi.Rem(0.75), "BrushTextMuted"));
        }

        var copy = TranscriptUi.Pill(Localization.T("cache.copyPrompt"), TranscriptUi.Rem(0.6875), "BrushTextMuted",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushBorderStrong"),
            new Thickness(TranscriptUi.Rem(0.55), TranscriptUi.Rem(0.2)), 999, FontWeight.SemiBold);
        copy.Margin = new Thickness(0, TranscriptUi.Rem(0.45), 0, 0);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Click += async (_, _) =>
        {
            var text = CacheHealth.DiagnosisTemplate()
                .Replace("{pct}", cache.Pct.ToString("0.#"))
                .Replace("{cached}", cache.Cached.ToString("N0"))
                .Replace("{input}", cache.Input.ToString("N0"))
                .Replace("{reasons}", string.Join('\n', cache.Reasons.Select(r => "- " + Localization.T(r))));
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not null) await top.Clipboard.SetValueAsync(DataFormat.Text, text);
        };

        var body = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(TranscriptUi.Text(title, TranscriptUi.Rem(0.8125), "BrushWarn", FontWeight.SemiBold));
        var reasonsBorder = new Border { Child = reasons, Margin = new Thickness(TranscriptUi.Rem(1.1), TranscriptUi.Rem(0.3), 0, 0) };
        body.Children.Add(reasonsBorder);
        body.Children.Add(copy);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.5) };
        row.Children.Add(new SofiaIcon
        {
            Glyph = "alert",
            Size = TranscriptUi.Rem(0.9),
            Foreground = ThemeLookup.Brush("BrushWarn"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TranscriptUi.Rem(0.15), 0, 0),
        });
        row.Children.Add(body);

        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = ThemeLookup.Brush("BrushWarn"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            Background = ThemeLookup.Brush("BrushWarnSoft"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.65), TranscriptUi.Rem(0.5)),
            Margin = new Thickness(0, TranscriptUi.Rem(0.6), 0, 0),
            Child = row,
        };
    }

    // --- permission and questions ---------------------------------------------------------------------------

    private Control BuildPermission(PermissionCardViewModel permission)
    {
        var tone = permission.Tone switch
        {
            "ok" => "BrushOk",
            "err" => "BrushErr",
            _ => "BrushWarn",
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = TranscriptUi.Icon("shield", TranscriptUi.Rem(0.95), tone);
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.4), 0);
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);
        var title = TranscriptUi.Text(permission.Title, TranscriptUi.Rem(0.75), tone, FontWeight.SemiBold, wrap: false);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var tool = TranscriptUi.Mono(permission.Tool, TranscriptUi.Rem(0.75), "BrushTextFaint");
        Grid.SetColumn(tool, 2);
        head.Children.Add(tool);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(head);
        var description = TranscriptUi.Text(permission.Description, TranscriptUi.Rem(0.8125), "BrushText");
        description.Margin = new Thickness(0, TranscriptUi.Rem(0.55), 0, permission.Resolved ? 0 : TranscriptUi.Rem(0.45));
        stack.Children.Add(description);

        if (!permission.Resolved)
        {
            var choices = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = TranscriptUi.Rem(0.4), LineSpacing = TranscriptUi.Rem(0.4) };
            foreach (var choice in permission.Choices)
            {
                var button = choice.IsDeny
                    ? TranscriptUi.Pill(choice.Label, TranscriptUi.Rem(0.75), "BrushTextMuted",
                        ThemeLookup.Brush("BrushSurface2"), ThemeLookup.Brush("BrushBorderStrong"),
                        new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.35)), 8, FontWeight.SemiBold)
                    : TranscriptUi.Pill(choice.Label, TranscriptUi.Rem(0.75), "BrushOk",
                        ThemeLookup.Brush("BrushOkSoft"), ThemeLookup.Brush("BrushOk"),
                        new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.35)), 8, FontWeight.SemiBold);
                var label = choice.Label;
                button.Click += async (_, _) => await _vm.ResolvePermissionAsync(label);
                choices.Children.Add(button);
            }
            stack.Children.Add(choices);
        }
        else
        {
            var resolved = new Border
            {
                Background = ThemeLookup.Brush(permission.Allowed ? "BrushOkSoft" : "BrushErrSoft"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(TranscriptUi.Rem(0.6), TranscriptUi.Rem(0.3)),
                Margin = new Thickness(0, TranscriptUi.Rem(0.55), 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = TranscriptUi.Text(permission.ResolvedText, TranscriptUi.Rem(0.78),
                    permission.Allowed ? "BrushOk" : "BrushErr", FontWeight.SemiBold, wrap: false),
            };
            stack.Children.Add(resolved);
        }

        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, TranscriptUi.Rem(0.5), 0, 0),
            Background = ThemeLookup.Brush(permission.Resolved ? "BrushSurface2" : "BrushWarnSoft"),
            BorderBrush = ThemeLookup.Brush(permission.Resolved ? "BrushBorder" : "BrushWarn"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.6)),
            Child = stack,
        };
    }

    private Control BuildQuestion(QuestionCardViewModel question)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.4), Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.45)) };
        head.Children.Add(TranscriptUi.Icon("help", TranscriptUi.Rem(0.95), "BrushPrimary"));
        head.Children.Add(TranscriptUi.Text(question.Prompt, TranscriptUi.Rem(0.8125), "BrushPrimary", FontWeight.SemiBold));
        stack.Children.Add(head);

        if (question.Resolved)
        {
            stack.Children.Add(new Border
            {
                Background = ThemeLookup.Brush("BrushOkSoft"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(TranscriptUi.Rem(0.6), TranscriptUi.Rem(0.4)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = TranscriptUi.Text("> " + question.ResolvedText, TranscriptUi.Rem(0.78), "BrushOk", FontWeight.SemiBold),
            });
            return QuestionCardBorder(stack);
        }

        var options = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.3) };
        stack.Children.Add(options);
        var free = new TextBox
        {
            Text = question.FreeText,
            PlaceholderText = question.FreePlaceholder,
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

        void RenderOptions()
        {
            options.Children.Clear();
            foreach (var option in question.Card.Options)
            {
                var chosen = question.Multi && question.IsPicked(option);
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.45) };
                if (question.Multi)
                {
                    content.Children.Add(new Border
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
                    });
                }
                content.Children.Add(TranscriptUi.Text(option, TranscriptUi.Rem(0.75), chosen ? "#FFFFFF" : "BrushText"));
                var button = new Button
                {
                    Content = content,
                    Background = chosen ? ThemeLookup.Brush("BrushPrimary") : ThemeLookup.Brush("BrushSurface2"),
                    BorderBrush = chosen ? ThemeLookup.Brush("BrushPrimary") : ThemeLookup.Brush("BrushBorderStrong"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(TranscriptUi.Rem(0.6), TranscriptUi.Rem(0.3)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                var label = option;
                button.Click += async (_, _) =>
                {
                    if (question.Multi)
                    {
                        question.TogglePick(label);
                        RenderOptions();
                        return;
                    }
                    await _vm.AnswerQuestionAsync([label]);
                };
                options.Children.Add(button);
            }

            if (question.Multi)
            {
                var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, TranscriptUi.Rem(0.6), 0, 0) };
                var hint = TranscriptUi.Text(question.Hint, TranscriptUi.Rem(0.7), "BrushTextFaint", wrap: false);
                hint.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(hint, 0);
                foot.Children.Add(hint);
                var confirm = TranscriptUi.Pill(question.ConfirmLabel, TranscriptUi.Rem(0.75), "#FFFFFF",
                    ThemeLookup.Brush("BrushPrimary"), padding: new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.3)),
                    weight: FontWeight.SemiBold);
                confirm.IsEnabled = question.Picks.Count > 0;
                confirm.Opacity = confirm.IsEnabled ? 1 : 0.4;
                confirm.Click += async (_, _) => await _vm.AnswerQuestionAsync([.. question.Picks]);
                Grid.SetColumn(confirm, 1);
                foot.Children.Add(confirm);
                options.Children.Add(foot);
            }
            options.Children.Add(free);
        }

        RenderOptions();
        free.TextChanged += (_, _) =>
        {
            if (free.Text == question.FreeText) return;
            question.FreeText = free.Text ?? "";
        };
        free.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var picks = question.FreePicks();
            if (picks is not null)
            {
                await _vm.AnswerQuestionAsync(picks);
            }
            else
            {
                free.Text = "";
                RenderOptions();
            }
        };

        return QuestionCardBorder(stack);
    }

    private Border QuestionCardBorder(Control content) => new()
    {
        MaxWidth = TranscriptUi.CardWidth,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, TranscriptUi.Rem(0.5), 0, 0),
        Background = ThemeLookup.Brush("BrushPrimarySoft"),
        BorderBrush = ThemeLookup.Brush("BrushPrimary"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.6)),
        Child = content,
    };

    // --- compaction ----------------------------------------------------------------------------------------

    private Control BuildCompactionRow(CompactionCardViewModel compaction)
    {
        var icon = TranscriptUi.Icon(compaction.Icon, TranscriptUi.Rem(0.85), compaction.Failed ? "BrushErr" : "BrushTextMuted");
        icon.Margin = new Thickness(0, 0, TranscriptUi.Rem(0.4), 0);
        CompactionText = TranscriptUi.Text(compaction.Text, TranscriptUi.Rem(0.78),
            compaction.Failed ? "BrushErr" : "BrushTextMuted");
        var chevron = compaction.CanExpand
            ? TranscriptUi.Icon(compaction.Expanded ? "chevronUp" : "chevronDown", TranscriptUi.Rem(0.75), "BrushTextFaint")
            : null;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);
        Grid.SetColumn(CompactionText, 1);
        head.Children.Add(CompactionText);
        if (chevron is not null)
        {
            Grid.SetColumn(chevron, 2);
            head.Children.Add(chevron);
        }

        var notice = new Border
        {
            Background = ThemeLookup.Brush("BrushSurface2"),
            BorderBrush = compaction.Failed ? ThemeLookup.Brush("BrushErr") : ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(TranscriptUi.Rem(0.7), TranscriptUi.Rem(0.35)),
            Child = head,
            Cursor = compaction.CanExpand ? new Cursor(StandardCursorType.Hand) : null,
        };

        var body = new MarkdownProse { MaxWidth = TranscriptUi.CardWidth };
        body.SetSource(compaction.Body, TranscriptUi.Rem(0.8125));
        var panel = new CollapsiblePanel { IsExpanded = compaction.Expanded, Child = body };
        if (compaction.CanExpand)
        {
            notice.PointerPressed += (_, _) =>
            {
                compaction.Toggle();
                panel.IsExpanded = compaction.Expanded;
                if (chevron is not null) chevron.Glyph = compaction.Expanded ? "chevronUp" : "chevronDown";
            };
        }

        var inner = new StackPanel { Orientation = Orientation.Vertical, Spacing = TranscriptUi.Rem(0.4) };
        inner.Children.Add(notice);
        inner.Children.Add(panel);
        return new Border
        {
            MaxWidth = TranscriptUi.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = inner,
        };
    }

    private void Pulse(CompactionCardViewModel compaction)
    {
        if (CompactionText is null) return;
        if (_pulse is not null) MotionService.Current.Detach(_pulse);
        var pulse = new CompactionPulse(CompactionText, compaction.Running);
        _pulse = pulse;
        pulse.Start(MotionService.Current.NowMs);
    }

    // --- actions -------------------------------------------------------------------------------------------

    private Control BuildActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.35), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, TranscriptUi.Rem(0.3), 0, 0) };
        var revealed = new List<Control>();

        if (!string.IsNullOrWhiteSpace(_vm.CopySource))
        {
            var copy = ActionButton(Localization.T("chat.copy"), "copy", "BrushTextFaint");
            copy.Click += async (_, _) =>
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard is null) return;
                await top.Clipboard.SetValueAsync(DataFormat.Text, _vm.CopySource);
                if (copy.Content is TextBlock label) label.Text = Localization.T("chat.copied");
                SoundService.Current.Play(SoundKey.Click);
            };
            revealed.Add(copy);
            panel.Children.Add(copy);
        }

        if (!_vm.IsLocal)
        {
            var branch = ActionButton(Localization.T("chat.branch"), "branch", "BrushTextFaint");
            branch.Click += async (_, _) => await _vm.BranchAsync();
            revealed.Add(branch);
            panel.Children.Add(branch);

            if (_vm.IsFailed)
            {
                var retry = ActionButton(Localization.T("chat.retry"), "retry", "BrushTextMuted");
                retry.BorderBrush = ThemeLookup.Brush("BrushBorder");
                retry.Click += (_, _) => _host.ShowRetryConfirm(_vm);
                panel.Children.Add(retry);
            }
            else
            {
                var revert = ActionButton(_vm.RevertArmed ? Localization.T("chat.confirm") : Localization.T("chat.revert"), "undo", "BrushTextFaint");
                if (_vm.RevertArmed)
                {
                    revert.BorderBrush = ThemeLookup.Brush("BrushBorderStrong");
                    revert.Background = ThemeLookup.Brush("BrushSurface2");
                    if (revert.Content is TextBlock label) label.Foreground = ThemeLookup.Brush("BrushErr");
                }
                revert.Click += async (_, _) =>
                {
                    if (!_vm.RevertArmed)
                    {
                        _vm.RevertArmed = true;
                        Refresh();
                        // `.15s` is the web's fade; the armed window itself is 2.6s (see ChatMessageItem.vue).
                        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2.6), DispatcherPriority.Background, (_, _) => { });
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            if (_vm.RevertArmed && !_vm.Busy)
                            {
                                _vm.RevertArmed = false;
                                Refresh();
                            }
                        };
                        timer.Start();
                        return;
                    }
                    _vm.RevertArmed = false;
                    await _vm.RevertAsync();
                };
                revealed.Add(revert);
                panel.Children.Add(revert);
            }
        }

        _reveal = new HoverReveal([.. revealed]);
        return panel;
    }

    private static Button ActionButton(string text, string glyph, string brush)
    {
        var icon = TranscriptUi.Icon(glyph, TranscriptUi.Rem(0.8), brush);
        var label = TranscriptUi.Text(text, TranscriptUi.Rem(0.625), brush, FontWeight.SemiBold, wrap: false);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.2) };
        content.Children.Add(icon);
        content.Children.Add(label);
        return new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderBrush = ThemeLookup.Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(TranscriptUi.Rem(0.35), TranscriptUi.Rem(0.05)),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    /// <summary>
    /// Motion contract #15: the compaction "running" text pulses opacity 1 → .55 → 1 over 1.4s ease-in-out while the
    /// summary is being generated, and holds still otherwise.
    /// </summary>
    private sealed class CompactionPulse(TextBlock target, bool running) : IMotionParticipant
    {
        private bool _active;
        private bool _seenAttached;

        public void Start(double nowMs)
        {
            _active = running;
            if (!_active) return;
            MotionService.Current.Attach(this);
            TickMotion(nowMs);
        }

        public void TickMotion(double nowMs)
        {
            if (!_active) return;
            // A closed window may never raise DetachedFromVisualTree in the headless host: stop pulsing (and detach)
            // once the target has been in the tree and then left it, so a leaked participant cannot touch another
            // thread's control. The first ticks may run before the child itself is attached.
            if (target.IsAttachedToVisualTree()) _seenAttached = true;
            else if (_seenAttached)
            {
                _active = false;
                MotionService.Current.Detach(this);
                return;
            }
            var duration = Math.Max(1, MotionTokens.Pulse);
            var phase = (nowMs % duration) / duration;
            var triangle = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
            target.Opacity = 1 - 0.45 * MotionTokens.InOut.Ease(triangle);
        }
    }
}

/// <summary>Small fluent helper so a built control can be configured inline without a statement body.</summary>
internal static class ControlFluent
{
    public static T With<T>(this T control, Action<T> configure)
    {
        configure(control);
        return control;
    }
}
