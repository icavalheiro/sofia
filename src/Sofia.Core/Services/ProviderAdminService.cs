using Sofia.Contracts.Domain;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;

namespace Sofia.Core.Services;

/// <summary>
/// Provider administration: create/patch/delete with the preset and discovery rules, probing a not-yet-saved
/// endpoint, the GitHub Copilot device flow and manual refresh. The endpoints and the in-process host share it.
/// </summary>
public sealed class ProviderAdminService(SofiaDb db, ProviderService providers)
{
    public object Models() => providers.Models();

    public void SetModelsEnabled(IReadOnlyList<string> ids, bool enabled) => providers.SetModelsEnabled([.. ids], enabled);

    public void SetSingleClient(IReadOnlyList<string> ids, bool enabled) => providers.SetSingleClient([.. ids], enabled);

    public object All() => providers.Providers();

    public static object Presets() => ProviderService.Presets.Select(p => new { p.Id, p.Name, p.Type, p.BaseUrl, p.EnvKey, p.Auth, p.Note }).ToList();

    public async Task<object> CopilotLoginAsync(CancellationToken ct)
    {
        try
        {
            return await providers.Copilot.StartDeviceFlowAsync(ct);
        }
        catch (ProviderException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }

    /// <summary>
    /// Polls the device flow. On success the GitHub token is stored on the <c>github-copilot</c> provider, which is
    /// created (or enabled) on the spot.
    /// </summary>
    public async Task<object> CopilotPollAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode)) throw new ArgumentException("deviceCode is required.");
        DevicePoll poll;
        try
        {
            poll = await providers.Copilot.PollDeviceFlowAsync(deviceCode, ct);
        }
        catch (ProviderException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        if (poll.Status != "success") return new { status = poll.Status, interval = poll.Interval, provider = (ProviderView?)null };

        var preset = ProviderService.Presets.First(p => p.Id == "github-copilot");
        var config = db.Providers.FindById(preset.Id) ?? new ProviderConfig
        {
            Id = preset.Id,
            Name = preset.Name,
            Type = preset.Type,
            Kind = "cloud",
            BaseUrl = preset.BaseUrl,
            ApiKeyEnv = preset.EnvKey,
            PresetId = preset.Id,
            Flavor = preset.Flavor,
            Custom = true,
        };
        config.ApiKey = poll.Token;
        config.Enabled = true;
        db.Providers.Upsert(config);
        await providers.RefreshAsync(discover: false, ct);
        return new { status = "success", interval = (int?)null, provider = providers.Provider(preset.Id) };
    }

    public async Task<object?> CreateAsync(CreateProviderRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.BaseUrl)) throw new ArgumentException("Base URL is required.");
        var preset = ProviderService.Presets.FirstOrDefault(p => p.Id == body.PresetId);
        var type = body.Type is "anthropic" or "openai-responses" ? body.Type : preset?.Type ?? "openai";
        var id = preset is not null && db.Providers.FindById(preset.Id) is null ? preset.Id : Ids.New("prv");
        var config = new ProviderConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(body.Name) ? preset?.Name ?? body.BaseUrl : body.Name.Trim(),
            Type = type,
            Kind = ProviderService.IsLoopback(body.BaseUrl) ? "local" : "cloud",
            BaseUrl = body.BaseUrl.Trim().TrimEnd('/'),
            ApiKey = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey.Trim(),
            Custom = true,
            PresetId = preset?.Id,
            Flavor = preset?.Flavor ?? ProviderService.GuessFlavor(body.BaseUrl, type),
            Models = body.Models?.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList() ?? [],
        };
        db.Providers.Insert(config);
        await providers.RefreshAsync(discover: false, ct);
        return providers.Provider(config.Id);
    }

    public async Task<object?> UpdateAsync(string id, UpdateProviderRequest body, CancellationToken ct)
    {
        var config = db.Providers.FindById(id) ?? throw new NotFoundException("Provider not found.");
        if (body.Enabled is { } enabled) config.Enabled = enabled;
        if (body.ApiKey is { } key) config.ApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (!string.IsNullOrWhiteSpace(body.BaseUrl)) config.BaseUrl = body.BaseUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(body.Name)) config.Name = body.Name.Trim();
        if (body.PreserveThinking is { } preserve) config.PreserveThinking = preserve;
        // An empty icon resets to the preset/runtime default; an absent field leaves the choice untouched.
        if (body.Icon is { } icon) config.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        db.Providers.Update(config);
        await providers.RefreshAsync(discover: false, ct);
        return providers.Provider(id);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var config = db.Providers.FindById(id) ?? throw new NotFoundException("Provider not found.");
        if (config.Discovered)
        {
            // Deleting a discovered runtime would bring it right back: disable it instead.
            config.Enabled = false;
            db.Providers.Update(config);
        }
        else
        {
            db.Providers.Delete(id);
        }
        await providers.RefreshAsync(discover: false, ct);
    }

    /// <summary>
    /// Re-runs discovery and the models.dev refresh. The catalog update runs off the request path: the model list
    /// already has the last known limits.
    /// </summary>
    public async Task<object> RefreshAsync(ModelCatalog catalog, CancellationToken ct)
    {
        _ = catalog.EnsureFreshAsync();
        await providers.RefreshAsync(discover: true, ct);
        return providers.Providers();
    }

    public async Task<object> ProbeAsync(ProbeRequest body, CancellationToken ct)
    {
        try
        {
            var models = await providers.ProbeAsync(body.Type is "anthropic" or "openai-responses" ? body.Type : "openai", body.BaseUrl.Trim(), body.ApiKey, ct);
            return new { models };
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException)
        {
            throw new ArgumentException($"Could not list models: {ex.Message}");
        }
    }
}
