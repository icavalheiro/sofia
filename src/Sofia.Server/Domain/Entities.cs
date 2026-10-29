using System.Text.Json.Serialization;
using LiteDB;

namespace Sofia.Server.Domain;

// Entities are persisted by LiteDB and serialized straight to the UI. Fields the
// UI must never see carry [JsonIgnore]; LiteDB still stores them.

public static class SessionStatus
{
    public const string Idle = "idle";
    public const string Working = "working";
    /// <summary>Queued behind another conversation for a single-client model (no activity, no spinner).</summary>
    public const string Waiting = "waiting";
    public const string Permission = "permission";
    public const string Question = "question";
    public const string Error = "error";
}

public sealed class Workspace
{
    [BsonId] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>'folder' or 'chat'.</summary>
    public string Kind { get; set; } = "folder";
    public string Icon { get; set; } = "folder";
    public string ModelId { get; set; } = "";
    public bool Expanded { get; set; } = true;
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore, BsonIgnore] public bool IsFolder => Kind == "folder" && !string.IsNullOrEmpty(Path);
}

public sealed class Usage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double Cost { get; set; }

    public void Add(Usage other)
    {
        Input += other.Input;
        Output += other.Output;
        Reasoning += other.Reasoning;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        Cost += other.Cost;
    }

    public Usage Clone() => (Usage)MemberwiseClone();
}

public sealed class ContextInfo
{
    public int Used { get; set; }
    public int Limit { get; set; }
}

public sealed class Session
{
    [BsonId] public string Id { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string? ParentId { get; set; }
    public string Title { get; set; } = Defaults.Title;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string ModelId { get; set; } = "";
    public string Agent { get; set; } = "build";
    public string PermissionMode { get; set; } = "ask";
    public string ReasoningMode { get; set; } = "medium";
    /// <summary>UI language code (e.g. "pt") the agent answers in by default.</summary>
    public string? Language { get; set; }
    public string Status { get; set; } = SessionStatus.Idle;
    /// <summary>Hidden from the conversation list and parked in the workspace's 'Archived' folder.</summary>
    public bool Archived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    /// <summary>Kept at the top of the workspace's conversation list.</summary>
    public bool Pinned { get; set; }
    /// <summary>Manual position in the list (ascending); 0 = never placed, so it sorts by recency.</summary>
    public int Order { get; set; }
    public Usage Usage { get; set; } = new();
    public ContextInfo Context { get; set; } = new();
    /// <summary>How long the last request took, start to finish, minus the time spent waiting on the user's answers.</summary>
    public long LastTurnMs { get; set; }
    /// <summary>When the last request finished; null before the first one.</summary>
    public DateTime? LastTurnAt { get; set; }
    public List<string> ActiveSkills { get; set; } = [];
    /// <summary>Highest message sequence already reviewed for knowledge notes.</summary>
    [JsonIgnore] public long KnowledgeSeq { get; set; }
    /// <summary>System prompt written once for the whole conversation (persisted, never exposed).</summary>
    [JsonIgnore] public string? FrozenPrompt { get; set; }
    /// <summary>Agent id the frozen prompt was built for.</summary>
    [JsonIgnore] public string? FrozenAgent { get; set; }
    /// <summary>SHA-256 of the tool schemas the frozen prompt was built for.</summary>
    [JsonIgnore] public string? FrozenTools { get; set; }
    /// <summary>Volatile facts baked into the frozen prompt (or already delivered as context updates).</summary>
    [JsonIgnore] public Dictionary<string, string>? PromptFacts { get; set; }
    [BsonIgnore] public int Queued { get; set; }
    /// <summary>Prompts accepted while a run is busy but not yet sent to the model (steer first, then queued). Replaced, never mutated.</summary>
    [BsonIgnore] public List<QueuedMessage> QueuedMessages { get; set; } = [];

    /// <summary>Tools the user allowed "for this session".</summary>
    [JsonIgnore] public List<string> AllowedTools { get; set; } = [];

    public static class Defaults
    {
        public const string Title = "New conversation";
    }
}

/// <summary>A user prompt waiting for its turn; <paramref name="Delivery"/> is 'steer' or 'queue'.</summary>
public sealed record QueuedMessage(string Id, string Text, string Delivery, List<Attachment> Attachments);

public sealed class Attachment
{
    [BsonId] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>'image' | 'video' | 'audio' | 'file'.</summary>
    public string Kind { get; set; } = "file";
    public string Mime { get; set; } = "application/octet-stream";
    public string Size { get; set; } = "";
    public long Bytes { get; set; }
    public string Url { get; set; } = "";
    [JsonIgnore] public string StoragePath { get; set; } = "";
    [JsonIgnore] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>'running' | 'success' | 'error'.</summary>
    public string Status { get; set; } = "running";
    public string Summary { get; set; } = "";
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string Duration { get; set; } = "running…";
    public string? ChildSessionId { get; set; }
    /// <summary>Media the tool produced (browser screenshots, images, audio or video read from disk); also sent to models that accept it.</summary>
    public List<Attachment>? Images { get; set; }

    /// <summary>Raw JSON arguments exactly as the model produced them.</summary>
    [JsonIgnore] public string Arguments { get; set; } = "";
    /// <summary>Text handed back to the model (may differ from the display output).</summary>
    [JsonIgnore] public string? Result { get; set; }
    [JsonIgnore] public bool Completed { get; set; }
}

public sealed class PermissionCard
{
    public string Tool { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Choices { get; set; } = [];
    public string? Resolved { get; set; }
    public string RequestId { get; set; } = "";
}

public sealed class QuestionCard
{
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string? Resolved { get; set; }
    public string FormId { get; set; } = "";
    public string FieldKey { get; set; } = "answer";
    public bool Multi { get; set; }
}

public sealed class QuestionItem
{
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string FieldKey { get; set; } = "";
    public bool Multi { get; set; }
}

public sealed class QuestionSetCard
{
    public List<QuestionItem> Questions { get; set; } = [];
    public List<string?>? Answers { get; set; }
    public bool? Confirmed { get; set; }
    public string FormId { get; set; } = "";
}

/// <summary>One key/value pair of a workspace's `memory` store; never injected into the system prompt.</summary>
public sealed class MemoryEntry
{
    [BsonId] public string Id { get; set; } = Infrastructure.Ids.New("mem");
    public string WorkspaceId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class KnowledgeScope
{
    /// <summary>Facts about one workspace (project conventions, commands, decisions).</summary>
    public const string Workspace = "workspace";
    /// <summary>How an agent should behave everywhere (user preferences, style).</summary>
    public const string Agent = "agent";
    /// <summary>Scratch notes the agent keeps for one chat through the `notes` tool; never injected into the prompt or listed by the API.</summary>
    public const string Chat = "chat";
}

/// <summary>A short learning the harness distilled from a conversation; injected into the system prompt.</summary>
public sealed class KnowledgeNote
{
    [BsonId] public string Id { get; set; } = "";
    /// <summary>'workspace' | 'agent' | 'chat'.</summary>
    public string Scope { get; set; } = KnowledgeScope.Workspace;
    public string WorkspaceId { get; set; } = "";
    /// <summary>Agent id the note belongs to (always set; only used for lookup when the scope is 'agent').</summary>
    public string Agent { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Why the reviewer judged the note worth keeping forever: the future situation where it helps. Never injected into the prompt.</summary>
    public string Why { get; set; } = "";
    public string SessionId { get; set; } = "";
    /// <summary>Chat card that announced the note.</summary>
    public string MessageId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Snapshot of a note on its chat card; <see cref="Deleted"/> once the user removed it.</summary>
public sealed class KnowledgeEntry
{
    public string Id { get; set; } = "";
    public string Scope { get; set; } = KnowledgeScope.Workspace;
    public string Text { get; set; } = "";
    /// <summary>The utility argument the reviewer gave for the note, shown on the card.</summary>
    public string Why { get; set; } = "";
    public bool Deleted { get; set; }
}

/// <summary>Progress/result of a context compaction, shown as a system message in the chat.</summary>
public sealed class CompactionNotice
{
    /// <summary>'running' | 'done' | 'failed' | 'skipped'.</summary>
    public string Status { get; set; } = "running";
    /// <summary>True when triggered by the context threshold, false for /compact.</summary>
    public bool Auto { get; set; }
    /// <summary>Estimated context tokens before compaction.</summary>
    public int Before { get; set; }
    /// <summary>Estimated context tokens after compaction (done only).</summary>
    public int? After { get; set; }
    public string? Error { get; set; }
    /// <summary>The generated summary (done only), so the card can expand to show it.</summary>
    public string? Result { get; set; }
}

/// <summary>
/// A transient provider failure Sofia is repeating: shown on the assistant card while the next attempt runs and
/// cleared by the attempt that succeeds. Distinct from <see cref="Message.Error"/>, which is terminal.
/// </summary>
public sealed class RetryNotice
{
    /// <summary>The attempt now starting (1-based).</summary>
    public int Attempt { get; set; } = 1;
    /// <summary>Attempts Sofia will make before it gives up and shows the error.</summary>
    public int Attempts { get; set; } = 1;
    /// <summary>Why the previous attempt failed, one line.</summary>
    public string? Error { get; set; }
}

public sealed class Message
{
    [BsonId] public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    /// <summary>'user' | 'assistant'.</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Model { get; set; }
    public string? Reasoning { get; set; }
    public string? ReasoningMode { get; set; }
    public List<Attachment>? Attachments { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public PermissionCard? Permission { get; set; }
    public QuestionCard? Question { get; set; }
    public QuestionSetCard? QuestionSet { get; set; }
    /// <summary>Notes learned from the conversation (card-only message).</summary>
    public List<KnowledgeEntry>? Knowledge { get; set; }
    public string? Error { get; set; }
    public bool? Summary { get; set; }
    /// <summary>System notice card (compaction progress/result); UI-only, never replayed to the model.</summary>
    public CompactionNotice? Compaction { get; set; }
    /// <summary>Transient provider failure being retried; UI-only, cleared by the attempt that succeeds.</summary>
    public RetryNotice? Retry { get; set; }
    public Usage? Usage { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>Changes since the frozen system prompt was written, one line each; replayed to the model as &lt;context_updates&gt;.</summary>
    public List<string>? ContextNotes { get; set; }

    /// <summary>Monotonic order within the session.</summary>
    [JsonIgnore] public long Seq { get; set; }
    /// <summary>Folded into a compaction summary; kept for the transcript, skipped in model history.</summary>
    [JsonIgnore] public bool Compacted { get; set; }
    /// <summary>UI-only card (permission/question prompts): never replayed to the model.</summary>
    [JsonIgnore] public bool Synthetic { get; set; }
    [JsonIgnore] public string? ReasoningSignature { get; set; }
    /// <summary>Full model id ('provider/model') that produced the message.</summary>
    [JsonIgnore] public string? ModelId { get; set; }
    /// <summary>Text the model sees when it differs from the displayed text (expanded /commands).</summary>
    [JsonIgnore] public string? Expanded { get; set; }
    /// <summary>Injected mid-run (steering): the model sees it appended to the tool results it interrupted, not as a new user turn.</summary>
    [JsonIgnore] public bool Steered { get; set; }
}

public sealed class ProviderConfig
{
    [BsonId] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>'openai' | 'openai-responses' | 'anthropic' — the wire protocol.</summary>
    public string Type { get; set; } = "openai";
    /// <summary>'local' | 'cloud'.</summary>
    public string Kind { get; set; } = "cloud";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Environment variable the key is read from when <see cref="ApiKey"/> is empty.</summary>
    public string? ApiKeyEnv { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Custom { get; set; }
    public bool Discovered { get; set; }
    public string? PresetId { get; set; }
    /// <summary>Runtime flavour used for request quirks ('unsloth', 'ollama', 'llamacpp', 'vllm', 'lmstudio', 'openai', 'openrouter', …).</summary>
    public string Flavor { get; set; } = "openai";
    /// <summary>Resend earlier reasoning with the history (OpenAI-compatible `reasoning_content`). Null = flavor default.</summary>
    public bool? PreserveThinking { get; set; }
    /// <summary>Manually declared models (used when the endpoint cannot list them).</summary>
    public List<string> Models { get; set; } = [];
    /// <summary>Last context window a runtime reported per model (runtimes omit it while a model is unloaded).</summary>
    public Dictionary<string, int> KnownContext { get; set; } = [];

    public string? ResolveKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey
        : !string.IsNullOrWhiteSpace(ApiKeyEnv) ? Environment.GetEnvironmentVariable(ApiKeyEnv)
        : null;
}

public sealed class McpServerConfig
{
    [BsonId] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>'command' | 'web'.</summary>
    public string Transport { get; set; } = "command";
    public string Command { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Token { get; set; }
    public Dictionary<string, string> Env { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

/// <summary>Content of a file before a session first modified it within one message.</summary>
public sealed class FileSnapshot
{
    [BsonId] public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public long MessageSeq { get; set; }
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ServerConfig
{
    [BsonId] public string Id { get; set; } = "config";
    public string DefaultModel { get; set; } = "";
    public string Shell { get; set; } = "";
    public bool AutoCompact { get; set; } = true;
    public double CompactThreshold { get; set; } = 0.8;
    public bool Discovery { get; set; } = true;
    public int MaxToolOutput { get; set; } = 50_000;
    /// <summary>Keep the display (and system) from sleeping while an agent is working.</summary>
    public bool KeepScreenAwake { get; set; }
    /// <summary>Distil knowledge notes from finished conversations.</summary>
    public bool Knowledge { get; set; } = true;
    /// <summary>Backend of the web_search tool: 'duckduckgo' or 'searxng'.</summary>
    public string SearchProvider { get; set; } = "duckduckgo";
    /// <summary>Base URL of the SearXNG instance used when <see cref="SearchProvider"/> is 'searxng'.</summary>
    public string SearxngUrl { get; set; } = "";
    /// <summary>Model ids ('providerId/modelId') hidden from the harness: not offered in pickers and never chosen as default.</summary>
    public List<string> DisabledModels { get; set; } = [];
    /// <summary>Model ids that serve one conversation at a time: runs are queued (subagents first) and a conversation keeps the model until its run ends. Local models are single client by default, so they only need to be listed here once they were switched off and back on.</summary>
    public List<string> SingleClientModels { get; set; } = [];
    /// <summary>Model ids of local runtimes that the user explicitly allowed to serve several conversations at once (the exception to the local-is-single-client default).</summary>
    public List<string> MultiClientModels { get; set; } = [];
}

public sealed class AccessToken
{
    /// <summary>SHA-256 of the bearer token.</summary>
    [BsonId] public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
