using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Composer;

/// <summary>
/// The composer view (web <c>ChatComposer.vue</c>): the attachment/reference strips, the input box with its attach,
/// stop and send controls, the hint row (metrics on the left, branch and the four chips on the right) and the in-window
/// popup layer that hosts the command palette, the <c>@</c> mention popup, the chip popovers, the follow-up mode menu
/// and the context legend. Nothing here animates its appearance: popovers and palettes open instantly like the web,
/// only the send press, the chip hover and the glyph spins read the shared motion clock.
/// </summary>
public partial class ComposerView : UserControl
{
    private const double PopupGap = 8;

    private readonly PressAnimator _press;
    private readonly ChipHoverAnimator _chipHover;
    private readonly Dictionary<ComposerMenu, Button> _chips = [];
    private readonly List<(Control Surface, Control Anchor, HAlign Align, double Gap)> _surfaces = [];
    private ComposerViewModel? _vm;
    private Button? _firstChip;
    private bool _syncing;
    private bool _focused;
    private bool _heightHooked;
    private int _menuIndex;
    private bool _contextHover;

    public ComposerView()
    {
        InitializeComponent();
        _press = new PressAnimator(SendSplit);
        _chipHover = new ChipHoverAnimator();

        Input.TextChanged += (_, _) =>
        {
            if (_vm is null || _syncing) return;
            _vm.Text = Input.Text ?? "";
            UpdateSendPill();
        };
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        Input.AddHandler(KeyUpEvent, OnInputKeyUp, RoutingStrategies.Tunnel);
        Input.GotFocus += (_, _) => ApplyFocus(true);
        Input.LostFocus += (_, _) => ApplyFocus(false);
        Input.PointerPressed += (_, _) => _vm?.UpdateSurfaces(Input.CaretIndex);

        AttachButton.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            await PickFilesAsync();
        };
        StopButton.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (_vm is not null) await _vm.StopAsync();
        };
        SendButton.Click += async (_, _) =>
        {
            if (_vm is null) return;
            SoundService.Current.Play(SoundKey.Click);
            await _vm.SubmitAsync();
            Rebuild();
        };
        ModeButton.Click += (_, _) =>
        {
            _vm?.ToggleMenu(ComposerMenu.Mode);
            _menuIndex = Math.Max(0, ComposerViewModel.DeliveryModes.ToList().FindIndex(m => m.Value == _vm?.Delivery));
            Rebuild();
        };
        SendSplit.PointerPressed += (_, _) => _press.Start();
        SendSplit.PointerEntered += (_, _) => SendSplit.Background = Brush("BrushPrimaryStrong");
        SendSplit.PointerExited += (_, _) => UpdateSendPill();

        Root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) =>
        {
            RebuildSurfaces();
            if (_heightHooked) HeightChanged?.Invoke(Root.Bounds.Height);
        };

        MotionService.Current.Attach(_press);
        MotionService.Current.Attach(_chipHover);
    }

    /// <summary>The view's state: fixture preview or the shell's live composer.</summary>
    public ComposerViewModel? ViewModel => _vm;

    /// <summary>Raised with the measured height of the composer (the shell forwards it to the transcript padding).</summary>
    public event Action<double>? HeightChanged;

    /// <summary>The measured height of everything the band renders (strips, input box and hint row).</summary>
    public double MeasuredHeight => Root.Bounds.Height;

    /// <summary>The multi-line input, exposed for tests and the headless harness.</summary>
    public TextBox InputControl => Input;

    /// <summary>The send half of the pill (press frames read its transform).</summary>
    public Button SendControl => SendButton;

    /// <summary>The input box surface (focus ring geometry assertions).</summary>
    public Border InputSurface => InputBox;

    /// <summary>Children of the popup layer, in z order (palette, mention popup, chip menus, context legend).</summary>
    public Canvas PopupHost => PopupLayer;

    /// <summary>The branch + chip row of the hint row.</summary>
    public StackPanel Chips => ChipsRow;

    /// <summary>Vertical offset of the send pill while it is pressed (frame harness: 0 / 40 / 80ms).</summary>
    public double PressOffset => _press.Offset;

    /// <summary>Border colour of the first chip while the hover preview runs (frame harness: 0 / 60 / 120ms).</summary>
    public Color? PreviewChipBorder => _chipHover.Current;

    public void Attach(ComposerViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.Changed -= Rebuild;
            _vm.FocusRequested -= FocusInput;
        }
        _vm = viewModel;
        _vm.Changed += Rebuild;
        _vm.FocusRequested += FocusInput;
        _heightHooked = true;
        Rebuild();
        HeightChanged?.Invoke(Root.Bounds.Height);
    }

    /// <summary>Re-applies the scaled tokens after a font-size/accent change.</summary>
    public void RefreshAppearance() => Rebuild();

    /// <summary>Focuses the text area (Ctrl+L, after a pick or a failed send).</summary>
    public void FocusInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Input.Focus();
            Input.CaretIndex = Input.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Closes the composer's transient surfaces (Escape). Returns true when one was open.</summary>
    public bool HandleEscape()
    {
        if (_vm is null) return false;
        if (_vm.Menu != ComposerMenu.None)
        {
            _vm.CloseMenu();
            return true;
        }
        if (_vm.MentionOpen)
        {
            _vm.CloseMention();
            return true;
        }
        if (_vm.PaletteOpen)
        {
            _vm.ClosePalette();
            return true;
        }
        if (_contextHover || _vm.ContextPinned)
        {
            _contextHover = false;
            _vm.ContextPinned = false;
            RebuildSurfaces();
            return true;
        }
        return false;
    }

    /// <summary>Clicking outside the composer closes its menus (web document mousedown handler).</summary>
    public void CloseTransient()
    {
        if (_vm is null) return;
        _vm.CloseMenu();
        if (_contextHover)
        {
            _contextHover = false;
            RebuildSurfaces();
        }
    }

    /// <summary>Screenshot harness: loads one deterministic state (idle, focus, attachments, model, agent, permission, reasoning, running, steer, queue, palette, mention).</summary>
    public void ApplyPreviewState(string state)
    {
        if (_vm is null) return;
        _vm.SeedFixtureSession(Fixture.ComposerSession());
        _vm.PreviewState(state);
        _heightHooked = true;
        Rebuild();
        if (state == "focus") FocusInput();
    }

    /// <summary>Frame harness: starts the 80ms send-pill press at the current clock reading.</summary>
    public void BeginPressFrame() => _press.Start();

    /// <summary>Frame harness: starts the 120ms chip hover on the first chip.</summary>
    public void BeginChipHoverFrame()
    {
        if (_firstChip is null) Rebuild();
        if (_firstChip is not null) _chipHover.Start(_firstChip);
    }

    /// <summary>
    /// After the composer's children are arranged, the popups know both their own size and their anchor's rectangle,
    /// which is when they can be placed above it (a canvas child may be arranged off the composer's top edge).
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        RepositionSurfaces();
        return arranged;
    }

    // --- rebuild ------------------------------------------------------------------------------------------

    /// <summary>Rebuilds every surface from the view model (one measured pass).</summary>
    private void Rebuild()
    {
        if (_vm is null) return;
        SyncInputText();
        UpdateSendPill();
        BuildStrips();
        BuildMetrics();
        BuildChips();
        RebuildSurfaces();
        ApplyFocus(_focused);
    }

    private void SyncInputText()
    {
        var text = _vm?.Text ?? "";
        if (Input.Text == text) return;
        _syncing = true;
        try
        {
            Input.Text = text;
            Input.CaretIndex = text.Length;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Send pill state: idle 38% fill, split with the caret while a run is active, disabled without content.</summary>
    private void UpdateSendPill()
    {
        if (_vm is null) return;
        var idle = !_vm.CanSend;
        SendSplit.Background = idle ? Brush("BrushPrimaryIdle") : Brush("BrushPrimary");
        SendSplit.CornerRadius = _vm.IsReplying ? new CornerRadius(10, 0, 0, 10) : new CornerRadius(10);
        SendButton.CornerRadius = _vm.IsReplying ? new CornerRadius(10, 0, 0, 10) : new CornerRadius(10);
        ModeDivider.IsVisible = _vm.IsReplying;
        ModeButton.IsVisible = _vm.IsReplying;
        SendGlyph.Glyph = _vm.SendIcon;
        ToolTip.SetTip(SendButton, _vm.SendTitle);
        ToolTip.SetTip(StopButton, Localization.T("composer.stop"));
        StopButton.IsVisible = _vm.IsReplying;
        SendButton.IsEnabled = _vm.CanSend && !_vm.Steering;
        ModeButton.IsEnabled = _vm.CanSend && !_vm.Steering;
        Input.PlaceholderText = Localization.T("composer.placeholder");
    }

    private void ApplyFocus(bool focused)
    {
        _focused = focused;
        InputBox.BorderBrush = Brush(focused ? "BrushPrimary" : "BrushBorderStrong");
        InputBox.BoxShadow = focused ? BoxShadow("ShadowFocusRing") : default;
    }

    // --- strips -------------------------------------------------------------------------------------------

    private void BuildStrips()
    {
        AttachStrip.Children.Clear();
        RefStrip.Children.Clear();
        if (_vm is null) return;

        foreach (var attachment in _vm.Attachments)
        {
            AttachStrip.Children.Add(AttachmentChip(attachment));
        }
        foreach (var reference in _vm.References)
        {
            RefStrip.Children.Add(ReferenceChip(reference));
        }
        AttachStrip.IsVisible = _vm.Attachments.Count > 0;
        RefStrip.IsVisible = _vm.References.Count > 0;
    }

    /// <summary>Attachment chip: 16rem max, 24x24 r5 thumb, name, size and the remove button (web <c>.attach-chip</c>).</summary>
    private Control AttachmentChip(ComposerAttachment attachment)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4, VerticalAlignment = VerticalAlignment.Center };
        if (attachment.IsImage && TryLoadThumb(attachment.PreviewUrl) is { } bitmap)
        {
            row.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
            });
        }
        else
        {
            row.Children.Add(new SofiaIcon
            {
                Glyph = ComposerFormat.IconForKind(attachment.Kind),
                Size = 14.4,
                Foreground = Brush("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var name = new TextBlock
        {
            Text = attachment.Name,
            FontSize = 12,
            Foreground = Brush("BrushText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(name);
        row.Children.Add(new TextBlock
        {
            Text = attachment.Size,
            FontSize = 12,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var remove = new Button
        {
            Theme = ThemeFor("ComposerIconButton"),
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Content = new SofiaIcon { Glyph = "x", Size = 11.2, Foreground = Brush("BrushTextFaint") },
        };
        ToolTip.SetTip(remove, attachment.Error is { Length: > 0 } error ? error : Localization.T("composer.removeAttachment"));
        remove.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _vm?.RemoveAttachment(attachment.LocalId);
        };
        row.Children.Add(remove);

        var chip = new Border
        {
            Padding = new Thickness(8.8, 4.8, 6.4, 4.8),
            Background = Brush("BrushSurface2"),
            BorderBrush = attachment.Error is { Length: > 0 } ? Brush("BrushErr") : Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = row,
            Margin = new Thickness(0, 0, 0, 0),
            MaxWidth = 256,
        };
        return chip;
    }

    private Control ReferenceChip(ComposerReference reference)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new SofiaIcon
        {
            Glyph = "chat",
            Size = 14.4,
            Foreground = Brush("BrushTextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = "@" + reference.Title,
            FontSize = 12,
            Foreground = Brush("BrushText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 190,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var remove = new Button
        {
            Theme = ThemeFor("ComposerIconButton"),
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Content = new SofiaIcon { Glyph = "x", Size = 11.2, Foreground = Brush("BrushTextFaint") },
        };
        ToolTip.SetTip(remove, Localization.T("composer.removeSessionRef"));
        remove.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _vm?.RemoveReference(reference.Id);
        };
        row.Children.Add(remove);
        return new Border
        {
            Padding = new Thickness(8.8, 4.8, 6.4, 4.8),
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 256,
            Child = row,
        };
    }

    private static Bitmap? TryLoadThumb(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (path.StartsWith("avares://", StringComparison.Ordinal))
            {
                using var assets = Avalonia.Platform.AssetLoader.Open(new Uri(path));
                return new Bitmap(assets);
            }
            if (File.Exists(path)) return new Bitmap(path);
        }
        catch
        {
            // A missing or unsupported preview falls back to the kind icon, like the web's broken image.
        }
        return null;
    }

    // --- metrics and the context meter --------------------------------------------------------------------

    private void BuildMetrics()
    {
        MetricsRow.Children.Clear();
        if (_vm is null) return;

        MetricsRow.Children.Add(Metric("clock", _vm.DurationLabel,
            "How long the last request took (time spent answering Sofia is not counted)"));
        if (_vm.CostAvailable)
        {
            MetricsRow.Children.Add(Metric("dollar", _vm.CostLabel,
                "Session cost (when the provider exposes a cost endpoint)"));
        }
        MetricsRow.Children.Add(Metric("zap", _vm.TokensPerSecLabel, "Generation speed"));
        MetricsRow.Children.Add(Metric("database", _vm.CacheHitLabel + "% cache hit", _vm.CacheTitle));

        if (_vm.Context is { } context) MetricsRow.Children.Add(ContextMeterControl(context));
    }

    /// <summary>One hint readout: 12.8px primary icon + 11px tabular number (web <c>.metric</c>).</summary>
    private Control Metric(string glyph, string text, string tooltip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4.8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new SofiaIcon
        {
            Glyph = glyph,
            Size = 12,
            Foreground = Brush("BrushPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontFeatures = FontFeatureCollection.Parse("tnum"),
            Foreground = Brush("BrushTextMuted"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTip.SetTip(row, tooltip);
        return row;
    }

    /// <summary>The 120x4 segmented bar with its percent label and the hover legend (web <c>.context-meter</c>).</summary>
    private Control ContextMeterControl(ContextMeterView context)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new SofiaIcon
        {
            Glyph = "layers",
            Size = 12,
            Foreground = Brush("BrushPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(SegmentBar(context, 120));
        row.Children.Add(new TextBlock
        {
            Text = context.Percent + "%",
            FontSize = 11,
            FontFeatures = FontFeatureCollection.Parse("tnum"),
            Foreground = Brush("BrushTextMuted"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.PointerEntered += (_, _) =>
        {
            _contextHover = true;
            RebuildSurfaces();
        };
        row.PointerExited += (_, _) =>
        {
            if (_vm?.ContextPinned == true) return;
            _contextHover = false;
            RebuildSurfaces();
        };
        ToolTip.SetTip(row, $"{context.Percent}% of context used");
        return row;
    }

    private Control SegmentBar(ContextMeterView context, double width)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var segment in context.Segments)
        {
            var slice = new Border
            {
                Height = 4,
                Width = segment.Value > 0 ? Math.Max(2, segment.Width / 100 * width) : 0,
                MinWidth = segment.Value > 0 ? 2 : 0,
                Background = SegmentBrush(segment.Color),
            };
            bar.Children.Add(slice);
        }
        return new Border
        {
            Width = width,
            Height = 4,
            CornerRadius = new CornerRadius(999),
            Background = Brush("BrushSurface3"),
            ClipToBounds = true,
            Child = bar,
        };
    }

    private static IBrush SegmentBrush(string color) => color == "primary"
        ? Brush("BrushPrimary")
        : new SolidColorBrush(Color.Parse(color));

    // --- chips --------------------------------------------------------------------------------------------

    private void BuildChips()
    {
        ChipsRow.Children.Clear();
        _chips.Clear();
        _firstChip = null;
        if (_vm is null) return;

        if (_vm.Branch.Length > 0)
        {
            var branchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4.8, VerticalAlignment = VerticalAlignment.Center };
            branchRow.Children.Add(new SofiaIcon
            {
                Glyph = "branch",
                Size = 12.8,
                Foreground = Brush("BrushPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            branchRow.Children.Add(new TextBlock
            {
                Text = _vm.Branch,
                FontFamily = Mono(),
                FontSize = 11.2,
                Foreground = Brush("BrushText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = 148,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var branchChip = new Border
            {
                Padding = new Thickness(8, 2.4, 8, 2.4),
                Background = new SolidColorBrush(Color.Parse("#BD242428")),
                BorderBrush = Brush("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Child = branchRow,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(branchChip, "Git branch");
            ChipsRow.Children.Add(branchChip);
        }

        // permission: icon-only, the tone colour carries the state (blue/beige/brown/red)
        var permission = Chip(ComposerMenu.Permission, "shield", null, caret: false, tone: _vm.PermissionTone,
            padding: new Thickness(6.4, 4), tooltip: $"Perms: {_vm.PermissionLabel}");
        _firstChip = permission;
        ChipsRow.Children.Add(permission);

        ChipsRow.Children.Add(Chip(ComposerMenu.Agent, "bot", null, caret: false, tone: null,
            padding: new Thickness(6.4, 4), tooltip: $"Agent: {_vm.AgentLabel}"));
        ChipsRow.Children.Add(Chip(ComposerMenu.Model, _vm.ModelIcon, _vm.ModelLabel, caret: true, tone: null,
            padding: new Thickness(6.4, 2.4), tooltip: "Model", maxWidth: 160));
        ChipsRow.Children.Add(Chip(ComposerMenu.Reasoning, "zap", _vm.ReasoningLabel, caret: true, tone: null,
            padding: new Thickness(6.4, 2.4), tooltip: "Reasoning", maxWidth: 160));
    }

    private void Toggle(ComposerMenu menu)
    {
        _vm?.ToggleMenu(menu);
        var count = MenuItemCount(menu);
        _menuIndex = menu switch
        {
            ComposerMenu.Permission => Math.Max(0, ComposerViewModel.PermissionModes.ToList().FindIndex(o => o.Value == _vm?.PermissionMode)),
            ComposerMenu.Reasoning => Math.Max(0, ComposerViewModel.ReasoningModes.ToList().FindIndex(o => o.Value == _vm?.ReasoningMode)),
            ComposerMenu.Model => Math.Max(0, _vm?.Models.ToList().FindIndex(m => m.Id == _vm.ModelId) ?? 0),
            ComposerMenu.Agent => Math.Max(0, _vm?.PrimaryAgents.ToList().FindIndex(a => a.Id == _vm.Agent) ?? 0),
            ComposerMenu.Mode => Math.Max(0, ComposerViewModel.DeliveryModes.ToList().FindIndex(m => m.Value == _vm?.Delivery)),
            _ => 0,
        };
        _menuIndex = Math.Clamp(_menuIndex, 0, Math.Max(0, count - 1));
        Rebuild();
    }

    private int MenuItemCount(ComposerMenu menu) => menu switch
    {
        ComposerMenu.Permission => ComposerViewModel.PermissionModes.Count,
        ComposerMenu.Reasoning => ComposerViewModel.ReasoningModes.Count,
        ComposerMenu.Model => _vm?.Models.Count ?? 0,
        ComposerMenu.Agent => _vm?.PrimaryAgents.Count ?? 0,
        ComposerMenu.Mode => ComposerViewModel.DeliveryModes.Count,
        _ => 0,
    };

    private Button Chip(ComposerMenu menu, string glyph, string? label, bool caret, ComposerTone? tone, Thickness padding,
        string tooltip, double maxWidth = double.PositiveInfinity)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = label is null ? 0 : 4.8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new SofiaIcon
        {
            Glyph = glyph,
            Size = label is null ? 12.8 : 12,
            Foreground = tone is { } value ? ToneBrush(value) : Brush("BrushPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (label is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeight.Medium,
                Foreground = Brush("BrushText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = maxWidth,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (caret)
        {
            row.Children.Add(new SofiaIcon
            {
                Glyph = _vm?.Menu == menu ? "chevronDown" : "chevronRight",
                Size = 11.2,
                Foreground = Brush("BrushTextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var chip = new Button { Theme = ThemeFor("ComposerChip"), Content = row, Padding = padding };
        ToolTip.SetTip(chip, tooltip);
        chip.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            Toggle(menu);
        };
        _chips[menu] = chip;
        return chip;
    }

    // --- popup surfaces -----------------------------------------------------------------------------------

    private void RebuildSurfaces()
    {
        PopupLayer.Children.Clear();
        _surfaces.Clear();
        if (_vm is null) return;

        if (_vm.PaletteVisible) AddSurface(PaletteSurface(), InputBox, HAlign.Center);
        if (_vm.MentionVisible) AddSurface(MentionSurface(), InputBox, HAlign.Center);
        if (_vm.Menu != ComposerMenu.None)
        {
            var anchor = _vm.Menu == ComposerMenu.Mode
                ? (Control)SendSplit
                : _chips.TryGetValue(_vm.Menu, out var chip) ? chip : InputBox;
            AddSurface(MenuSurface(_vm.Menu), anchor, HAlign.Right, 6);
        }
        if (_contextHover || _vm.ContextPinned)
        {
            if (_vm.Context is { } context) AddSurface(ContextLegend(context), InputBox, HAlign.Left);
        }
    }

    private enum HAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Adds an in-window surface above its anchor. The composer sits at the bottom of the window, so the surface
    /// unfolds upward (the web's <c>bottom: calc(100% + 6px)</c>) and is only clamped to the window bounds: a canvas
    /// child may use a negative Y to paint over the transcript. Placement runs again after layout, because a popup's
    /// height changes once its rows are constrained to the surface width.
    /// </summary>
    private void AddSurface(Control surface, Control anchor, HAlign align, double gap = PopupGap)
    {
        PopupLayer.Children.Add(surface);
        surface.Measure(new Size(Math.Max(200, Bounds.Width), double.PositiveInfinity));
        _surfaces.Add((surface, anchor, align, gap));
        PlaceSurface(surface, anchor, align, gap);
        // The arranged size (rows constrained to the surface width) decides where the popup's top edge lands; a canvas
        // child is measured before its content wraps, so reposition as soon as it has a real height.
        surface.SizeChanged += (_, _) => PlaceSurface(surface, anchor, align, gap);
    }

    private void PlaceSurface(Control surface, Control anchor, HAlign align, double gap)
    {
        // Before the composer is laid out the anchor has no rectangle and the translation would be (0,0).
        if (anchor.Bounds.Width <= 0 && anchor.Bounds.Height <= 0) return;
        var width = surface.Bounds.Width > 0 ? surface.Bounds.Width : surface.DesiredSize.Width;
        var height = surface.Bounds.Height > 0 ? surface.Bounds.Height : surface.DesiredSize.Height;
        var origin = anchor.TranslatePoint(new Point(0, 0), Root) ?? default;
        var top = TopLevel.GetTopLevel(this);
        var windowOrigin = top is null ? default : this.TranslatePoint(new Point(0, 0), top) ?? default;
        var windowWidth = top?.Bounds.Width ?? Bounds.Width;
        var x = align switch
        {
            HAlign.Left => origin.X,
            HAlign.Right => origin.X + anchor.Bounds.Width - width,
            _ => origin.X + (anchor.Bounds.Width - width) / 2,
        };
        var minX = 8 - windowOrigin.X;
        var maxX = Math.Max(minX, windowWidth - width - 8 - windowOrigin.X);
        x = Math.Clamp(x, minX, maxX);
        var y = origin.Y - gap - height;
        // Keep the surface inside the window's top edge; it may (and usually does) start above the composer.
        y = Math.Max(8 - windowOrigin.Y, y);
        var currentLeft = Canvas.GetLeft(surface);
        var currentTop = Canvas.GetTop(surface);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - x) > 0.1) Canvas.SetLeft(surface, x);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - y) > 0.1) Canvas.SetTop(surface, y);
    }

    private void RepositionSurfaces()
    {
        foreach (var (surface, anchor, align, gap) in _surfaces)
        {
            if (surface.IsVisible) PlaceSurface(surface, anchor, align, gap);
        }
    }

    /// <summary>Palette/popover chrome: surface-2, 1px border-strong, r12, the menu shadow.</summary>
    private Border Surface(Control content, double width, Thickness? padding = null)
    {
        return new Border
        {
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding ?? new Thickness(4),
            BoxShadow = BoxShadow("ShadowMenu"),
            MinWidth = width,
            MaxWidth = width,
            Child = content,
        };
    }

    /// <summary>The <c>/</c> palette: min(340px, 100% - 1rem) wide, max-height 240, z50 (web <c>.cmd-palette</c>).</summary>
    private Control PaletteSurface()
    {
        var panel = new StackPanel { Spacing = 1 };
        var commands = _vm!.FilteredCommands;
        if (commands.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _vm.CommandsLoading
                    ? Localization.T("composer.loadingCommands")
                    : _vm.PaletteQuery.Length > 0
                        ? string.Format(Localization.T("composer.noCommandsMatching"), _vm.PaletteQuery)
                        : Localization.T("composer.noCommands"),
                FontSize = 12,
                Foreground = Brush("BrushTextFaint"),
                Margin = new Thickness(5.6, 3.2, 5.6, 3.2),
            });
        }
        var index = 0;
        foreach (var command in commands)
        {
            var ordinal = index++;
            panel.Children.Add(RowItem(
                active: ordinal == _vm.PaletteIndex,
                focus: ordinal == _vm.PaletteIndex,
                onClick: () =>
                {
                    _vm.PickCommand(command);
                    FocusInput();
                },
                onHover: () => _vm.PaletteIndex = ordinal,
                content: Row(
                    new TextBlock
                    {
                        Text = "/" + command.Name,
                        FontSize = 12,
                        Foreground = Brush("BrushText"),
                        MaxWidth = 96,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = command.Description,
                        FontSize = 11,
                        MaxWidth = 196,
                        Foreground = Brush("BrushTextFaint"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    })));
        }
        var scroll = new ScrollViewer
        {
            MaxHeight = 240,
            Width = 340 - 6.4 * 2 - 2,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
        return Surface(scroll, 340, new Thickness(6.4));
    }

    /// <summary>The <c>@</c> popup: section headers, then agents, sessions and files (web <c>mentionRows</c>).</summary>
    private Control MentionSurface()
    {
        var panel = new StackPanel { Spacing = 1 };
        var rows = _vm!.MentionRows;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var ordinal = i;
            if (row.IsHeader)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Localization.T(row.Header!),
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 0.4,
                    Foreground = Brush("BrushTextFaint"),
                    Margin = new Thickness(5.6, 2.4, 5.6, 1.6),
                });
                continue;
            }

            var content = new List<Control>();
            if (row.Agent is { } agent)
            {
                content.Add(new TextBlock
                {
                    Text = "@" + agent.Id,
                    FontSize = 12,
                    Foreground = Brush("BrushText"),
                    MaxWidth = 110,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (agent.ShortDescription.Length > 0)
                {
                    content.Add(new TextBlock
                    {
                        Text = agent.ShortDescription,
                        FontSize = 11,
                        MaxWidth = 160,
                        Foreground = Brush("BrushTextFaint"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                if (agent.Source is { Length: > 0 } and not "builtin") content.Add(Tag(agent.Source));
            }
            else if (row.Session is { } session)
            {
                content.Add(new TextBlock
                {
                    Text = "@" + session.Title,
                    FontSize = 12,
                    Foreground = Brush("BrushText"),
                    MaxWidth = 200,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                content.Add(new TextBlock
                {
                    Text = "referenced session",
                    FontSize = 11,
                    Foreground = Brush("BrushTextFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                content.Add(new TextBlock
                {
                    Text = "@" + row.File,
                    FontSize = 12,
                    Foreground = Brush("BrushText"),
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                content.Add(new TextBlock
                {
                    Text = "workspace file",
                    FontSize = 11,
                    Foreground = Brush("BrushTextFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var item = row;
            panel.Children.Add(RowItem(
                active: ordinal == _vm.MentionIndex,
                focus: ordinal == _vm.MentionIndex,
                onClick: () =>
                {
                    _vm.PickMention(item);
                    FocusInput();
                },
                onHover: () => _vm.MentionIndex = ordinal,
                content: Row([.. content])));
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 240,
            Width = 340 - 6.4 * 2 - 2,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
        return Surface(scroll, 340, new Thickness(6.4));
    }

    private Control ContextLegend(ContextMeterView context)
    {
        var panel = new StackPanel { Spacing = 4.8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = context.Percent + "%",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            FontFeatures = FontFeatureCollection.Parse("tnum"),
            Foreground = Brush("BrushText"),
        });
        var caption = new TextBlock
        {
            Text = " of context used",
            FontSize = 12,
            Foreground = Brush("BrushTextMuted"),
            Margin = new Thickness(4.8, 0, 0, 0),
        };
        Grid.SetColumn(caption, 1);
        head.Children.Add(caption);
        var range = new TextBlock
        {
            Text = $"~{ComposerFormat.Tokens(_vm?.ContextUsed ?? 0)} / {ComposerFormat.Tokens(_vm?.ContextLimit ?? 0)}",
            FontFamily = Mono(),
            FontSize = 11,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(range, 2);
        head.Children.Add(range);
        panel.Children.Add(head);
        panel.Children.Add(SegmentBar(context, 236 - 20.8));

        foreach (var segment in context.Segments)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                Background = SegmentBrush(segment.Color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var name = new TextBlock
            {
                Text = Localization.T(segment.Label),
                FontSize = 12,
                Foreground = Brush("BrushText"),
                Margin = new Thickness(7.2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var value = new TextBlock
            {
                Text = "~" + ComposerFormat.Tokens(segment.Value),
                FontFamily = Mono(),
                FontSize = 11,
                Foreground = Brush("BrushTextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            panel.Children.Add(row);
        }

        return new Border
        {
            Width = 236,
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10.4, 8.8, 10.4, 8),
            BoxShadow = BoxShadow("ShadowMenu"),
            Child = panel,
        };
    }

    private Control MenuSurface(ComposerMenu menu)
    {
        var panel = new StackPanel { Spacing = 1 };
        var index = 0;
        void Add(Control content, bool active, Func<Task> pick)
        {
            var ordinal = index++;
            panel.Children.Add(RowItem(
                active: active,
                focus: ordinal == _menuIndex,
                onClick: () => _ = pick(),
                onHover: () =>
                {
                    _menuIndex = ordinal;
                    RebuildSurfaces();
                },
                content: content));
        }

        switch (menu)
        {
            case ComposerMenu.Permission:
                foreach (var option in ComposerViewModel.PermissionModes)
                {
                    Add(OptionRow("shield", option.Label, option.Description, option.Tone,
                            option.Value == _vm!.PermissionMode, badge: null),
                        option.Value == _vm!.PermissionMode,
                        () => PickOptionAsync(() => _vm!.SetPermissionAsync(option.Value)));
                }
                break;
            case ComposerMenu.Reasoning:
                foreach (var option in ComposerViewModel.ReasoningModes)
                {
                    Add(OptionRow("zap", option.Label, option.Description, null,
                            option.Value == _vm!.ReasoningMode, badge: null),
                        option.Value == _vm!.ReasoningMode,
                        () => PickOptionAsync(() => _vm!.SetReasoningAsync(option.Value)));
                }
                break;
            case ComposerMenu.Model:
                foreach (var model in _vm!.Models)
                {
                    Add(OptionRow(model.Icon, model.Label, model.Description, null, model.Id == _vm.ModelId, model.Badge),
                        model.Id == _vm.ModelId,
                        () => PickOptionAsync(() => _vm!.SetModelAsync(model.Id)));
                }
                break;
            case ComposerMenu.Agent:
                if (_vm!.PrimaryAgents.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Localization.T("composer.noPrimaryAgents"),
                        FontSize = 12,
                        Foreground = Brush("BrushTextFaint"),
                        Margin = new Thickness(8, 3.2, 8, 3.2),
                    });
                }
                foreach (var agent in _vm.PrimaryAgents)
                {
                    Add(OptionRow("bot", agent.Name, agent.ShortDescription, null, agent.Id == _vm.Agent,
                            agent.Source is { Length: > 0 } and not "builtin" ? agent.Source : null),
                        agent.Id == _vm.Agent,
                        () => PickOptionAsync(() => _vm!.SetAgentAsync(agent.Id)));
                }
                break;
            case ComposerMenu.Mode:
                foreach (var mode in ComposerViewModel.DeliveryModes)
                {
                    Add(OptionRow(mode.Icon, mode.Label, mode.Description, null, mode.Value == _vm!.Delivery, null),
                        mode.Value == _vm!.Delivery,
                        () =>
                        {
                            _vm!.Delivery = mode.Value;
                            SoundService.Current.Play(SoundKey.Select);
                            _vm.CloseMenu();
                            Rebuild();
                            FocusInput();
                            return Task.CompletedTask;
                        });
                }
                break;
        }

        var width = menu is ComposerMenu.Model or ComposerMenu.Agent ? 280 : menu == ComposerMenu.Reasoning ? 240 : 260;
        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            Width = width - 10,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
        return Surface(scroll, width);
    }

    private async Task PickOptionAsync(Func<Task> pick)
    {
        await pick();
        SoundService.Current.Play(SoundKey.Select);
        _vm?.CloseMenu();
        Rebuild();
        FocusInput();
    }

    /// <summary>One option row: 15.2px icon in the item's tone, label (+ badge), description and the check mark.</summary>
    private Control OptionRow(string glyph, string label, string description, ComposerTone? tone, bool active, string? badge)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(new SofiaIcon
        {
            Glyph = glyph,
            Size = 15.2,
            Foreground = active ? Brush("BrushPrimary") : tone is { } value ? ToneBrush(value) : Brush("BrushTextMuted"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1.6, 0, 0),
        });

        var body = new StackPanel { Spacing = 1.6 };
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        labelRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = Brush("BrushText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        if (badge is { Length: > 0 }) labelRow.Children.Add(Tag(badge));
        body.Children.Add(labelRow);
        if (description.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = Brush("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(body);

        if (active)
        {
            var check = new SofiaIcon
            {
                Glyph = "check",
                Size = 14.4,
                Foreground = Brush("BrushPrimary"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1.6, 0, 0),
            };
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }
        return row;
    }

    /// <summary>Selectable row of a popup (hover + keyboard highlight, active fill).</summary>
    private Button RowItem(bool active, bool focus, Action onClick, Action? onHover, Control content)
    {
        var button = new Button
        {
            Theme = ThemeFor("ComposerMenuRow"),
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (active) button.Classes.Add("active");
        if (focus) button.Classes.Add("focus");
        button.Click += (_, _) => onClick();
        button.PointerEntered += (_, _) => onHover?.Invoke();
        return button;
    }

    private static Control Tag(string text) => new Border
    {
        Padding = new Thickness(4.8, 0.8, 4.8, 0.8),
        CornerRadius = new CornerRadius(999),
        Background = Brush("BrushSurface"),
        Child = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.4,
            Foreground = Brush("BrushTextFaint"),
        },
    };

    // --- input handling -----------------------------------------------------------------------------------

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.Text = Input.Text ?? "";
    }

    private void OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) _vm?.UpdateSurfaces(Input.CaretIndex);
    }

    /// <summary>
    /// The composer's key route (web <c>onKeydown</c>): the follow-up menu, then the mention popup, then the palette,
    /// then Enter to send. Shift+Enter and an IME-composed key never send (<see cref="ComposerKeys"/>).
    /// </summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var action = ComposerKeys.Decide(
            e.Key,
            e.KeyModifiers,
            _vm.MentionVisible,
            _vm.PaletteOpen && !_vm.MentionVisible,
            _vm.FilteredCommands.Count > 0,
            _vm.Menu != ComposerMenu.None,
            imeComposing: false);
        switch (action)
        {
            case ComposerKeyAction.None:
                return;
            case ComposerKeyAction.MenuClose:
                _vm.CloseMenu();
                e.Handled = true;
                return;
            case ComposerKeyAction.MentionDown:
                _vm.MoveMention(1);
                e.Handled = true;
                return;
            case ComposerKeyAction.MentionUp:
                _vm.MoveMention(-1);
                e.Handled = true;
                return;
            case ComposerKeyAction.MentionPick:
                _vm.PickHighlightedMention();
                e.Handled = true;
                return;
            case ComposerKeyAction.MentionClose:
                _vm.CloseMention();
                e.Handled = true;
                return;
            case ComposerKeyAction.PaletteClose:
                _vm.ClosePalette();
                e.Handled = true;
                return;
            case ComposerKeyAction.PaletteDown:
                _vm.MovePalette(1);
                e.Handled = true;
                return;
            case ComposerKeyAction.PaletteUp:
                _vm.MovePalette(-1);
                e.Handled = true;
                return;
            case ComposerKeyAction.PalettePick:
                if (_vm.PickPaletteCommand())
                {
                    Input.CaretIndex = _vm.Text.Length;
                }
                e.Handled = true;
                return;
            case ComposerKeyAction.Send:
                e.Handled = true;
                _ = SubmitAsync();
                return;
        }
    }

    /// <summary>Arrow keys drive an open chip popover (the web listens for keys in the capture phase).</summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || _vm.Menu == ComposerMenu.None) return;
        var count = MenuItemCount(_vm.Menu);
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _menuIndex = (_menuIndex + 1) % count;
                e.Handled = true;
                RebuildSurfaces();
                break;
            case Key.Up when count > 0:
                _menuIndex = (_menuIndex - 1 + count) % count;
                e.Handled = true;
                RebuildSurfaces();
                break;
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) == 0:
                e.Handled = true;
                _ = PickMenuIndexAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                _vm.CloseMenu();
                Rebuild();
                break;
        }
    }

    private async Task PickMenuIndexAsync()
    {
        if (_vm is null) return;
        switch (_vm.Menu)
        {
            case ComposerMenu.Permission when _menuIndex < ComposerViewModel.PermissionModes.Count:
                await PickOptionAsync(() => _vm.SetPermissionAsync(ComposerViewModel.PermissionModes[_menuIndex].Value));
                break;
            case ComposerMenu.Reasoning when _menuIndex < ComposerViewModel.ReasoningModes.Count:
                await PickOptionAsync(() => _vm.SetReasoningAsync(ComposerViewModel.ReasoningModes[_menuIndex].Value));
                break;
            case ComposerMenu.Model when _menuIndex < _vm.Models.Count:
                await PickOptionAsync(() => _vm.SetModelAsync(_vm.Models[_menuIndex].Id));
                break;
            case ComposerMenu.Agent when _menuIndex < _vm.PrimaryAgents.Count:
                await PickOptionAsync(() => _vm.SetAgentAsync(_vm.PrimaryAgents[_menuIndex].Id));
                break;
            case ComposerMenu.Mode when _menuIndex < ComposerViewModel.DeliveryModes.Count:
                _vm.Delivery = ComposerViewModel.DeliveryModes[_menuIndex].Value;
                _vm.CloseMenu();
                SoundService.Current.Play(SoundKey.Select);
                Rebuild();
                break;
        }
    }

    // --- sending and files ---------------------------------------------------------------------------------

    private async Task SubmitAsync()
    {
        if (_vm is null) return;
        await _vm.SubmitAsync();
        Rebuild();
        FocusInput();
    }

    /// <summary>Desktop attachment picker. The browser head (044) pushes bytes through the view model instead.</summary>
    private async Task PickFilesAsync()
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.T("composer.attach"),
            AllowMultiple = true,
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) _vm.AddAttachment(path);
        }
        if (files.Count > 0) Rebuild();
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(child);
        }
        return panel;
    }

    private static ControlTheme? ThemeFor(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as ControlTheme : null;

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static FontFamily Mono() =>
        Application.Current?.TryFindResource("FontFamilyMono", out var value) == true && value is FontFamily family
            ? family
            : new FontFamily("Consolas");

    private static BoxShadows BoxShadow(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is BoxShadows shadows
            ? shadows
            : default;

    private static IBrush ToneBrush(ComposerTone tone) => tone switch
    {
        ComposerTone.Info => Brush("BrushInfo"),
        ComposerTone.Detail => Brush("BrushDetail"),
        ComposerTone.Err => Brush("BrushErr"),
        ComposerTone.Warn => Brush("BrushWarn"),
        _ => Brush("BrushPrimary"),
    };

    // --- motion -------------------------------------------------------------------------------------------

    /// <summary>Motion contract #2: the send pill translates down 1px over 80ms and springs back when released.</summary>
    private sealed class PressAnimator(Control target) : IMotionParticipant
    {
        private double _start;
        private bool _running;

        public double Offset { get; private set; }

        public void Start()
        {
            _start = MotionService.Current.NowMs;
            _running = true;
            MotionService.Current.Attach(this);
            TickMotion(_start);
        }

        public void TickMotion(double nowMs)
        {
            if (!_running) return;
            var duration = Math.Max(1, MotionTokens.Press.TotalMilliseconds);
            var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
            Offset = MotionTokens.Standard.Ease(progress);
            target.RenderTransform = new TranslateTransform(0, Offset);
            if (progress < 1) return;
            _running = false;
            MotionService.Current.Detach(this);
            // Released: the CSS `:active` transform springs back (no transition on the way out).
            Dispatcher.UIThread.Post(() =>
            {
                if (_running) return;
                Offset = 0;
                target.RenderTransform = new TranslateTransform(0, 0);
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Motion contract #1 on a chip: border and fill move from the rest state to the hover state over 120ms with the
    /// web's `ease`. The theme's own transitions drive real pointer hovers; the harness starts this one so the
    /// stop-motion frames measure the same duration and curve.
    /// </summary>
    private sealed class ChipHoverAnimator : IMotionParticipant
    {
        private Border? _target;
        private double _start;
        private bool _running;

        public Color? Current { get; private set; }

        public void Start(Button chip)
        {
            chip.ApplyTemplate();
            _target = chip.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            if (_target is null) return;
            _start = MotionService.Current.NowMs;
            _running = true;
            MotionService.Current.Attach(this);
            TickMotion(_start);
        }

        public void TickMotion(double nowMs)
        {
            if (!_running || _target is null) return;
            var duration = Math.Max(1, MotionTokens.Hover.TotalMilliseconds);
            var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
            var eased = MotionTokens.Standard.Ease(progress);
            var from = Color.Parse("#33333A");
            var to = Color.Parse("#43434B");
            Current = Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * eased),
                (byte)Math.Round(from.G + (to.G - from.G) * eased),
                (byte)Math.Round(from.B + (to.B - from.B) * eased));
            _target.BorderBrush = new SolidColorBrush(Current.Value);
            if (progress < 1) return;
            _running = false;
            MotionService.Current.Detach(this);
        }
    }
}
