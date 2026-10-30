namespace Sofia.Server.Providers;

/// <summary>
/// Traits of a model family that no catalog reports: whether it reasons, accepts images, and its
/// per-token prices. Context windows and output limits are **not** here — they come from the model
/// itself, from <see cref="ModelCatalog"/> (models.dev) or from <see cref="FallbackContext"/>.
/// </summary>
public sealed record ModelTraits(bool Reasoning, bool Vision, double InputPrice = 0, double OutputPrice = 0);

/// <summary>
/// Fallback traits for models no catalog knows. Prices are USD per million tokens.
/// Context/output limits are resolved by <see cref="ProviderService"/> in this order: provider-reported
/// metadata, the runtime value remembered in <c>ProviderConfig.KnownContext</c>, the models.dev catalog,
/// and finally <see cref="FallbackContext"/>.
/// </summary>
public static class ModelLimits
{
    /// <summary>Context window used when neither the provider nor the catalog reports one. The only hardcoded limit.</summary>
    public const int FallbackContext = 32_768;

    // Ordered: the first matching prefix wins, so specific names come first.
    private static readonly (string Prefix, ModelTraits Traits)[] Table =
    [
        ("claude-haiku-5", new(true, true)),
        ("claude-opus-4", new(true, true, 15, 75)),
        ("claude-sonnet-4", new(true, true, 3, 15)),
        ("claude-haiku-4", new(true, true, 1, 5)),
        ("claude-3-7-sonnet", new(true, true, 3, 15)),
        ("claude-3-5-sonnet", new(false, true, 3, 15)),
        ("claude-3-5-haiku", new(false, true, 0.8, 4)),
        ("claude", new(false, true, 3, 15)),
        ("gpt-5-mini", new(true, true, 0.25, 2)),
        ("gpt-5-nano", new(true, true, 0.05, 0.4)),
        ("gpt-5", new(true, true, 1.25, 10)),
        ("gpt-4.1-mini", new(false, true, 0.4, 1.6)),
        ("gpt-4.1-nano", new(false, true, 0.1, 0.4)),
        ("gpt-4.1", new(false, true, 2, 8)),
        ("gpt-4o-mini", new(false, true, 0.15, 0.6)),
        ("gpt-4o", new(false, true, 2.5, 10)),
        ("gpt-oss", new(true, false)),
        ("o4-mini", new(true, true, 1.1, 4.4)),
        ("o3-mini", new(true, false, 1.1, 4.4)),
        ("o3", new(true, true, 2, 8)),
        ("o1", new(true, true, 15, 60)),
        ("gemini-2.5-pro", new(true, true, 1.25, 10)),
        ("gemini-2.5-flash-lite", new(true, true, 0.1, 0.4)),
        ("gemini-2.5-flash", new(true, true, 0.3, 2.5)),
        ("gemini-2.0-flash", new(false, true, 0.1, 0.4)),
        ("gemini", new(false, true)),
        ("deepseek-v4-pro", new(true, false, 0.66, 1.98)),
        ("deepseek-v4", new(true, true, 0.15, 0.6)),
        ("deepseek-flash", new(true, true, 0.15, 0.6)),
        ("deepseek-reasoner", new(true, false, 0.55, 2.19)),
        ("deepseek-chat", new(false, false, 0.27, 1.1)),
        ("grok-4", new(true, true, 3, 15)),
        ("grok-3-mini", new(true, false, 0.3, 0.5)),
        ("grok", new(false, false, 3, 15)),
        ("mistral-large", new(false, false, 2, 6)),
        ("codestral", new(false, false, 0.3, 0.9)),
        ("devstral", new(false, false)),
        ("qwen3.8", new(true, true)),
        ("qwen3.7-max", new(true, false)),
        ("qwen3.7", new(true, true)),
        ("qwen3.6", new(true, true)),
        ("kimi-k3", new(true, true)),
        ("kimi-k2.7", new(true, true)),
        ("kimi-k2.6", new(true, true)),
        ("kimi-k2.5", new(true, true)),
        ("glm-5.3", new(true, false)),
        ("glm-5.2", new(true, false)),
        ("glm-5.1", new(true, false)),
        ("glm-5", new(true, false)),
        ("minimax-m3", new(true, false)),
        ("minimax-m2", new(true, false)),
        ("mimo-v2", new(true, false)),
        ("longcat-2", new(true, false)),
        ("hy4", new(true, false)),
        ("hy3", new(true, false)),
        ("step-5", new(true, true)),
        ("kimi-k2", new(false, false)),
        ("llama-4", new(false, true)),
        ("llama-3", new(false, false)),
    ];

    public static ModelTraits? Known(string modelId)
    {
        var name = Normalize(modelId);
        foreach (var (prefix, traits) in Table)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return traits;
        return null;
    }

    /// <summary>Strips routing prefixes such as 'openai/' or 'models/'.</summary>
    public static string Normalize(string modelId)
    {
        var name = modelId.ToLowerInvariant();
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    public static bool LooksReasoning(string modelId)
    {
        var n = modelId.ToLowerInvariant();
        return n.Contains("qwen3") || n.Contains("r1") || n.Contains("think") || n.Contains("nemotron") || n.Contains("gpt-oss")
            || n.Contains("magistral") || n.Contains("reason") || n.Contains("qwq") || n.Contains("glm-4.5") || n.Contains("glm-4.6")
            || n.Contains("deepseek-v3.1") || n.Contains("lfm2");
    }

    public static bool LooksVision(string modelId)
    {
        var n = modelId.ToLowerInvariant();
        return n.Contains("-vl") || n.Contains("vision") || n.Contains("llava") || n.Contains("gemma-3") || n.Contains("gemma3")
            || n.Contains("pixtral") || n.Contains("minicpm-v") || n.Contains("moondream") || n.Contains("qwen2.5-omni") || n.Contains("omni");
    }

    /// <summary>Audio input is only assumed for model families known to take it; runtime metadata adds the rest.</summary>
    public static bool LooksAudio(string modelId)
    {
        var n = modelId.ToLowerInvariant();
        return n.Contains("audio") || n.Contains("omni") || n.Contains("voxtral") || n.Contains("gemma-3n") || n.Contains("gemma3n") || n.Contains("ultravox");
    }

    public static int DefaultMaxOutput(int context) => Math.Clamp(context / 4, 1024, 32_768);
}
