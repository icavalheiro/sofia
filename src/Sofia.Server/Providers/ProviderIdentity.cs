using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Providers;

/// <summary>
/// How the harness identifies itself to upstream APIs. Gateways use a conversation id for sticky routing, session
/// aggregation and prompt caching, and some reject requests without one (OpenCode Go answers 400 `MissingSessionID`);
/// subscription gateways also reject a generic SDK user agent. Every LLM request therefore carries the Sofia agent
/// user agent where it matters and the session headers below everywhere else.
/// </summary>
public static class ProviderIdentity
{
    /// <summary>Application version as built (e.g. `0.2.0`); falls back to the assembly version.</summary>
    public static string Version { get; } =
        typeof(ProviderIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ProviderIdentity).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>OpenCode Go asks clients to identify themselves with their own agent user agent, not a generic one.</summary>
    public static string UserAgent => $"sofia/{Version} (coding agent; https://github.com/icavalheiro/sofia)";

    /// <summary>Session header names sent with every LLM request, all carrying the same conversation id.</summary>
    private static readonly string[] Names = ["x-session-id", "x-litellm-trace-id", "x-claude-code-session-id", "x-dynamo-session-id"];

    /// <summary>Vendor-specific names: OpenCode Go and AWS Bedrock AgentCore use their own.</summary>
    private const string OpenCodeSessionHeader = "x-opencode-session";
    private const string ClaudeCodeSessionHeader = "x-claude-code-session-id";
    private const string AgentCoreRuntimeSessionHeader = "x-amzn-bedrock-agentcore-runtime-session-id";
    private const string AgentCorePolicySessionHeader = "x-amzn-bedrock-agentcore-policy-session-id";

    /// <summary>
    /// A gateway reads the session header it implements and ignores the rest: `x-session-id` (generic and self-built
    /// proxies), `x-litellm-trace-id` (LiteLLM's highest-priority session header), `x-claude-code-session-id` (Claude
    /// Code compatible gateways, and LiteLLM's `x-&lt;vendor&gt;-session-id` fallback) and `x-dynamo-session-id`
    /// (NVIDIA Dynamo). Claude Code's name is dropped when the endpoint is Anthropic itself: there is no proxy in
    /// between, and naming our conversation after another vendor's session scheme would be false.
    /// </summary>
    private static IEnumerable<string> SessionHeaderNames(ProviderConfig provider) =>
        provider.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase)
            ? Names.Except([ClaudeCodeSessionHeader])
            : Names;

    /// <summary>
    /// Id of requests that belong to no conversation (model discovery, provider probes). One per process, so those
    /// calls still group together instead of arriving with no session at all.
    /// </summary>
    public static string HarnessSessionId { get; } = Ids.New("harness");

    /// <summary>
    /// Adds the identity headers of one request. Every provider gets them: `sessionId` is the conversation id, and a
    /// request without one falls back to <see cref="HarnessSessionId"/> so no LLM request ever leaves unidentified.
    /// </summary>
    public static void ApplyIdentity(HttpRequestMessage request, ProviderConfig provider, string? sessionId)
    {
        if (OpenCodeGo.IsGo(provider)) ApplyHeader(request, "User-Agent", UserAgent);

        var id = string.IsNullOrWhiteSpace(sessionId) ? HarnessSessionId : sessionId.Trim();
        foreach (var name in SessionHeaderNames(provider)) request.Headers.TryAddWithoutValidation(name, id);
        if (OpenCodeGo.IsGo(provider)) request.Headers.TryAddWithoutValidation(OpenCodeSessionHeader, id);
        // AWS Bedrock AgentCore rejects a runtime session id shorter than 33 characters, so that one is padded.
        if (IsAgentCore(provider))
        {
            request.Headers.TryAddWithoutValidation(AgentCoreRuntimeSessionHeader, Padded(id, 33));
            request.Headers.TryAddWithoutValidation(AgentCorePolicySessionHeader, id);
        }
    }

    /// <summary>AgentCore is reached through its runtime data-plane host rather than a preset Sofia ships.</summary>
    public static bool IsAgentCore(ProviderConfig provider) =>
        provider.BaseUrl.Contains("bedrock-agentcore", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reaches `minLength` by appending a digest of the id, so the value stays stable and still unique.</summary>
    internal static string Padded(string id, int minLength)
    {
        if (id.Length >= minLength) return id;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        var builder = new StringBuilder(id);
        while (builder.Length < minLength) builder.Append(digest);
        return builder.ToString(0, Math.Min(minLength, 256));
    }

    /// <summary>
    /// Sets a request header over whatever the HttpClient defaults carry. A per-request `User-Agent` added with
    /// TryAddWithoutValidation is appended to the client default instead of replacing it, so that header goes through
    /// the parser on a cleared collection.
    /// </summary>
    public static void ApplyHeader(HttpRequestMessage request, string name, string value)
    {
        if (!name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(name, value);
            return;
        }
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(value);
    }
}
