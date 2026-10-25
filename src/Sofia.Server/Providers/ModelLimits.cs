namespace Sofia.Server.Providers;

public sealed record ModelTraits(int Context, int MaxOutput, bool Reasoning, bool Vision, double InputPrice = 0, double OutputPrice = 0);

/// <summary>
/// Fallback knowledge for models whose endpoint does not report limits. Prices are
/// USD per million tokens. Runtime metadata always wins over this table.
/// </summary>
public static class ModelLimits
{
    public const int FallbackContext = 32_768;

    // Ordered: the first matching prefix wins, so specific names come first.
    private static readonly (string Prefix, ModelTraits Traits)[] Table =
    [
        ("claude-opus-4", new(200_000, 32_000, true, true, 15, 75)),
        ("claude-sonnet-4", new(200_000, 64_000, true, true, 3, 15)),
        ("claude-haiku-4", new(200_000, 64_000, true, true, 1, 5)),
        ("claude-3-7-sonnet", new(200_000, 64_000, true, true, 3, 15)),
        ("claude-3-5-sonnet", new(200_000, 8_192, false, true, 3, 15)),
        ("claude-3-5-haiku", new(200_000, 8_192, false, true, 0.8, 4)),
        ("claude", new(200_000, 8_192, false, true, 3, 15)),
        ("gpt-5-mini", new(400_000, 128_000, true, true, 0.25, 2)),
        ("gpt-5-nano", new(400_000, 128_000, true, true, 0.05, 0.4)),
        ("gpt-5", new(400_000, 128_000, true, true, 1.25, 10)),
        ("gpt-4.1-mini", new(1_047_576, 32_768, false, true, 0.4, 1.6)),
        ("gpt-4.1-nano", new(1_047_576, 32_768, false, true, 0.1, 0.4)),
        ("gpt-4.1", new(1_047_576, 32_768, false, true, 2, 8)),
        ("gpt-4o-mini", new(128_000, 16_384, false, true, 0.15, 0.6)),
        ("gpt-4o", new(128_000, 16_384, false, true, 2.5, 10)),
        ("gpt-oss", new(131_072, 32_768, true, false)),
        ("o4-mini", new(200_000, 100_000, true, true, 1.1, 4.4)),
        ("o3-mini", new(200_000, 100_000, true, false, 1.1, 4.4)),
        ("o3", new(200_000, 100_000, true, true, 2, 8)),
        ("o1", new(200_000, 100_000, true, true, 15, 60)),
        ("gemini-2.5-pro", new(1_048_576, 65_536, true, true, 1.25, 10)),
        ("gemini-2.5-flash-lite", new(1_048_576, 65_536, true, true, 0.1, 0.4)),
        ("gemini-2.5-flash", new(1_048_576, 65_536, true, true, 0.3, 2.5)),
        ("gemini-2.0-flash", new(1_048_576, 8_192, false, true, 0.1, 0.4)),
        ("gemini", new(1_048_576, 8_192, false, true)),
        ("deepseek-reasoner", new(128_000, 32_768, true, false, 0.55, 2.19)),
        ("deepseek-chat", new(128_000, 8_192, false, false, 0.27, 1.1)),
        ("grok-4", new(256_000, 32_768, true, true, 3, 15)),
        ("grok-3-mini", new(131_072, 16_384, true, false, 0.3, 0.5)),
        ("grok", new(131_072, 16_384, false, false, 3, 15)),
        ("mistral-large", new(131_072, 8_192, false, false, 2, 6)),
        ("codestral", new(256_000, 8_192, false, false, 0.3, 0.9)),
        ("devstral", new(131_072, 8_192, false, false)),
        ("kimi-k2", new(131_072, 16_384, false, false)),
        ("llama-4", new(131_072, 8_192, false, true)),
        ("llama-3", new(131_072, 8_192, false, false)),
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

    public static int DefaultMaxOutput(int context) => Math.Clamp(context / 4, 1024, 32_768);
}
