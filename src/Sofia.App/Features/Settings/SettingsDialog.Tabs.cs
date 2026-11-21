using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Settings;

// Models, Skills, MCP, Language servers and Shortcuts tabs. Each builder reads the cached server state loaded by
// SettingsDialog.LoadAsync and turns one action into an ISofiaBackend call plus a rebuild.
public partial class SettingsDialog
{
    // --- models --------------------------------------------------------------------------------------------

    private void BuildModels()
    {
        var connected = _providers.Count(provider => provider.Status == "connected" && provider.Enabled);
        TabBody.Children.Add(Intro(Localization.T("settings.models.intro", ("connected", connected), ("total", _providers.Count))));
        if (_modelsError.Length > 0) TabBody.Children.Add(ErrorNote(_modelsError));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8, Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(Ui.MiniButton(Localization.T("settings.models.addProvider"), () =>
        {
            _addProvider = !_addProvider;
            _presetId = null;
            _probeModels.Clear();
            _probeNote = "";
            _probeError = "";
            Build();
        }, "plus", primary: true));
        actions.Children.Add(Ui.MiniButton(Localization.T("settings.models.refresh"), () => _ = RediscoverAsync(), "refresh"));
        TabBody.Children.Add(actions);

        if (_addProvider) TabBody.Children.Add(AddProviderForm());

        TabBody.Children.Add(Section("settings.models.providers"));
        if (_providers.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("settings.models.empty")));
        }
        else
        {
            foreach (var provider in _providers) TabBody.Children.Add(ProviderCard(provider));
        }

        TabBody.Children.Add(Section("settings.models.available"));
        var models = new WrapPanel();
        foreach (var model in _models.Where(model => model.Enabled))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
            chip.Children.Add(new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Ui.Brush(model.Connected ? "BrushOk" : "BrushTextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            chip.Children.Add(Ui.Icon("cpu", 13.6, "BrushTextMuted"));
            var label = Ui.Truncate(model.Label, 12, "BrushText");
            label.MaxWidth = 180;
            chip.Children.Add(label);
            if (model.Local) chip.Children.Add(KindTag(Localization.T("settings.models.local")));
            chip.Children.Add(Ui.Text(model.Context, 10.5, "BrushTextFaint"));
            var card = Card(chip);
            card.Margin = new Thickness(0, 0, 6.4, 6.4);
            ToolTip.SetTip(card, $"{model.Provider} · {model.Id}");
            models.Children.Add(card);
        }
        TabBody.Children.Add(models);
    }

    private Control AddProviderForm()
    {
        var stack = new StackPanel { Spacing = 8.8 };
        stack.Children.Add(Card(new StackPanel
        {
            Spacing = 8.8,
            Children = { ProviderPicker() },
        }));

        if (_presetId is not null)
        {
            var preset = _presets.FirstOrDefault(item => item.Id == _presetId);
            if (preset is not null && preset.Note.Length > 0) stack.Children.Add(Note(preset.Note));
            if (preset is { Auth: "github-device" })
            {
                var login = new StackPanel { Spacing = 6.4 };
                login.Children.Add(Ui.Text(Localization.T("settings.models.github"), 12.5, "BrushText", Avalonia.Media.FontWeight.SemiBold));
                if (_copilotCode is not null)
                {
                    login.Children.Add(Ui.Text(Localization.T("settings.models.enterCode", ("uri", _copilotUri)), 12, "BrushTextMuted"));
                    var code = Ui.Text(_copilotCode, 14, "BrushPrimary", Avalonia.Media.FontWeight.SemiBold);
                    code.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
                    login.Children.Add(code);
                    if (_copilotNote.Length > 0) login.Children.Add(Note(_copilotNote));
                }
                login.Children.Add(Ui.MiniButton(
                    Localization.T(_copilotCode is null ? "settings.models.signIn" : "settings.models.restartSignIn"),
                    () => _ = CopilotLoginAsync(), "link"));
                stack.Children.Add(Card(login));
            }

            stack.Children.Add(Field("settings.models.name", _newName, value => _newName = value,
                Localization.T("settings.models.namePlaceholder")));
            stack.Children.Add(SegmentField("settings.models.protocol",
                [("openai", Localization.T("settings.models.openai")), ("openai-responses", Localization.T("settings.models.openaiResponses")), ("anthropic", Localization.T("settings.models.anthropic"))],
                _newType, value => _newType = value));
            stack.Children.Add(Field("settings.models.baseUrl", _newBaseUrl, value => _newBaseUrl = value, "https://api.example.com/v1"));
            stack.Children.Add(Field("settings.models.token", _newKey, value => _newKey = value, "sk-…", password: true));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Ui.MiniButton(
                Localization.T(_probing ? "settings.models.loadingList" : "settings.models.loadModels"),
                () => _ = ProbeAsync(), "refresh"));
            buttons.Children.Add(Ui.MiniButton(Localization.T("settings.models.addProvider"), () => _ = AddProviderAsync(),
                "plus", primary: true));
            buttons.Children.Add(Ui.MiniButton(Localization.T("settings.models.cancel"), () =>
            {
                _addProvider = false;
                Build();
            }));
            stack.Children.Add(buttons);

            if (_probeNote.Length > 0) stack.Children.Add(Note(_probeNote));
            if (_probeError.Length > 0) stack.Children.Add(ErrorNote(_probeError));
            if (_probeModels.Count > 0)
            {
                var found = new StackPanel { Spacing = 4.8 };
                found.Children.Add(Ui.Text(Localization.T("settings.models.found", ("count", _probeModels.Count)), 11.5,
                    "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold));
                var wrap = new WrapPanel();
                foreach (var model in _probeModels)
                {
                    var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
                    chip.Children.Add(Ui.Icon("cpu", 13.6, "BrushOk"));
                    var label = Ui.Truncate(model.Label, 12, "BrushText");
                    label.MaxWidth = 200;
                    chip.Children.Add(label);
                    chip.Children.Add(Ui.Text(model.Context, 10.5, "BrushTextFaint"));
                    var card = Card(chip);
                    card.Margin = new Thickness(0, 0, 6.4, 6.4);
                    wrap.Children.Add(card);
                }
                found.Children.Add(wrap);
                stack.Children.Add(found);
            }
        }
        else
        {
            var list = new StackPanel { Spacing = 4.8 };
            foreach (var preset in _presets)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7.2 };
                content.Children.Add(Ui.Icon("plug", 13.6, "BrushTextMuted"));
                content.Children.Add(Ui.Truncate(preset.Name, 12.5, "BrushText"));
                var row = Ui.MenuRow(content, () =>
                {
                    _presetId = preset.Id;
                    _newName = preset.Name;
                    _newType = preset.Type;
                    _newBaseUrl = preset.BaseUrl;
                    _newKey = "";
                    Build();
                });
                list.Children.Add(row);
            }
            var custom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7.2 };
            custom.Children.Add(Ui.Icon("plus", 13.6, "BrushTextMuted"));
            custom.Children.Add(Ui.Truncate(Localization.T("settings.models.custom"), 12.5, "BrushText"));
            list.Children.Add(Ui.MenuRow(custom, () =>
            {
                _presetId = "";
                _newName = "";
                _newType = "openai";
                _newBaseUrl = "";
                _newKey = "";
                Build();
            }));
            stack.Children.Add(list);
        }
        return stack;
    }

    private Control ProviderPicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7.2 };
        row.Children.Add(Ui.Icon("plug", 13.6, "BrushTextMuted"));
        var preset = _presets.FirstOrDefault(item => item.Id == _presetId);
        var label = preset?.Name ?? (_presetId is null ? Localization.T("settings.models.provider")
            : Localization.T("settings.models.custom"));
        row.Children.Add(Ui.Text(label, 12.5, "BrushText", Avalonia.Media.FontWeight.Medium));
        if (_presetId is not null)
        {
            row.Children.Add(Ui.IconButton("x", 11, Localization.T("settings.models.pickAnother"), () =>
            {
                _presetId = null;
                Build();
            }, 20));
        }
        return row;
    }

    private Control Field(string titleKey, string value, Action<string> changed, string placeholder = "", bool password = false)
    {
        var box = Ui.Field(value, placeholder, password);
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        var stack = new StackPanel { Spacing = 4.8 };
        var label = Ui.Text(Localization.T(titleKey), 11.5, "BrushTextMuted");
        stack.Children.Add(label);
        stack.Children.Add(box);
        return stack;
    }

    private Control SegmentField(string titleKey, IReadOnlyList<(string, string)> options, string selected, Action<string> pick)
    {
        var stack = new StackPanel { Spacing = 4.8 };
        stack.Children.Add(Ui.Text(Localization.T(titleKey), 11.5, "BrushTextMuted"));
        stack.Children.Add(Ui.Segment(options, selected, value =>
        {
            pick(value);
            Build();
        }));
        return stack;
    }

    private Control ProviderCard(ProviderRow provider)
    {
        var card = new StackPanel { Spacing = 0 };
        var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        side.Children.Add(Ui.StatusChip(provider.Enabled ? provider.Status : "stopped",
            provider.Enabled ? Ui.StatusLabel(provider.Status) : Localization.T("settings.models.disabled")));
        // Icon override (ProviderIconPicker.vue): cycles the provider's Lucide icon; the empty string resets it to auto.
        var iconButton = new Button
        {
            Content = Ui.Icon(provider.Icon.Length > 0 ? provider.Icon : "server", 14.4, "BrushTextMuted"),
            Padding = new Thickness(4.8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = Ui.Brush("BrushSurface3"),
        };
        ToolTip.SetTip(iconButton, Localization.T("settings.models.changeIcon"));
        iconButton.Click += (_, _) => _ = CycleProviderIconAsync(provider);
        side.Children.Add(iconButton);
        side.Children.Add(Ui.MiniButton(Localization.T("settings.models.manage"), () =>
        {
            _managingProvider = _managingProvider == provider.Id ? null : provider.Id;
            _modelFilter = "";
            Build();
        }, "cpu"));
        side.Children.Add(Ui.MiniButton(
            Localization.T(provider.Enabled ? "settings.models.disable" : "settings.models.enable"),
            () => _ = ToggleProviderAsync(provider), primary: false));
        if (provider.Type == "openai")
        {
            side.Children.Add(Ui.MiniButton(
                Localization.T("settings.models.preserveThinking",
                    ("state", Localization.T(provider.PreserveThinking ? "settings.models.on" : "settings.models.off"))),
                () => _ = PreserveThinkingAsync(provider)));
        }
        if (provider.Custom)
        {
            side.Children.Add(Ui.IconButton("trash", 12.8, Localization.T("settings.models.remove"),
                () => _ = RemoveProviderAsync(provider), 24));
        }
        card.Children.Add(Card(CardHead(provider.Icon.Length > 0 ? provider.Icon : "server", provider.Name,
            KindTag(provider.Kind), provider.Detail.Length > 0 ? provider.Detail : provider.BaseUrl, side)));

        if (_managingProvider == provider.Id) card.Children.Add(ModelManager(provider));
        return card;
    }

    private Control ModelManager(ProviderRow provider)
    {
        var stack = new StackPanel { Spacing = 6.4, Margin = new Thickness(0, 0, 0, 10) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
        var filter = Ui.Field(_modelFilter, Localization.T("settings.models.filter"));
        filter.Width = 180;
        filter.TextChanged += (_, _) =>
        {
            _modelFilter = filter.Text ?? "";
            Build();
        };
        bar.Children.Add(filter);
        var providerModels = ProviderModels(provider);
        var visible = VisibleProviderModels(provider);
        bar.Children.Add(Ui.MiniButton(Localization.T("settings.models.selectAll"),
            () => _ = SetModelsEnabledAsync(visible, true)));
        bar.Children.Add(Ui.MiniButton(Localization.T("settings.models.deselectAll"),
            () => _ = SetModelsEnabledAsync(visible, false)));
        stack.Children.Add(bar);

        if (providerModels.Count == 0)
        {
            stack.Children.Add(Note(Localization.T("settings.models.noModels")));
            return Card(stack);
        }
        stack.Children.Add(Note($"{providerModels.Count(model => model.Enabled)} / {providerModels.Count} " +
                                 Localization.T("settings.models.enabledCount")));

        var list = new StackPanel { Spacing = 2 };
        foreach (var model in visible)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8.8 };
            var check = new CheckBox { IsChecked = model.Enabled, VerticalAlignment = VerticalAlignment.Center };
            var captured = model;
            check.IsCheckedChanged += (_, _) => _ = SetModelEnabledAsync(captured, check.IsChecked == true);
            row.Children.Add(check);
            var label = Ui.Truncate(model.Label, 12, "BrushText");
            ToolTip.SetTip(label, model.Id);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var single = Ui.Text(
                Localization.T(model.SingleClient ? "settings.models.singleClientOn" : "settings.models.singleClient"),
                9.6, model.SingleClient ? "BrushPrimary" : "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold);
            var singleButton = new Button
            {
                Content = single,
                Padding = new Thickness(6.4, 1.6),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                Background = Ui.Brush(model.SingleClient ? "BrushPrimarySoft" : "BrushSurface3"),
            };
            singleButton.Click += (_, _) => _ = SetSingleClientAsync(captured, !captured.SingleClient);
            Grid.SetColumn(singleButton, 2);
            row.Children.Add(singleButton);

            var context = Ui.Text(model.Context, 10.5, "BrushTextFaint");
            Grid.SetColumn(context, 3);
            row.Children.Add(context);
            list.Children.Add(row);
        }
        stack.Children.Add(list);
        return Card(stack);
    }

    private List<ModelRow> ProviderModels(ProviderRow provider) =>
        [.. _models.Where(model => model.ProviderId == provider.Id)];

    private List<ModelRow> VisibleProviderModels(ProviderRow provider)
    {
        var visible = ProviderModels(provider);
        if (_modelFilter.Length == 0) return visible;
        return [.. visible.Where(model => model.Label.Contains(_modelFilter, StringComparison.OrdinalIgnoreCase)
                                          || model.Id.Contains(_modelFilter, StringComparison.OrdinalIgnoreCase))];
    }

    private async Task RediscoverAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _modelsError = "";
        try
        {
            _providers = [.. J.Items(await backend.RefreshProvidersAsync()).Select(ProviderRow.From)];
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task ProbeAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _probing = true;
        _probeError = "";
        _probeNote = "";
        Build();
        try
        {
            var result = await backend.ProbeProviderAsync(new ProbeRequest(_newType, _newBaseUrl.Trim(), Empty(_newKey)));
            _probeModels.Clear();
            _probeModels.AddRange(J.Items(J.Obj(result, "models")).Select(ModelRow.From));
            if (_probeModels.Count == 0) _probeNote = Localization.T("settings.models.noModels");
        }
        catch (Exception ex)
        {
            _probeError = ex.Message;
        }
        finally
        {
            _probing = false;
            Build();
        }
    }

    private async Task AddProviderAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.CreateProviderAsync(new CreateProviderRequest(
                _newName.Trim(),
                _newType,
                _newBaseUrl.Trim(),
                Empty(_newKey),
                _probeModels.Count > 0 ? [.. _probeModels.Select(model => model.Id)] : null,
                _presetId is { Length: > 0 } ? _presetId : null));
            _addProvider = false;
            _probeNote = Localization.T("settings.models.saved");
            await ReloadProvidersAsync();
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _probeError = ex.Message;
        }
        Build();
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The provider icon choices the picker offers (the keys <c>PROVIDER_ICON_CHOICES</c> maps to).</summary>
    private static readonly string[] ProviderIcons = ["", "cloud", "flask", "box", "server", "globe", "sparkles", "cpu", "bot", "zap"];

    /// <summary>Cycles the provider's icon override and PATCHes it (empty = the automatic icon).</summary>
    private async Task CycleProviderIconAsync(ProviderRow provider)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        var index = Array.IndexOf(ProviderIcons, provider.Icon);
        var next = ProviderIcons[(index + 1) % ProviderIcons.Length];
        try
        {
            await backend.UpdateProviderAsync(provider.Id,
                new UpdateProviderRequest(null, null, null, null, null, next));
            await ReloadProvidersAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task ReloadProvidersAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            _providers = [.. J.Items(await backend.ProvidersAsync()).Select(ProviderRow.From)];
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
    }

    private async Task ToggleProviderAsync(ProviderRow provider)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.UpdateProviderAsync(provider.Id, new UpdateProviderRequest(!provider.Enabled, null, null, null, null, null));
            await ReloadProvidersAsync();
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task PreserveThinkingAsync(ProviderRow provider)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.UpdateProviderAsync(provider.Id, new UpdateProviderRequest(null, null, null, null, !provider.PreserveThinking, null));
            await ReloadProvidersAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task RemoveProviderAsync(ProviderRow provider)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.DeleteProviderAsync(provider.Id);
            await ReloadProvidersAsync();
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task SetModelsEnabledAsync(IEnumerable<ModelRow> models, bool enabled)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        var ids = models.Select(model => model.Id).ToList();
        if (ids.Count == 0) return;
        try
        {
            await backend.SetModelEnabledAsync(ids, enabled);
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task SetModelEnabledAsync(ModelRow model, bool enabled)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.SetModelEnabledAsync([model.Id], enabled);
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
    }

    private async Task SetSingleClientAsync(ModelRow model, bool enabled)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.SetModelSingleClientAsync([model.Id], enabled);
            await ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            _modelsError = ex.Message;
        }
        Build();
    }

    private async Task CopilotLoginAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _copilotNote = Localization.T("settings.models.waiting");
        try
        {
            var flow = await backend.CopilotLoginAsync();
            _copilotCode = J.Str(flow, "deviceCode");
            _copilotUri = J.Str(flow, "verificationUri");
            var deviceCode = _copilotCode;
            var interval = Math.Max(2, J.Int(flow, "interval", 5));
            Build();
            while (deviceCode is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval));
                var poll = await backend.CopilotPollAsync(deviceCode);
                var status = J.Str(poll, "status", "pending");
                if (status is "pending")
                {
                    if (J.Int(poll, "interval", 0) > 0) interval = J.Int(poll, "interval");
                    continue;
                }
                _copilotNote = status == "success"
                    ? Localization.T("settings.models.saved")
                    : Localization.T("settings.models.waiting");
                if (status == "success")
                {
                    _copilotCode = null;
                    await ReloadProvidersAsync();
                    await ReloadModelsAsync();
                }
                break;
            }
        }
        catch (Exception ex)
        {
            _copilotNote = ex.Message;
        }
        Build();
    }

    // --- skills --------------------------------------------------------------------------------------------

    private void BuildSkills()
    {
        TabBody.Children.Add(Intro(Localization.T("skills.intro")));
        if (_skillsError.Length > 0) TabBody.Children.Add(ErrorNote(_skillsError));
        if (_loading && _skills.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("skills.loading")));
            return;
        }
        if (_skills.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("skills.empty")));
            return;
        }

        var session = ActiveSession();
        var active = session?.ActiveSkills ?? [];
        foreach (var skill in _skills)
        {
            var isActive = active.Contains(skill.Id);
            var stack = new StackPanel { Spacing = 6.4 };
            var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
            if (isActive) side.Children.Add(KindTag(Localization.T("skills.active")));
            side.Children.Add(Ui.MiniButton(Localization.T(_skillOpen == skill.Id ? "skills.hide" : "skills.view"), () =>
            {
                _skillOpen = _skillOpen == skill.Id ? null : skill.Id;
                Build();
            }));
            side.Children.Add(Ui.MiniButton(
                Localization.T(isActive ? "skills.deactivate" : "skills.activate"),
                () => _ = ToggleSkillAsync(skill, isActive), primary: !isActive));
            stack.Children.Add(CardHead("sparkles", skill.Name,
                skill.Source == "workspace" ? KindTag(skill.Source) : null, skill.Description, side));
            if (_skillOpen == skill.Id)
            {
                var content = Ui.Text(skill.Content, 11.5, "BrushTextMuted");
                content.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
                content.TextWrapping = TextWrapping.Wrap;
                content.Margin = new Thickness(0, 6.4, 0, 0);
                stack.Children.Add(content);
            }
            var card = Card(stack);
            if (isActive)
            {
                card.BorderBrush = Ui.Brush("BrushPrimary");
                card.Background = Ui.Brush("BrushPrimarySoft");
            }
            TabBody.Children.Add(card);
        }
        TabBody.Children.Add(Note(Localization.T("skills.note")));
    }

    private Contracts.Domain.Session? ActiveSession() =>
        _vm is null ? null : _vm.FindSession(_vm.ActiveSessionId);

    private async Task ToggleSkillAsync(SkillRow skill, bool active)
    {
        if (_vm?.Connection.Backend is not { } backend || _vm.ActiveSessionId.Length == 0) return;
        _skillsError = "";
        try
        {
            if (active) await backend.RemoveSkillAsync(_vm.ActiveSessionId, skill.Id);
            else await backend.AddSkillAsync(_vm.ActiveSessionId, skill.Id);
        }
        catch (Exception ex)
        {
            _skillsError = ex.Message;
        }
        Build();
    }

    // --- MCP -----------------------------------------------------------------------------------------------

    private void BuildMcp()
    {
        var connected = _mcp.Count(server => server.Status == "connected");
        TabBody.Children.Add(Intro(Localization.T("settings.mcp.intro", ("connected", connected), ("total", _mcp.Count))));
        if (_mcpError.Length > 0) TabBody.Children.Add(ErrorNote(_mcpError));

        var add = Ui.MiniButton(Localization.T("settings.mcp.addServer"), () =>
        {
            _addMcp = !_addMcp;
            Build();
        }, "plus", primary: true);
        add.Margin = new Thickness(0, 0, 0, 12);
        TabBody.Children.Add(add);

        if (_addMcp) TabBody.Children.Add(AddMcpForm());

        TabBody.Children.Add(Section("settings.mcp.servers"));
        if (_loading && _mcp.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("settings.mcp.loading")));
            return;
        }
        if (_mcp.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("settings.mcp.empty")));
            return;
        }
        foreach (var server in _mcp) TabBody.Children.Add(McpCard(server));
    }

    private Control AddMcpForm()
    {
        var stack = new StackPanel { Spacing = 8.8, Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(Field("settings.mcp.name", _mcpName, value => _mcpName = value, "brave-search"));
        stack.Children.Add(SegmentField("settings.mcp.command",
        [
            ("command", Localization.T("settings.mcp.command")),
            ("web", Localization.T("settings.mcp.web")),
        ], _mcpTransport, value => _mcpTransport = value));
        if (_mcpTransport == "web")
        {
            stack.Children.Add(Field("settings.mcp.url", _mcpUrl, value => _mcpUrl = value, "https://mcp.example.com/mcp"));
            stack.Children.Add(Field("settings.mcp.token", _mcpToken, value => _mcpToken = value, "sk-…", password: true));
        }
        else
        {
            stack.Children.Add(Field("settings.mcp.command", _mcpCommand, value => _mcpCommand = value,
                "npx @modelcontextprotocol/server-brave-search"));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        buttons.Children.Add(Ui.MiniButton(Localization.T("settings.mcp.cancel"), () =>
        {
            _addMcp = false;
            Build();
        }));
        buttons.Children.Add(Ui.MiniButton(
            Localization.T(_addingMcp ? "settings.mcp.adding" : "settings.mcp.addServer"),
            () => _ = AddMcpAsync(), "plus", primary: true));
        stack.Children.Add(buttons);
        if (_mcpError.Length > 0) stack.Children.Add(ErrorNote(_mcpError));
        return Card(stack);
    }

    private Control McpCard(McpRow server)
    {
        var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        side.Children.Add(Ui.StatusChip(server.Status, Ui.StatusLabel(server.Status)));
        var connected = server.Status is "connected" or "pending";
        side.Children.Add(Ui.MiniButton(
            Localization.T(server.Status == "error" ? "settings.mcp.restart" : connected ? "settings.mcp.stop" : "settings.mcp.start"),
            () => _ = ToggleMcpAsync(server, connected), primary: false));
        side.Children.Add(Ui.IconButton("trash", 12.8, Localization.T("settings.mcp.remove"),
            () => _ = RemoveMcpAsync(server), 24));

        var detail = server.Transport == "web" ? server.Url : server.Command;
        if (server.Error.Length > 0) detail = server.Error;
        else if (server.Tools > 0) detail += " · " + Localization.T("settings.mcp.tools", ("count", server.Tools));
        var tag = KindTag(server.Transport == "web" ? "web" : "cmd");
        return Card(CardHead("plug", server.Name, tag, detail, side));
    }

    private async Task AddMcpAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _addingMcp = true;
        _mcpError = "";
        Build();
        try
        {
            await backend.AddMcpAsync(new CreateMcpRequest(
                _mcpName.Trim(),
                _mcpTransport,
                _mcpTransport == "command" ? Empty(_mcpCommand) : null,
                _mcpTransport == "web" ? Empty(_mcpUrl) : null,
                _mcpTransport == "web" ? Empty(_mcpToken) : null,
                null));
            _addMcp = false;
            _mcpName = "";
            _mcpCommand = "";
            _mcpUrl = "";
            _mcpToken = "";
            await ReloadMcpAsync();
        }
        catch (Exception ex)
        {
            _mcpError = ex.Message;
        }
        finally
        {
            _addingMcp = false;
            Build();
        }
    }

    private async Task ReloadMcpAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            _mcp = [.. J.Items(await backend.McpServersAsync()).Select(McpRow.From)];
        }
        catch (Exception ex)
        {
            _mcpError = ex.Message;
        }
    }

    private async Task ToggleMcpAsync(McpRow server, bool connected)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _mcpError = "";
        try
        {
            if (connected) await backend.DisconnectMcpAsync(server.Id);
            else await backend.ConnectMcpAsync(server.Id);
            await ReloadMcpAsync();
        }
        catch (Exception ex)
        {
            _mcpError = ex.Message;
        }
        Build();
    }

    private async Task RemoveMcpAsync(McpRow server)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.DeleteMcpAsync(server.Id);
            await ReloadMcpAsync();
        }
        catch (Exception ex)
        {
            _mcpError = ex.Message;
        }
        Build();
    }

    // --- language servers ----------------------------------------------------------------------------------

    private void BuildLsp()
    {
        TabBody.Children.Add(Intro(Localization.T("settings.lsp.intro")));
        if (_lspError.Length > 0) TabBody.Children.Add(ErrorNote(_lspError));
        if (_config is not null)
        {
            TabBody.Children.Add(SwitchRow("settings.lsp.master", "settings.lsp.masterSub", _config.LspEnabled,
                on => PatchLspAsync(new LspConfigPatch(on, null, null))));
            TabBody.Children.Add(SwitchRow("settings.lsp.auto", "settings.lsp.autoSub", _config.LspAutoDiagnostics,
                on => PatchLspAsync(new LspConfigPatch(null, on, null))));
        }
        TabBody.Children.Add(Section("settings.lsp.servers"));
        if (_loading && _lsp.Count == 0)
        {
            TabBody.Children.Add(Note(Localization.T("settings.lsp.loading")));
            return;
        }
        foreach (var server in _lsp) TabBody.Children.Add(LspCard(server));
    }

    private Control LspCard(LspRow server)
    {
        var stack = new StackPanel { Spacing = 0 };
        var setting = _config is not null && _config.LspServers.TryGetValue(server.Id, out var entry)
            ? entry
            : (Enabled: server.Status != "disabled", Command: "");
        var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        side.Children.Add(Ui.StatusChip(server.Status, Ui.StatusLabel(server.Status)));
        side.Children.Add(Ui.Switch(setting.Enabled, on => _ = ToggleLspServerAsync(server, on)));
        stack.Children.Add(Card(CardHead("code", server.Name, KindTag(server.Id), server.Command, side)));

        var detail = string.Join(' ', server.Extensions.Select(extension => "." + extension));
        if (server.RunningIn.Count > 0)
            detail += " · " + Localization.T("settings.lsp.runningIn", ("count", server.RunningIn.Count));
        if (server.Error.Length > 0) detail = server.Error;
        var foot = new StackPanel { Spacing = 6.4, Margin = new Thickness(0, 0, 0, 10) };
        foot.Children.Add(Ui.Text(detail, 11.5, server.Error.Length > 0 ? "BrushErr" : "BrushTextFaint"));

        var custom = Ui.Field(_lspCommands.TryGetValue(server.Id, out var draft) ? draft : "",
            Localization.T("settings.lsp.customCommand", ("command", server.Command)));
        custom.TextChanged += (_, _) => _lspCommands[server.Id] = custom.Text ?? "";
        foot.Children.Add(custom);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
        if (server.Status == "missing")
        {
            buttons.Children.Add(Ui.MiniButton(
                Localization.T(_lspBusy == server.Id ? "settings.lsp.installing" : "settings.lsp.install"),
                () => ConfirmInstall(server), primary: true));
        }
        buttons.Children.Add(Ui.MiniButton(
            Localization.T(_lspBusy == server.Id ? "settings.lsp.working" : "settings.lsp.restart"),
            () => _ = RestartLspAsync(server)));
        buttons.Children.Add(Ui.MiniButton(Localization.T("settings.lsp.apply"), () => _ = SaveLspCommandAsync(server)));
        foot.Children.Add(buttons);
        if (_installConfirm == server.Id)
        {
            var confirm = new StackPanel { Spacing = 6.4, Margin = new Thickness(0, 6.4, 0, 0) };
            confirm.Children.Add(Ui.Text(Localization.T("settings.lsp.installTitle", ("name", server.Name)), 12.5,
                "BrushText", Avalonia.Media.FontWeight.SemiBold));
            var body = Ui.Text(Localization.T("settings.lsp.installBody", ("command", server.InstallCommand)), 11.5,
                "BrushTextMuted");
            body.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
            body.TextWrapping = TextWrapping.Wrap;
            confirm.Children.Add(body);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
            actions.Children.Add(Ui.MiniButton(Localization.T("common.cancel"), () =>
            {
                _installConfirm = null;
                Build();
            }));
            actions.Children.Add(Ui.MiniButton(Localization.T("settings.lsp.confirmInstall"), () => _ = InstallLspAsync(server),
                primary: true));
            confirm.Children.Add(actions);
            foot.Children.Add(Card(confirm));
        }
        stack.Children.Add(foot);
        return stack;
    }

    private string? _installConfirm;

    private void ConfirmInstall(LspRow server)
    {
        _installConfirm = server.Id;
        Build();
    }

    private async Task PatchLspAsync(LspConfigPatch patch)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            _lspError = "";
            _config = SettingsConfig.From(await backend.PatchConfigAsync(new ConfigPatch { Lsp = patch }));
            _lsp = [.. J.Items(await backend.LspServersAsync()).Select(LspRow.From)];
        }
        catch (Exception ex)
        {
            _lspError = ex.Message;
        }
        Build();
    }

    private async Task ToggleLspServerAsync(LspRow server, bool enabled)
    {
        await PatchLspAsync(new LspConfigPatch(null, null, new Dictionary<string, LspServerSettingPatch>
        {
            [server.Id] = new(enabled, null),
        }));
    }

    private async Task SaveLspCommandAsync(LspRow server)
    {
        var command = _lspCommands.TryGetValue(server.Id, out var draft) ? draft.Trim() : "";
        await PatchLspAsync(new LspConfigPatch(null, null, new Dictionary<string, LspServerSettingPatch>
        {
            [server.Id] = new(null, command.Length > 0 ? command : ""),
        }));
    }

    private async Task RestartLspAsync(LspRow server)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _lspBusy = server.Id;
        _lspError = "";
        Build();
        try
        {
            await backend.RestartLspAsync(server.Id);
            _lsp = [.. J.Items(await backend.LspServersAsync()).Select(LspRow.From)];
        }
        catch (Exception ex)
        {
            _lspError = ex.Message;
        }
        finally
        {
            _lspBusy = null;
            Build();
        }
    }

    private async Task InstallLspAsync(LspRow server)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        _installConfirm = null;
        _lspBusy = server.Id;
        _lspError = "";
        Build();
        try
        {
            await backend.InstallLspAsync(server.Id);
            _lsp = [.. J.Items(await backend.LspServersAsync()).Select(LspRow.From)];
        }
        catch (Exception ex)
        {
            _lspError = ex.Message;
        }
        finally
        {
            _lspBusy = null;
            Build();
        }
    }

    // --- shortcuts -----------------------------------------------------------------------------------------

    private void BuildShortcuts()
    {
        TabBody.Children.Add(Intro(Localization.T("shortcuts.intro")));
        var service = ShortcutService.Current;
        foreach (var group in ShortcutService.Groups)
        {
            TabBody.Children.Add(Section($"shortcuts.group.{group}"));
            foreach (var binding in service.Bindings.Where(binding => binding.Group == group))
            {
                var combo = service.ComboOf(binding.Id);
                var active = _recordingId == binding.Id;
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
                    ColumnSpacing = 9.6,
                    Margin = new Thickness(6.4, 7.2),
                    Background = Ui.Brush(active ? "BrushSurface2" : "BrushTransparent"),
                };
                row.Children.Add(Ui.Icon(binding.Icon, 15.2, "BrushTextMuted"));
                var label = Ui.Text(Localization.T($"shortcut.{binding.Id}"), 14, "BrushText");
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3.2 };
                if (active)
                {
                    keys.Children.Add(Ui.Text(Localization.T("shortcuts.press"), 11.5, "BrushPrimary"));
                }
                else if (combo.Length == 0)
                {
                    keys.Children.Add(Ui.Text(Localization.T("shortcuts.unbound"), 11.5, "BrushTextFaint"));
                }
                else
                {
                    foreach (var key in ShortcutService.ComboKeys(combo))
                    {
                        var cap = Ui.Text(key, 10.5, "BrushTextMuted", Avalonia.Media.FontWeight.SemiBold);
                        cap.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
                        keys.Children.Add(new Border
                        {
                            Child = cap,
                            Padding = new Thickness(5.6, 1.6),
                            CornerRadius = new CornerRadius(5),
                            BorderThickness = new Thickness(1),
                            BorderBrush = Ui.Brush("BrushBorderStrong"),
                            Background = Ui.Brush("BrushSurface2"),
                        });
                    }
                }
                var keysButton = new Button
                {
                    Content = keys,
                    Padding = new Thickness(8, 3.2),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Ui.Brush("BrushBorderStrong"),
                    Background = Ui.Brush("BrushSurface"),
                };
                ToolTip.SetTip(keysButton, Localization.T("shortcuts.edit"));
                var id = binding.Id;
                keysButton.Click += (_, _) =>
                {
                    if (_recordingId == id) StopRecording();
                    else StartRecording(id);
                    Build();
                };
                Grid.SetColumn(keysButton, 2);
                row.Children.Add(keysButton);

                if (combo.Length > 0 && ShortcutService.IsBrowserReserved(combo))
                {
                    var warn = Ui.Icon("alert", 13.6, "BrushWarn");
                    ToolTip.SetTip(warn, Localization.T("shortcuts.reserved"));
                    Grid.SetColumn(warn, 3);
                    row.Children.Add(warn);
                }

                var reset = Ui.IconButton("undo", 13.6, Localization.T("shortcuts.reset"), () =>
                {
                    service.ResetBinding(id);
                    Build();
                }, 24);
                reset.IsEnabled = service.IsCustomized(id);
                Grid.SetColumn(reset, 4);
                row.Children.Add(reset);
                TabBody.Children.Add(row);

                if (active && _recordError.Length > 0)
                {
                    var error = ErrorNote(_recordError);
                    error.Margin = new Thickness(6.4, 0, 0, 4.8);
                    TabBody.Children.Add(error);
                }
            }
        }

        var resetAll = Ui.MiniButton(Localization.T("shortcuts.resetAll"), () =>
        {
            service.ResetAllBindings();
            Build();
        });
        resetAll.Margin = new Thickness(0, 12, 0, 0);
        TabBody.Children.Add(resetAll);
    }
}
