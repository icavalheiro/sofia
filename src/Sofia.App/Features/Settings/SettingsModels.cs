using System.Globalization;
using System.Text.Json;

namespace Sofia.App.Features.Settings;

/// <summary>Tiny readers over the backend's <see cref="JsonElement"/> views (config, models, providers, MCP, LSP).</summary>
internal static class J
{
    public static string Str(JsonElement data, string name, string fallback = "") =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static bool Bool(JsonElement data, string name, bool fallback = false) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    public static double Num(JsonElement data, string name, double fallback = 0) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    public static int Int(JsonElement data, string name, int fallback = 0) => (int)Num(data, name, fallback);

    public static JsonElement Obj(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) ? value : default;

    public static IEnumerable<JsonElement> Items(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    public static List<string> Strings(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")]
            : [];
}

/// <summary>The server config (<c>GET /api/config</c> → <c>ConfigDto</c>), mutable because every row patches one field.</summary>
public sealed class SettingsConfig
{
    public string DefaultModel = "";
    public string ImageFallbackModel = "";
    public string Shell = "";
    public bool AutoCompact;
    public double CompactThreshold = 0.75;
    public bool PruneToolResults = true;
    public double PruneThreshold = 0.6;
    public int PruneKeepSteps = 3;
    public bool Discovery = true;
    public bool KeepScreenAwake;
    public bool Knowledge = true;
    public string SearchProvider = "duckduckgo";
    public string SearxngUrl = "";
    public bool Sandbox;
    public bool SandboxNetwork = true;
    public string SandboxBackend = "";
    public bool SandboxAvailable;
    public string ApplyPatch = "auto";
    public bool ReadBeforeEdit = true;
    public string DefaultBrowser = "auto";
    public bool LspEnabled = true;
    public bool LspAutoDiagnostics = true;
    public Dictionary<string, (bool Enabled, string Command)> LspServers = new(StringComparer.Ordinal);
    public string DataDir = "";
    public string ConfigPath = "";

    public static SettingsConfig From(JsonElement data)
    {
        var config = new SettingsConfig
        {
            DefaultModel = J.Str(data, "defaultModel"),
            ImageFallbackModel = J.Str(data, "imageFallbackModel"),
            Shell = J.Str(data, "shell"),
            AutoCompact = J.Bool(data, "autoCompact"),
            CompactThreshold = J.Num(data, "compactThreshold", 0.75),
            PruneToolResults = J.Bool(data, "pruneToolResults", true),
            PruneThreshold = J.Num(data, "pruneThreshold", 0.6),
            PruneKeepSteps = J.Int(data, "pruneKeepSteps", 3),
            Discovery = J.Bool(data, "discovery", true),
            KeepScreenAwake = J.Bool(data, "keepScreenAwake"),
            Knowledge = J.Bool(data, "knowledge", true),
            SearchProvider = J.Str(data, "searchProvider", "duckduckgo"),
            SearxngUrl = J.Str(data, "searxngUrl"),
            Sandbox = J.Bool(data, "sandbox"),
            SandboxNetwork = J.Bool(data, "sandboxNetwork", true),
            ApplyPatch = J.Str(data, "applyPatch", "auto"),
            ReadBeforeEdit = J.Bool(data, "readBeforeEdit", true),
            DefaultBrowser = J.Str(data, "defaultBrowser", "auto"),
            DataDir = J.Str(data, "dataDir"),
            ConfigPath = J.Str(data, "configPath"),
        };
        var lsp = J.Obj(data, "lsp");
        if (lsp.ValueKind == JsonValueKind.Object)
        {
            config.LspEnabled = J.Bool(lsp, "enabled", true);
            config.LspAutoDiagnostics = J.Bool(lsp, "autoDiagnostics", true);
            var servers = J.Obj(lsp, "servers");
            if (servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    config.LspServers[server.Name] = (
                        J.Bool(server.Value, "enabled", true),
                        J.Str(server.Value, "command"));
                }
            }
        }
        var sandbox = J.Obj(data, "sandboxStatus");
        if (sandbox.ValueKind == JsonValueKind.Object)
        {
            config.SandboxAvailable = J.Bool(sandbox, "available");
            config.SandboxBackend = J.Str(sandbox, "backend");
        }
        return config;
    }
}

/// <summary>One provider (<c>GET /api/providers</c>).</summary>
public sealed record ProviderRow(
    string Id,
    string Name,
    string Kind,
    string Type,
    string BaseUrl,
    string Status,
    string Detail,
    bool Enabled,
    bool Custom,
    bool HasKey,
    int Models,
    bool PreserveThinking,
    string Icon)
{
    public static ProviderRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "name"),
        J.Str(item, "kind"),
        J.Str(item, "type"),
        J.Str(item, "baseUrl"),
        J.Str(item, "status"),
        J.Str(item, "detail"),
        J.Bool(item, "enabled", true),
        J.Bool(item, "custom"),
        J.Bool(item, "hasKey"),
        J.Int(item, "models"),
        J.Bool(item, "preserveThinking"),
        J.Str(item, "icon"));
}

/// <summary>One model (<c>GET /api/models</c>).</summary>
public sealed record ModelRow(
    string Id,
    string ProviderId,
    string Label,
    string Provider,
    bool Local,
    string Context,
    bool Connected,
    bool Vision,
    bool Enabled,
    bool SingleClient)
{
    public static ModelRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "providerId"),
        J.Str(item, "label"),
        J.Str(item, "provider"),
        J.Bool(item, "local"),
        J.Str(item, "context"),
        J.Bool(item, "connected"),
        J.Bool(item, "vision"),
        J.Bool(item, "enabled", true),
        J.Bool(item, "singleClient"));
}

/// <summary>One preset of the add-provider flow (<c>GET /api/providers/presets</c>).</summary>
public sealed record PresetRow(string Id, string Name, string Type, string BaseUrl, string EnvKey, string Auth, string Note)
{
    public static PresetRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "name"),
        J.Str(item, "type", "openai"),
        J.Str(item, "baseUrl"),
        J.Str(item, "envKey"),
        J.Str(item, "auth"),
        J.Str(item, "note"));
}

/// <summary>One global skill (<c>GET /api/skills</c>).</summary>
public sealed record SkillRow(string Id, string Name, string Description, string Content, string Source)
{
    public static SkillRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "name"),
        J.Str(item, "description"),
        J.Str(item, "content"),
        J.Str(item, "source"));
}

/// <summary>One MCP server (<c>GET /api/mcp</c>).</summary>
public sealed record McpRow(
    string Id,
    string Name,
    string Transport,
    string Command,
    string Url,
    string Status,
    int Tools,
    string Error)
{
    public static McpRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "name"),
        J.Str(item, "transport", "command"),
        J.Str(item, "command"),
        J.Str(item, "url"),
        J.Str(item, "status", "stopped"),
        J.Int(item, "tools"),
        J.Str(item, "error"));
}

/// <summary>One language server (<c>GET /api/lsp</c>).</summary>
public sealed record LspRow(
    string Id,
    string Name,
    string Command,
    string Status,
    string InstallCommand,
    List<string> Extensions,
    List<string> RunningIn,
    string Error)
{
    public static LspRow From(JsonElement item) => new(
        J.Str(item, "id"),
        J.Str(item, "name"),
        J.Str(item, "command"),
        J.Str(item, "status", "stopped"),
        J.Str(item, "installCommand"),
        J.Strings(item, "extensions"),
        J.Strings(item, "runningIn"),
        J.Str(item, "error"));
}

/// <summary>One installed browser engine (<c>GET /api/browsers</c>).</summary>
public sealed record BrowserRow(string Id, string Label)
{
    public static BrowserRow From(JsonElement item) => new(J.Str(item, "id"), J.Str(item, "label"));
}

internal static class SettingsFormat
{
    public static string Percent(double value) => Math.Round(value * 100).ToString(CultureInfo.InvariantCulture);

    public static double Fraction(string text, double fallback) =>
        double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number / 100, 0.05, 0.99)
            : fallback;
}
