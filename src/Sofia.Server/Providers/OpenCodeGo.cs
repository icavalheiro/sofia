using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>
/// OpenCode Go serves each model on one of three wire protocols. Sofia speaks Chat Completions and Anthropic
/// Messages; models that are only reachable through the Responses or Google APIs are not offered.
/// </summary>
public static class OpenCodeGo
{
    public enum Wire { Chat, Anthropic, Unsupported }

    public static Wire WireFor(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.StartsWith("gpt-") || id.StartsWith("grok-") || id.StartsWith("muse-spark") || id.StartsWith("gemini-")) return Wire.Unsupported;
        if (id.StartsWith("claude-") || id.StartsWith("qwen") || id.StartsWith("minimax-m2") || id == "minimax-m3") return Wire.Anthropic;
        return Wire.Chat;
    }

    /// <summary>The public model list lives next to, not under, the protocol-specific paths.</summary>
    public static string ModelsUrl(string baseUrl) =>
        baseUrl.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl[..^"/openai/v1".Length]}/v1/models" : $"{baseUrl}/models";

    /// <summary>The same provider, pointed at the Anthropic Messages endpoint.</summary>
    public static ProviderConfig ForAnthropic(ProviderConfig provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        Type = "anthropic",
        Kind = provider.Kind,
        BaseUrl = provider.BaseUrl.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{provider.BaseUrl[..^"/openai/v1".Length]}/anthropic"
            : provider.BaseUrl,
        ApiKey = provider.ApiKey,
        ApiKeyEnv = provider.ApiKeyEnv,
        Flavor = provider.Flavor,
    };
}
