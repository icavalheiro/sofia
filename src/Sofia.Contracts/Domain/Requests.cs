using System.Text.Json;

namespace Sofia.Contracts.Domain;

// Request shapes of the HTTP API. They live in the contract assembly so every client (HTTP, in-process, the WASM
// head) posts the same objects and the endpoints bind them without a translation layer.

public sealed record SessionReference(string Id, string Title);

/// <summary>Body of <c>POST /api/sessions/{id}/prompt</c>.</summary>
public sealed record PromptInput(
    string Text,
    List<string>? Attachments = null,
    string? Delivery = null,
    string? ModelId = null,
    string? Agent = null,
    string? PermissionMode = null,
    string? ReasoningMode = null,
    string? Language = null,
    List<SessionReference>? References = null);

public sealed record CreateWorkspaceRequest(string? Kind, string? Path, string? Name, string? Icon, string? ModelId);
public sealed record UpdateWorkspaceRequest(string? Name, string? Icon, string? ModelId, bool? Expanded, int? Order, bool? Sandbox = null);
public sealed record CreateSessionRequest(string? Title, string? Origin = null);
public sealed record ReorderRequest(List<string>? Ids);
public sealed record UpdateSessionRequest(string? Title, string? ModelId, string? Agent, string? PermissionMode, string? ReasoningMode, bool? Sandbox = null);
public sealed record CommandRequest(string Name, string? Arguments, List<string>? Attachments = null);
public sealed record RevertRequest(string MessageId);
public sealed record PermissionReplyRequest(string Decision);
public sealed record AnswerRequest(List<JsonElement?> Answers);
public sealed record SkillRequest(string SkillId);
public sealed record SessionMcpRequest(string? Name, string? Transport, string? Command, List<string>? Args, string? Url, string? Token, Dictionary<string, string>? Env);
public sealed record ImportRequest(string? WorkspaceId, Session Info, List<Message>? Messages);

public sealed record CreateProviderRequest(string Name, string? Type, string BaseUrl, string? ApiKey, List<string>? Models, string? PresetId);
public sealed record UpdateProviderRequest(bool? Enabled, string? ApiKey, string? BaseUrl, string? Name, bool? PreserveThinking = null, string? Icon = null);
public sealed record ProbeRequest(string? Type, string BaseUrl, string? ApiKey);
public sealed record CopilotPollRequest(string DeviceCode);
public sealed record SetModelEnabledRequest(string? Id, List<string>? Ids, bool Enabled);
public sealed record CreateMcpRequest(string Name, string? Transport, string? Command, string? Url, string? Token, Dictionary<string, string>? Env);
public sealed record CreateWorktreeRequest(string Directory, string Name);
public sealed record DeleteWorktreeRequest(string Directory, string Worktree, bool? Force);
public sealed record OpenRequest(string Path, string? App);
public sealed record PairConnectRequest(string Code);

/// <summary>Partial server-config update. Properties (not constructor parameters) so a partial JSON body binds by name.</summary>
public sealed record ConfigPatch
{
    public string? DefaultModel { get; init; }
    /// <summary>Vision model ('providerId/modelId') that describes images for chats whose model cannot see them; '' disables it.</summary>
    public string? ImageFallbackModel { get; init; }
    public string? Shell { get; init; }
    public bool? AutoCompact { get; init; }
    public double? CompactThreshold { get; init; }
    public bool? PruneToolResults { get; init; }
    public double? PruneThreshold { get; init; }
    public int? PruneKeepSteps { get; init; }
    public int? PruneMinChars { get; init; }
    public bool? Discovery { get; init; }
    public int? MaxToolOutput { get; init; }
    public bool? KeepScreenAwake { get; init; }
    public bool? Knowledge { get; init; }
    public string? SearchProvider { get; init; }
    public string? SearxngUrl { get; init; }
    public bool? Sandbox { get; init; }
    public bool? SandboxNetwork { get; init; }
    /// <summary>'auto' | 'always' | 'never' — exposure of the `apply_patch` tool.</summary>
    public string? ApplyPatch { get; init; }
    /// <summary>Read-before-edit policy for edit/apply_patch/write-over-existing.</summary>
    public bool? ReadBeforeEdit { get; init; }
    /// <summary>'auto' or a browser catalog id (chrome, edge, brave, vivaldi, opera, chromium, firefox).</summary>
    public string? DefaultBrowser { get; init; }
    public LspConfigPatch? Lsp { get; init; }
}

/// <summary>Partial LSP config update; per-server entries merge with the stored ones.</summary>
public sealed record LspConfigPatch(bool? Enabled, bool? AutoDiagnostics, Dictionary<string, LspServerSettingPatch>? Servers);

public sealed record LspServerSettingPatch(bool? Enabled, string? Command);
