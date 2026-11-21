using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Settings;

/// <summary>
/// SettingsPanel.vue ported: the z2000 settings modal with the seven tabs (General, Models, Skills, MCP, Language servers,
/// Shortcuts, Appearance). Local preferences live in <see cref="LocalPreferences"/> (<c>sofia.settings.v1</c>); server
/// settings go through <c>GET/PATCH /api/config</c>, providers, models, skills, MCP and LSP through
/// <see cref="Sofia.Client.ISofiaBackend"/>. The modal opens instantly, like the web.
/// </summary>
public partial class SettingsDialog : UserControl
{
    /// <summary>The seven tabs, in the web's order, with the icons of <c>SettingsPanel.vue</c>.</summary>
    public enum SettingsTab
    {
        General,
        Models,
        Skills,
        Mcp,
        Lsp,
        Shortcuts,
        Appearance,
    }

    private static readonly (SettingsTab Id, string Key, string Icon)[] Tabs =
    [
        (SettingsTab.General, "settings.tab.general", "sliders"),
        (SettingsTab.Models, "settings.tab.models", "cpu"),
        (SettingsTab.Skills, "settings.tab.skills", "sparkles"),
        (SettingsTab.Mcp, "settings.tab.mcp", "plug"),
        (SettingsTab.Lsp, "settings.tab.lsp", "code"),
        (SettingsTab.Shortcuts, "settings.tab.shortcuts", "keyboard"),
        (SettingsTab.Appearance, "settings.tab.appearance", "palette"),
    ];

    private ShellViewModel? _vm;
    private SettingsTab _tab = SettingsTab.General;

    // server state
    private SettingsConfig? _config;
    private string _configError = "";
    private List<ModelRow> _models = [];
    private List<ProviderRow> _providers = [];
    private List<PresetRow> _presets = [];
    private List<SkillRow> _skills = [];
    private List<McpRow> _mcp = [];
    private List<LspRow> _lsp = [];
    private List<BrowserRow> _browsers = [];
    private bool _browserHeadless;
    private string _browserAuto = "";
    private string _loadError = "";
    private bool _loading;

    // tab-local draft state
    private bool _addProvider;
    private string? _presetId;
    private readonly List<ModelRow> _probeModels = [];
    private string _probeNote = "";
    private string _probeError = "";
    private string _newName = "";
    private string _newType = "openai";
    private string _newBaseUrl = "";
    private string _newKey = "";
    private bool _probing;
    private string? _copilotCode;
    private string _copilotUri = "";
    private string _copilotNote = "";
    private string? _managingProvider;
    private string _modelFilter = "";
    private bool _addMcp;
    private string _mcpName = "";
    private string _mcpTransport = "command";
    private string _mcpCommand = "";
    private string _mcpUrl = "";
    private string _mcpToken = "";
    private string _mcpError = "";
    private bool _addingMcp;
    private string _lspError = "";
    private string? _lspBusy;
    private Dictionary<string, string> _lspCommands = new(StringComparer.Ordinal);
    private string? _recordingId;
    private string _recordError = "";
    private bool _previewing;
    private string _skillsError = "";
    private string? _skillOpen;
    private string _modelsError = "";

    public SettingsDialog()
    {
        InitializeComponent();
        Title.Text = Localization.T("settings.title");
        ResetButton.Content = Ui.Text(Localization.T("settings.reset"), 12, "BrushText");
        DoneButton.Content = Ui.Text(Localization.T("settings.done"), 12, "BrushPrimaryInk", Avalonia.Media.FontWeight.SemiBold);
        CloseButton.Click += (_, _) => Close();
        DoneButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) => ResetLocal();
        Overlay.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, Overlay)) Close();
        };
        ShortcutService.Current.Changed += OnShortcutsChanged;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the dialog opened or closed (the shell suspends the global shortcuts while it is open).</summary>
    public event Action<bool>? OpenChanged;

    /// <summary>Raised when the user asks for the pairing dialog.</summary>
    public event Action? PairingRequested;

    public bool IsOpen => IsVisible;

    public SettingsTab CurrentTab => _tab;

    /// <summary>Screenshot harness: scrolls the tab body (the server block sits below the fold at 1080p).</summary>
    public void PreviewScroll(double offset) => BodyScroll.Offset = new Vector(0, offset);

    /// <summary>Screenshot harness: opens the LSP install confirmation for a server id (the risky host action).</summary>
    public void PreviewInstallConfirm(string serverId)
    {
        _tab = SettingsTab.Lsp;
        _installConfirm = serverId;
        Build();
    }

    /// <summary>Fixture/screenshot entry: no backend, deterministic rows.</summary>
    public bool FixtureMode { get; set; }

    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
    }

    /// <summary>
    /// Fixture/screenshot entry: deterministic server state for every tab, so the gate can open any tab without a
    /// server (the values mirror what the local test server returns).
    /// </summary>
    public void LoadFixture()
    {
        FixtureMode = true;
        _loading = false;
        _loadError = "";
        _config = new SettingsConfig
        {
            DefaultModel = "unsloth/nemotron",
            Shell = "",
            AutoCompact = true,
            CompactThreshold = 0.75,
            PruneToolResults = true,
            PruneThreshold = 0.6,
            PruneKeepSteps = 3,
            Discovery = true,
            Knowledge = true,
            SearchProvider = "searxng",
            SearxngUrl = "http://home-server.ivansc.dev.br:9777",
            SandboxAvailable = true,
            SandboxBackend = "job-object",
            ApplyPatch = "auto",
            ReadBeforeEdit = true,
            DefaultBrowser = "auto",
            LspEnabled = true,
            LspAutoDiagnostics = true,
            DataDir = @"C:\Users\ivans\.sofia",
            ConfigPath = @"C:\Users\ivans\.sofia\config.json",
        };
        _models =
        [
            new("unsloth/nemotron", "unsloth", "Nemotron 3.5 Bolt", "Unsloth Studio", true, "128K ctx", true, false, true, true),
            new("ollama/qwen3", "ollama", "Qwen3 30B", "Ollama", true, "32K ctx", true, false, true, true),
            new("openai/gpt-5", "openai", "GPT-5", "OpenAI", false, "400K ctx", true, true, false, false),
        ];
        _providers =
        [
            new("unsloth", "Unsloth Studio", "local", "openai", "http://127.0.0.1:8888/v1", "connected", "3 models loaded", true, false, false, 3, false, "flask"),
            new("ollama", "Ollama", "local", "openai", "http://127.0.0.1:11434/v1", "connected", "7 models", true, false, false, 7, false, "box"),
            new("openai", "OpenAI", "cloud", "openai", "https://api.openai.com/v1", "connected", "sk-…", true, true, true, 12, true, "cloud"),
        ];
        _presets =
        [
            new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "", "One key for many models."),
            new("github-copilot", "GitHub Copilot", "openai", "https://api.githubcopilot.com", "", "github-device", ""),
            new("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", "", ""),
        ];
        _skills =
        [
            new("brainstorming", "Brainstorming", "Turns an idea into a short design brief before any code is written.", "# Brainstorming\n\nAsk one question at a time.", "global"),
            new("verification", "Verification", "Runs the project's build and tests after a change and reports the exact command.", "# Verification\n\n1. Build\n2. Test", "global"),
        ];
        _mcp =
        [
            new("mcp_1", "brave-search", "command", "npx @modelcontextprotocol/server-brave-search", "", "connected", 4, ""),
            new("mcp_2", "docs", "web", "", "https://mcp.example.com/mcp", "error", 0, "Connection refused"),
        ];
        _lsp =
        [
            new("typescript", "TypeScript", "typescript-language-server --stdio", "running", "npm i -g typescript-language-server", ["ts", "tsx"], [@"D:\repos\opensource\sofia"], ""),
            new("csharp", "C#", "roslyn-language-server --stdio", "missing", "dotnet tool install -g roslyn-language-server", ["cs"], [], ""),
        ];
        _browsers = [new("chrome", "Google Chrome"), new("edge", "Microsoft Edge")];
        _browserAuto = "chrome";
        Build();
    }

    /// <summary>Opens the dialog on a tab (gear, Ctrl+, and the screenshots use it).</summary>
    public void Open(SettingsTab tab = SettingsTab.General, bool load = true)
    {
        _tab = tab;
        IsVisible = true;
        ShortcutService.Current.Suspended = true;
        SoundService.Current.Play(SoundKey.Select);
        Build();
        OpenChanged?.Invoke(true);
        if (load && !FixtureMode) _ = LoadAsync();
    }

    public void Close()
    {
        StopRecording();
        ShortcutService.Current.Suspended = false;
        IsVisible = false;
        OpenChanged?.Invoke(false);
    }

    /// <summary>Escape closes the dialog (and cancels a shortcut recording first, like the web).</summary>
    public bool HandleEscape()
    {
        if (!IsVisible) return false;
        if (_recordingId is not null)
        {
            StopRecording();
            Build();
            return true;
        }
        Close();
        return true;
    }

    /// <summary>Re-applies labels after a locale switch; the tab rows are rebuilt from the cached state.</summary>
    public void RefreshLabels()
    {
        Title.Text = Localization.T("settings.title");
        ResetButton.Content = Ui.Text(Localization.T("settings.reset"), 12, "BrushText");
        DoneButton.Content = Ui.Text(Localization.T("settings.done"), 12, "BrushPrimaryInk", Avalonia.Media.FontWeight.SemiBold);
        if (IsVisible) Build();
    }

    // --- loading -------------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _loading = true;
        _loadError = "";
        try
        {
            var configTask = backend.ConfigAsync();
            var modelsTask = backend.ModelsAsync();
            var providersTask = backend.ProvidersAsync();
            var presetsTask = backend.ProviderPresetsAsync();
            var skillsTask = backend.SkillsAsync(null);
            var mcpTask = backend.McpServersAsync();
            var lspTask = backend.LspServersAsync();
            var browsersTask = backend.BrowsersAsync();
            await Task.WhenAll(configTask, modelsTask, providersTask, presetsTask, skillsTask, mcpTask, lspTask, browsersTask);

            _config = SettingsConfig.From(await configTask);
            _models = [.. J.Items(await modelsTask).Select(ModelRow.From)];
            _providers = [.. J.Items(await providersTask).Select(ProviderRow.From)];
            _presets = [.. J.Items(await presetsTask).Select(PresetRow.From)];
            _skills = [.. J.Items(await skillsTask).Select(SkillRow.From)];
            _mcp = [.. J.Items(await mcpTask).Select(McpRow.From)];
            _lsp = [.. J.Items(await lspTask).Select(LspRow.From)];
            var browsers = await browsersTask;
            _browsers = [.. J.Items(J.Obj(browsers, "engines")).Select(BrowserRow.From)];
            _browserHeadless = J.Bool(browsers, "headless");
            _browserAuto = J.Str(browsers, "auto");
            foreach (var server in _lsp) _lspCommands.TryAdd(server.Id, "");
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }
        finally
        {
            _loading = false;
            Build();
        }
    }

    private async Task ReloadModelsAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            _models = [.. J.Items(await backend.ModelsAsync()).Select(ModelRow.From)];
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
    }

    // --- shell ---------------------------------------------------------------------------------------------

    private void Build()
    {
        BuildTabs();
        TabBody.Children.Clear();
        if (_vm is null && !FixtureMode)
        {
            TabBody.Children.Add(Note(Localization.T("pair.error")));
            return;
        }
        if (_loadError.Length > 0) TabBody.Children.Add(ErrorNote(_loadError));
        switch (_tab)
        {
            case SettingsTab.General:
                BuildGeneral();
                break;
            case SettingsTab.Models:
                BuildModels();
                break;
            case SettingsTab.Skills:
                BuildSkills();
                break;
            case SettingsTab.Mcp:
                BuildMcp();
                break;
            case SettingsTab.Lsp:
                BuildLsp();
                break;
            case SettingsTab.Shortcuts:
                BuildShortcuts();
                break;
            default:
                BuildAppearance();
                break;
        }
    }

    private void BuildTabs()
    {
        TabRail.Children.Clear();
        foreach (var (id, key, icon) in Tabs)
        {
            var active = id == _tab;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
            content.Children.Add(Ui.Icon(icon, 15.2, active ? "BrushPrimary" : "BrushTextMuted"));
            content.Children.Add(Ui.Text(Localization.T(key), 13, active ? "BrushPrimary" : "BrushTextMuted"));
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(11.2, 6.4),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = Ui.Brush(active ? "BrushPrimarySoft" : "BrushTransparent"),
            };
            var captured = id;
            button.Click += (_, _) =>
            {
                SoundService.Current.Play(SoundKey.Click);
                _tab = captured;
                Build();
            };
            TabRail.Children.Add(button);
        }
    }

    private void ResetLocal()
    {
        var prefs = LocalPreferences.Current;
        prefs.Reset();
        if (_vm is not null) _vm.Language = Localization.Current;
        _vm?.Theme.ApplyAll();
        if (_vm is not null) _vm.RaiseAppearanceChanged();
        Build();
    }

    // --- row vocabulary ------------------------------------------------------------------------------------

    private static Control Row(string title, string sub, Control? right = null, double rightWidth = 0)
    {
        var text = new StackPanel { Spacing = 1.6, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(Localization.T(title), 13, "BrushText", Avalonia.Media.FontWeight.Medium));
        if (sub.Length > 0)
        {
            var subText = Ui.Text(Localization.T(sub), 11.5, "BrushTextFaint");
            subText.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(subText);
        }
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 4.8, 0, 4.8),
        };
        grid.Children.Add(text);
        if (right is not null)
        {
            if (rightWidth > 0) right.Width = rightWidth;
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    private Control SwitchRow(string title, string sub, bool value, Func<bool, Task> set)
    {
        var toggle = Ui.Switch(value, on => _ = set(on));
        return Row(title, sub, toggle);
    }

    private Control TextRow(string title, string sub, string value, Func<string, Task> set, string placeholder = "")
    {
        var box = Ui.Field(value, placeholder);
        box.Width = 200;
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            _ = set(box.Text ?? "");
        };
        box.LostFocus += (_, _) => _ = set(box.Text ?? "");
        return Row(title, sub, box);
    }

    private Control SelectRow(string title, string sub, IReadOnlyList<(string Value, string Label)> options,
        string selected, Func<string, Task> set)
    {
        var combo = new ComboBox { Width = 220, FontSize = 12.5, Background = Ui.Brush("BrushSurface2") };
        foreach (var (value, label) in options) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var index = 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value != selected) continue;
            index = i;
            break;
        }
        combo.SelectedIndex = options.Count > 0 ? index : -1;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value } && value != selected) _ = set(value);
        };
        return Row(title, sub, combo);
    }

    private Control NumberRow(string title, string sub, string value, Func<string, Task> set, bool enabled = true)
    {
        var box = Ui.Field(value);
        box.Width = 80;
        box.IsEnabled = enabled;
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            _ = set(box.Text ?? "");
        };
        box.LostFocus += (_, _) => _ = set(box.Text ?? "");
        return Row(title, sub, box);
    }

    private static Control Section(string key)
    {
        var label = Ui.SectionLabel(Localization.T(key));
        label.Margin = new Thickness(0, 16, 0, 8);
        return label;
    }

    private static Control Note(string text)
    {
        var note = Ui.Text(text, 12, "BrushTextFaint");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 6.4, 0, 6.4);
        return note;
    }

    private static Control ErrorNote(string text)
    {
        var note = Ui.Text(text, 12, "BrushErr");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 6.4, 0, 6.4);
        return note;
    }

    private static Control Intro(string text)
    {
        var intro = Ui.Text(text, 12.5, "BrushTextMuted");
        intro.TextWrapping = TextWrapping.Wrap;
        intro.Margin = new Thickness(0, 0, 0, 12);
        return intro;
    }

    private static Border Card(Control child) => new()
    {
        Child = child,
        Padding = new Thickness(9.6, 8.8),
        Margin = new Thickness(0, 0, 0, 6.4),
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        BorderBrush = Ui.Brush("BrushBorder"),
        Background = Ui.Brush("BrushSurface2"),
    };

    private static Control KindTag(string text)
    {
        var label = Ui.Text(text, 9.6, "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold);
        label.LetterSpacing = 0.6;
        return new Border
        {
            Child = label,
            Padding = new Thickness(5.6, 0.8),
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush("BrushSurface3"),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Control CardHead(string glyph, string name, Control? tag, string sub, Control side)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8.8 };
        grid.Children.Add(Ui.Icon(glyph, 16, "BrushTextMuted"));
        var text = new StackPanel { Spacing = 1.6 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
        if (tag is not null) titleRow.Children.Add(tag);
        titleRow.Children.Add(Ui.Text(name, 13, "BrushText", Avalonia.Media.FontWeight.SemiBold));
        text.Children.Add(titleRow);
        if (sub.Length > 0)
        {
            var subText = Ui.Truncate(sub, 11.5, "BrushTextFaint");
            ToolTip.SetTip(subText, sub);
            text.Children.Add(subText);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(side, 2);
        side.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(side);
        return grid;
    }

    // --- config patching -----------------------------------------------------------------------------------

    private async Task PatchAsync(ConfigPatch patch)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            _configError = "";
            var result = await backend.PatchConfigAsync(patch);
            _config = SettingsConfig.From(result);
            _models = [.. J.Items(await backend.ModelsAsync()).Select(ModelRow.From)];
        }
        catch (Exception ex)
        {
            // The server's own validation message reaches the row, never a generic one.
            _configError = ex.Message;
        }
        Build();
    }

    private void OnShortcutsChanged()
    {
        if (!IsVisible || _tab != SettingsTab.Shortcuts) return;
        Dispatcher.UIThread.Post(Build);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_recordingId is null || !IsVisible) return;
        var combo = ShortcutService.ComboFromKey(e.Key, e.KeyModifiers);
        var bare = e.KeyModifiers == KeyModifiers.None;
        if (bare && e.Key == Key.Escape)
        {
            e.Handled = true;
            StopRecording();
            Build();
            return;
        }
        if (bare && e.Key is Key.Back or Key.Delete)
        {
            e.Handled = true;
            ShortcutService.Current.SetBinding(_recordingId, "");
            StopRecording();
            Build();
            return;
        }
        if (combo is null) return;
        e.Handled = true;
        if (!ShortcutService.IsValidCombo(combo))
        {
            _recordError = Localization.T("shortcuts.needModifier");
            Build();
            return;
        }
        var conflict = ShortcutService.Current.ConflictFor(_recordingId, combo);
        if (conflict is not null)
        {
            _recordError = Localization.T("shortcuts.conflict", ("action", Localization.T($"shortcut.{conflict.Id}")));
            Build();
            return;
        }
        ShortcutService.Current.SetBinding(_recordingId, combo);
        StopRecording();
        Build();
    }

    private void StartRecording(string id)
    {
        StopRecording();
        _recordingId = id;
        _recordError = "";
        ShortcutService.Current.Recording = true;
        Build();
    }

    private void StopRecording()
    {
        _recordingId = null;
        _recordError = "";
        ShortcutService.Current.Recording = false;
    }

    // --- general tab ---------------------------------------------------------------------------------------

    private void BuildGeneral()
    {
        TabBody.Children.Add(Intro(Localization.T("settings.generalIntro")));
        var prefs = LocalPreferences.Current.Data;
        var general = prefs.General;

        TabBody.Children.Add(SwitchRow("settings.stream", "settings.streamSub", general.StreamResponses, on =>
        {
            general.StreamResponses = on;
            LocalPreferences.Current.Save(apply: false);
            return Task.CompletedTask;
        }));
        TabBody.Children.Add(SwitchRow("settings.confirmDestructive", "settings.confirmDestructiveSub",
            general.ConfirmDestructive, on =>
            {
                general.ConfirmDestructive = on;
                LocalPreferences.Current.Save(apply: false);
                return Task.CompletedTask;
            }));
        TabBody.Children.Add(SwitchRow("settings.autoUpdate", "settings.autoUpdateSub", general.AutoUpdate, on =>
        {
            general.AutoUpdate = on;
            LocalPreferences.Current.Save(apply: false);
            return Task.CompletedTask;
        }));
        TabBody.Children.Add(SwitchRow("settings.telemetry", "settings.telemetrySub", general.Telemetry, on =>
        {
            general.Telemetry = on;
            LocalPreferences.Current.Save(apply: false);
            return Task.CompletedTask;
        }));
        TabBody.Children.Add(SwitchRow("settings.sound", "settings.soundSub", SoundService.Current.Enabled, on =>
        {
            general.SoundEffects = on;
            LocalPreferences.Current.Save();
            Build();
            return Task.CompletedTask;
        }));

        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = SoundService.Current.Volume,
            Width = 160,
            IsEnabled = SoundService.Current.Enabled,
        };
        var volumeLabel = Ui.Text($"{Math.Round(SoundService.Current.Volume)}%", 11.5, "BrushTextFaint");
        volume.ValueChanged += (_, args) =>
        {
            general.SoundVolume = Math.Round(args.NewValue);
            volumeLabel.Text = $"{general.SoundVolume}%";
            LocalPreferences.Current.Save();
        };
        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        volumeRow.Children.Add(volume);
        volumeRow.Children.Add(volumeLabel);
        TabBody.Children.Add(Row(Localization.T("settings.volume"), Localization.T("settings.volumeSub"), volumeRow));

        var previewButton = Ui.MiniButton(
            Localization.T(_previewing ? "settings.previewPlaying" : "settings.previewPlay"),
            () => _ = PreviewAsync(), "music");
        previewButton.IsEnabled = !_previewing && SoundService.Current.Enabled;
        TabBody.Children.Add(Row(Localization.T("settings.preview"), Localization.T("settings.previewSub"), previewButton));

        TabBody.Children.Add(ServerSection());
        TabBody.Children.Add(PairingRow());
    }

    private Control ServerSection()
    {
        var section = new StackPanel();
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 16, 0, 8),
        };
        var title = Ui.Text(Localization.T("settings.serverSection"), 14, "BrushText", Avalonia.Media.FontWeight.SemiBold);
        head.Children.Add(title);
        var badge = KindTag(Localization.T("settings.serverBadge"));
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);
        section.Children.Add(head);
        if (_configError.Length > 0) section.Children.Add(ErrorNote(_configError));
        if (_config is null)
        {
            section.Children.Add(Note(Localization.T("common.loading")));
            return section;
        }

        var enabledModels = _models.Where(m => m.Enabled)
            .Select(m => (m.Id, $"{m.Label} · {m.Provider}")).ToList();
        enabledModels.Insert(0, ("", Localization.T("settings.firstConnected")));
        section.Children.Add(SelectRow(Localization.T("settings.defaultModel"), Localization.T("settings.defaultModelSub"),
            enabledModels, _config.DefaultModel, value => PatchAsync(new ConfigPatch { DefaultModel = value })));

        var vision = _models.Where(m => m.Enabled && m.Vision).Select(m => (m.Id, $"{m.Label} · {m.Provider}")).ToList();
        vision.Insert(0, ("", Localization.T("settings.imageFallbackOff")));
        section.Children.Add(SelectRow(Localization.T("settings.imageFallback"), Localization.T("settings.imageFallbackSub"),
            vision, _config.ImageFallbackModel, value => PatchAsync(new ConfigPatch { ImageFallbackModel = value })));

        section.Children.Add(SwitchRow("settings.autoCompact", "settings.autoCompactSub", _config.AutoCompact,
            on => PatchAsync(new ConfigPatch { AutoCompact = on })));
        section.Children.Add(NumberRow(Localization.T("settings.compactThreshold"), Localization.T("settings.compactThresholdSub"),
            SettingsFormat.Percent(_config.CompactThreshold), text => PatchAsync(new ConfigPatch
            {
                CompactThreshold = SettingsFormat.Fraction(text, _config.CompactThreshold),
            }), _config.AutoCompact));
        section.Children.Add(SwitchRow("settings.prune", "settings.pruneSub", _config.PruneToolResults,
            on => PatchAsync(new ConfigPatch { PruneToolResults = on })));
        section.Children.Add(NumberRow(Localization.T("settings.pruneThreshold"), Localization.T("settings.pruneThresholdSub"),
            SettingsFormat.Percent(_config.PruneThreshold), text => PatchAsync(new ConfigPatch
            {
                PruneThreshold = SettingsFormat.Fraction(text, _config.PruneThreshold),
            }), _config.AutoCompact && _config.PruneToolResults));
        section.Children.Add(NumberRow(Localization.T("settings.pruneKeep"), Localization.T("settings.pruneKeepSub"),
            _config.PruneKeepSteps.ToString(), text => PatchAsync(new ConfigPatch
            {
                PruneKeepSteps = int.TryParse(text, out var value) ? Math.Clamp(value, 1, 50) : _config.PruneKeepSteps,
            }), _config.AutoCompact && _config.PruneToolResults));
        section.Children.Add(SwitchRow("settings.discovery", "settings.discoverySub", _config.Discovery,
            on => PatchAsync(new ConfigPatch { Discovery = on })));
        section.Children.Add(TextRow(Localization.T("settings.shell"), Localization.T("settings.shellSub"),
            _config.Shell, text => PatchAsync(new ConfigPatch { Shell = text.Trim() }),
            Localization.T("settings.shellPlaceholder")));

        var browsers = _browsers.Select(b => (b.Id, b.Label)).ToList();
        browsers.Insert(0, ("auto", _browserAuto.Length > 0
            ? $"{Localization.T("settings.browser.auto")} — {_browserAuto}"
            : Localization.T("settings.browser.auto")));
        var browserSub = Localization.T("settings.defaultBrowserSub")
                         + (_browserHeadless ? " " + Localization.T("settings.browserHeadless")
                             : _browsers.Count == 0 ? " " + Localization.T("settings.browserNone") : "");
        section.Children.Add(SelectRow(Localization.T("settings.defaultBrowser"), browserSub, browsers,
            _config.DefaultBrowser, value => PatchAsync(new ConfigPatch { DefaultBrowser = value })));

        section.Children.Add(SwitchRow("settings.keepAwake", "settings.keepAwakeSub", _config.KeepScreenAwake,
            on => PatchAsync(new ConfigPatch { KeepScreenAwake = on })));
        section.Children.Add(SwitchRow("settings.knowledge", "settings.knowledgeSub", _config.Knowledge,
            on => PatchAsync(new ConfigPatch { Knowledge = on })));

        var providers = new List<(string, string)> { ("duckduckgo", "DuckDuckGo"), ("searxng", "SearXNG") };
        section.Children.Add(SelectRow(Localization.T("settings.webSearch"), Localization.T("settings.webSearchSub"),
            providers, _config.SearchProvider, value => PatchAsync(new ConfigPatch { SearchProvider = value })));
        if (_config.SearchProvider == "searxng")
        {
            section.Children.Add(TextRow(Localization.T("settings.searxngUrl"), Localization.T("settings.searxngSub"),
                _config.SearxngUrl, text => PatchAsync(new ConfigPatch { SearxngUrl = text.Trim() }),
                Localization.T("settings.searxngPlaceholder")));
        }

        var sandboxSub = Localization.T("settings.sandboxSub",
            ("backend", _config.SandboxAvailable ? _config.SandboxBackend : Localization.T("settings.sandboxNo")));
        section.Children.Add(SwitchRow(Localization.T("settings.sandbox"), sandboxSub, _config.Sandbox,
            on => PatchAsync(new ConfigPatch { Sandbox = on })));
        section.Children.Add(SwitchRow("settings.sandboxNetwork", "settings.sandboxNetworkSub", _config.SandboxNetwork,
            on => PatchAsync(new ConfigPatch { SandboxNetwork = on })));

        var applyPatch = new List<(string, string)>
        {
            ("auto", Localization.T("settings.tools.applyPatchAuto")),
            ("always", Localization.T("settings.tools.applyPatchAlways")),
            ("never", Localization.T("settings.tools.applyPatchNever")),
        };
        section.Children.Add(SelectRow(Localization.T("settings.tools.applyPatch"), Localization.T("settings.tools.applyPatchSub"),
            applyPatch, _config.ApplyPatch, value => PatchAsync(new ConfigPatch { ApplyPatch = value })));
        section.Children.Add(SwitchRow("settings.tools.readBeforeEdit", Localization.T("settings.tools.readBeforeEditSub"),
            _config.ReadBeforeEdit, on => PatchAsync(new ConfigPatch { ReadBeforeEdit = on })));

        var paths = new StackPanel { Spacing = 6.4, Margin = new Thickness(0, 10, 0, 0) };
        paths.Children.Add(ReadOnlyPath("settings.dataDir", _config.DataDir));
        paths.Children.Add(ReadOnlyPath("settings.configPath", _config.ConfigPath));
        section.Children.Add(paths);
        return section;
    }

    private static Control ReadOnlyPath(string key, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 8 };
        grid.Children.Add(Ui.Text(Localization.T(key), 11.5, "BrushTextFaint"));
        var path = Ui.Text(value.Length > 0 ? value : "—", 11.5, "BrushTextMuted");
        path.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        path.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(path, 1);
        grid.Children.Add(path);
        return grid;
    }

    private Control PairingRow()
    {
        var button = Ui.MiniButton(Localization.T("pair.open"), () => PairingRequested?.Invoke(), "link");
        button.Margin = new Thickness(0, 12, 0, 0);
        return Row(Localization.T("pair.section"), Localization.T("pair.sectionSub"), button);
    }

    private async Task PreviewAsync()
    {
        if (_previewing) return;
        _previewing = true;
        Build();
        try
        {
            await SoundService.Current.PreviewAsync();
        }
        catch (OperationCanceledException)
        {
            // Closed mid-preview.
        }
        finally
        {
            _previewing = false;
            Build();
        }
    }

    // --- appearance tab ------------------------------------------------------------------------------------

    private void BuildAppearance()
    {
        var prefs = LocalPreferences.Current.Data;
        var appearance = prefs.Appearance;
        TabBody.Children.Add(Intro(Localization.T("settings.appearance.intro")));
        TabBody.Children.Add(Section("settings.appearance.accent"));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        foreach (var accent in new[] { "brown", "beige", "slate", "custom" })
        {
            var active = appearance.Accent == accent;
            var color = accent switch
            {
                "beige" => "#E4D3AC",
                "slate" => "#7F8B99",
                "custom" => appearance.CustomColor,
                _ => "#A9744F",
            };
            var name = accent == "custom"
                ? Localization.T("settings.appearance.custom")
                : accent;
            var label = Ui.Text(name, 11.5, active ? "BrushText" : "BrushTextMuted", Avalonia.Media.FontWeight.Medium);
            var swatch = new StackPanel { Spacing = 4.8 };
            swatch.Children.Add(new Border
            {
                Width = 44,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = SolidColorBrush.Parse(color),
                BorderThickness = new Thickness(active ? 2 : 1),
                BorderBrush = Ui.Brush(active ? "BrushPrimary" : "BrushBorderStrong"),
            });
            swatch.Children.Add(label);
            var button = new Button
            {
                Content = swatch,
                Padding = new Thickness(4.8),
                CornerRadius = new CornerRadius(9),
                Background = Ui.Brush(active ? "BrushPrimarySoft" : "BrushTransparent"),
                BorderThickness = new Thickness(0),
            };
            var captured = accent;
            button.Click += (_, _) =>
            {
                appearance.Accent = captured;
                LocalPreferences.Current.Save();
                _vm?.RaiseAppearanceChanged();
                Build();
            };
            swatches.Children.Add(button);
        }
        TabBody.Children.Add(swatches);

        var hexBox = Ui.Field(appearance.CustomColor);
        hexBox.Width = 120;
        hexBox.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            var text = (hexBox.Text ?? "").Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            try
            {
                _ = SolidColorBrush.Parse(text);
            }
            catch
            {
                return;
            }
            appearance.CustomColor = text;
            appearance.Accent = "custom";
            LocalPreferences.Current.Save();
            _vm?.RaiseAppearanceChanged();
            Build();
        };
        var colourRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        colourRow.Children.Add(hexBox);
        colourRow.Children.Add(Ui.Text(Localization.T("settings.appearance.pickColour"), 11.5, "BrushTextFaint"));
        TabBody.Children.Add(Row(Localization.T("settings.appearance.custom"), "", colourRow));

        TabBody.Children.Add(Section("settings.appearance.textSize"));
        TabBody.Children.Add(Ui.Segment(
        [
            ("sm", "SM"), ("md", "MD"), ("lg", "LG"),
        ], appearance.FontSize, size =>
        {
            appearance.FontSize = size;
            LocalPreferences.Current.Save();
            _vm?.RaiseAppearanceChanged();
            Build();
        }));

        TabBody.Children.Add(Row(Localization.T("settings.appearance.compactSidebar"),
            Localization.T("settings.appearance.compactSub"), Ui.Switch(appearance.CompactSidebar, on =>
            {
                appearance.CompactSidebar = on;
                LocalPreferences.Current.Save();
                _vm?.RaiseAppearanceChanged();
            })));
    }
}
