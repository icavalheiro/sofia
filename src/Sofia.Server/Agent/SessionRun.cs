using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Changes;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Providers;
using Sofia.Server.Tools;
using Sofia.Server.Vcs;

namespace Sofia.Server.Agent;

/// <summary>A unit of work waiting for a run: a user prompt or a compaction request.</summary>
public sealed record PendingPrompt(string MessageId, string Kind, string Text, string? Expanded, List<Attachment> Attachments)
{
    /// <summary>False for the prompt that starts a run: it is consumed at once, so clients need no "waiting" preview.</summary>
    public bool Announce { get; init; } = true;

    public static PendingPrompt Compact() => new(Ids.New("msg"), "compact", "", null, []);
}

/// <summary>Services shared by every run (resolved once from DI).</summary>
public sealed class RunServices(
    SofiaDb db,
    SessionStore store,
    ProviderService providers,
    ToolRegistry tools,
    ContextFiles contextFiles,
    SkillService skills,
    AgentCatalog agentCatalog,
    KnowledgeService knowledge,
    ChangeTracker changes,
    GitService git,
    ILogger<SessionRun> log)
{
    public SofiaDb Db => db;
    public SessionStore Store => store;
    public ProviderService Providers => providers;
    public ToolRegistry Tools => tools;
    public ContextFiles ContextFiles => contextFiles;
    public SkillService Skills => skills;
    public AgentCatalog AgentCatalog => agentCatalog;
    public KnowledgeService Knowledge => knowledge;
    public ChangeTracker Changes => changes;
    public GitService Git => git;
    public ILogger<SessionRun> Log => log;
}

/// <summary>
/// One agent loop for one session (pi-style): stream a step, run its tool calls, repeat
/// until the model stops calling tools; then drain steering and follow-up prompts.
/// </summary>
public sealed class SessionRun : IToolHost
{
    public static readonly List<string> PermissionChoices = ["Allow once", "Allow for this session", "Deny"];

    private readonly RunServices _s;
    private readonly AgentService _agents;
    private readonly CancellationTokenSource _cts;
    private readonly object _gate = new();
    private readonly Queue<PendingPrompt> _steer = new();
    private readonly Queue<PendingPrompt> _followUps = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<string> Tcs, Message Card)> _permissions = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<List<string>> Tcs, Message Card)> _questions = new();
    private readonly Stopwatch _clock = new();
    private bool _finished;
    private Message? _current;
    private DateTime _lastProgressPublish = DateTime.MinValue;

    public SessionRun(RunServices services, AgentService agents, Session session, Workspace workspace, SessionRun? root, CancellationToken parent)
    {
        _s = services;
        _agents = agents;
        Session = session;
        Workspace = workspace;
        Root = root ?? this;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    public Session Session { get; }
    public Workspace Workspace { get; }
    public SessionRun Root { get; }
    public string? LastError { get; private set; }
    public bool HasPendingQuestion => !_questions.IsEmpty;

    private string Cwd => Workspace.IsFolder ? Workspace.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // --- queueing ------------------------------------------------------------

    public bool TryEnqueue(PendingPrompt prompt, bool steer)
    {
        lock (_gate)
        {
            if (_finished) return false;
            (steer ? _steer : _followUps).Enqueue(prompt);
            SyncQueue();
        }
        _s.Store.SaveSession(Session);
        return true;
    }

    /// <summary>Refreshes the queue fields clients render. Caller holds <c>_gate</c>.</summary>
    private void SyncQueue()
    {
        Session.Queued = _steer.Count + _followUps.Count;
        Session.QueuedMessages = _steer.Select(p => (Prompt: p, Delivery: "steer"))
            .Concat(_followUps.Select(p => (Prompt: p, Delivery: "queue")))
            .Where(x => x.Prompt is { Kind: "prompt", Announce: true })
            .Select(x => new QueuedMessage(x.Prompt.MessageId, x.Prompt.Text, x.Delivery, x.Prompt.Attachments))
            .ToList();
    }

    public void Start() => _ = Task.Run(() => RunAsync());

    public void Abort() => _cts.Cancel();

    private bool HasSteering
    {
        get
        {
            lock (_gate) return _steer.Count > 0;
        }
    }

    // --- main loop -------------------------------------------------------------

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        _clock.Start();
        _s.Store.SetStatus(Session, SessionStatus.Working);
        var aborted = false;
        try
        {
            while (true)
            {
                PendingPrompt next;
                lock (_gate)
                {
                    if (_steer.Count == 0 && _followUps.Count == 0)
                    {
                        _finished = true;
                        break;
                    }
                    next = _steer.Count > 0 ? _steer.Dequeue() : _followUps.Dequeue();
                    SyncQueue();
                }

                if (next.Kind == "compact")
                {
                    _ = await CompactAsync(auto: false, ct);
                    continue;
                }
                // A new turn: re-read the live facts once (frozen prompt reused, changes become context notes).
                _turnPrompt = null;
                var (_, _, notes) = await TurnContextAsync(_s.Db.GetConfig(), ct);
                AddUserMessage(next, contextNotes: notes);
                // Only after the message is persisted: a crash replays the change once more instead of losing it.
                CommitFacts();
                await RunTurnAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            aborted = true;
        }
        catch (Exception ex)
        {
            LastError = ex is ProviderException or InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            _s.Log.LogWarning(ex, "Run for session {Session} failed", Session.Id);
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                _steer.Clear();
                _followUps.Clear();
                SyncQueue();
            }
            Cleanup(aborted);
            _agents.Finished(this);
        }
    }

    private void Cleanup(bool aborted)
    {
        // A run that ends while a card is waiting would leave it clickable forever: AskAsync/permission
        // finally-blocks settle them. Here we only release anything still pending.
        foreach (var (_, pending) in _permissions) pending.Tcs.TrySetCanceled();
        foreach (var (_, pending) in _questions) pending.Tcs.TrySetCanceled();

        if (_current is { } current)
        {
            var changed = false;
            lock (current)
            {
                foreach (var call in current.ToolCalls ?? [])
                {
                    if (call.Completed) continue;
                    call.Status = "error";
                    call.Error = aborted ? "Aborted." : "Interrupted.";
                    call.Duration = "—";
                    call.Completed = true;
                    changed = true;
                }
                if (aborted && current.Error is null)
                {
                    current.Error = "Stopped by the user.";
                    changed = true;
                }
                if (LastError is not null && current.Error is null)
                {
                    current.Error = LastError;
                    changed = true;
                }
            }
            if (changed) _s.Store.UpdateMessage(Session, current);
        }
        else if (LastError is not null)
        {
            _s.Store.AddMessage(Session, new Message { Role = "assistant", Error = LastError, Model = ModelLabel() });
        }

        _s.Store.SaveSession(Session);
        _s.Store.SetStatus(Session, LastError is null ? SessionStatus.Idle : SessionStatus.Error, LastError);
        PublishMetrics(0);
    }

    private void AddUserMessage(PendingPrompt prompt, bool steered = false, List<string>? contextNotes = null)
    {
        _s.Store.AddMessage(Session, new Message
        {
            Id = prompt.MessageId,
            Role = "user",
            Text = prompt.Text,
            Expanded = prompt.Expanded,
            Attachments = prompt.Attachments.Count > 0 ? prompt.Attachments : null,
            Steered = steered,
            ContextNotes = contextNotes,
        });
    }

    private void InjectSteering()
    {
        while (true)
        {
            PendingPrompt next;
            lock (_gate)
            {
                if (_steer.Count == 0) return;
                next = _steer.Dequeue();
                SyncQueue();
            }
            if (next.Kind == "compact") continue;
            AddUserMessage(next, steered: true);
        }
    }

    private (ProviderConfig Provider, ModelInfo Model) ResolveModel()
    {
        if (string.IsNullOrEmpty(Session.ModelId))
        {
            Session.ModelId = _s.Providers.DefaultModelId();
            _s.Store.SaveSession(Session);
        }
        var resolved = _s.Providers.Resolve(Session.ModelId);
        if (resolved is null)
            throw new InvalidOperationException(string.IsNullOrEmpty(Session.ModelId)
                ? "No model is available. Start a local runtime (Unsloth, Ollama, LM Studio, llama.cpp, vLLM) or add a provider in Settings → Models."
                : $"Model '{Session.ModelId}' is not available. Pick another model.");
        if (!resolved.Value.Provider.Enabled) throw new InvalidOperationException($"Provider '{resolved.Value.Provider.Name}' is disabled.");
        return resolved.Value;
    }

    private string ModelLabel() => _s.Providers.Resolve(Session.ModelId)?.Model.Label ?? Session.ModelId;

    private async Task RunTurnAsync(CancellationToken ct)
    {
        // The request prefix (system prompt + tool schemas) is frozen for the whole CONVERSATION, not just the turn: it is
        // written once and stored on the session. Providers cache on the longest common prefix, so one changed character
        // would re-bill every token. Whatever changed since (notes, branch, date, mode, language, context files, skills)
        // arrives as <context_updates> on the newest user message (see `TurnContextAsync`), appended where it never
        // invalidates the cache. Only an agent or tool-set change re-freezes, and then the prefix is doomed anyway.
        // History stays append-only within a run (see `HistoryBuilder`).
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (provider, model) = ResolveModel();
            var config = _s.Db.GetConfig();
            Session.Context.Limit = model.ContextLength;
            var (system, tools, _) = await TurnContextAsync(config, ct);
            var history = HistoryBuilder.Build(_s.Db.SessionMessages(Session.Id), model);
            var estimatedInput = Text.EstimateTokens(system) + history.Sum(m => Text.EstimateTokens(m.TextContent) + m.ToolCalls.Sum(t => Text.EstimateTokens(t.Arguments)));

            // `Context.Used` only reflects the previous response; tool results and the new user message added since then
            // are not in it. Compare against the estimate of what is about to be sent as well, otherwise a large tool
            // output pushes the next request past the window before compaction ever triggers.
            var threshold = config.CompactThreshold * model.ContextLength;
            if (config.AutoCompact && Math.Max(Session.Context.Used, estimatedInput) > threshold)
            {
                if (await CompactAsync(auto: true, ct))
                {
                    history = HistoryBuilder.Build(_s.Db.SessionMessages(Session.Id), model);
                    estimatedInput = Text.EstimateTokens(system) + history.Sum(m => Text.EstimateTokens(m.TextContent) + m.ToolCalls.Sum(t => Text.EstimateTokens(t.Arguments)));
                }
            }
            var room = model.ContextLength - estimatedInput - 512;
            var request = new ChatRequest
            {
                Model = model.ModelId,
                System = system,
                Messages = history,
                Tools = tools.Select(t => new ToolSpec(t.Name, t.Description, t.Parameters)).ToList(),
                Reasoning = Session.ReasoningMode,
                MaxTokens = Math.Max(1024, Math.Min(model.MaxOutput, room)),
                ModelSupportsReasoning = model.Reasoning,
                SessionId = Session.Id,
            };

            var assistant = _s.Store.AddMessage(Session, new Message
            {
                Role = "assistant",
                Model = model.Label,
                ModelId = model.Id,
                ReasoningMode = Session.ReasoningMode,
            });
            _current = assistant;

            var (calls, finish) = await StreamStepAsync(provider, model, request, assistant, tools, estimatedInput, ct);
            if (calls.Count == 0)
            {
                if (finish == "length")
                {
                    lock (assistant) assistant.Error ??= "The reply was cut off at the output token limit.";
                    _s.Store.UpdateMessage(Session, assistant);
                }
                return;
            }

            await ExecuteToolsAsync(assistant, calls, tools, ct);
            InjectSteering();
        }
    }

    private (string System, List<ITool> Tools, List<string>? Delta, string Agent)? _turnPrompt;
    private Dictionary<string, string>? _pendingFacts;

    /// <summary>
    /// The system prompt, tool set and context delta of the current turn. The prompt is frozen on the session for the whole
    /// conversation; it is rebuilt (and the delta dropped) only when the agent or the tool-schema fingerprint changed.
    /// Otherwise the live facts are diffed against <see cref="Session.PromptFacts"/>. The result is cached in
    /// <c>_turnPrompt</c> (reset by <c>RunAsync</c> at turn start) so every step reuses the same system string and delta.
    /// </summary>
    private async Task<(string System, List<ITool> Tools, List<string>? Delta)> TurnContextAsync(ServerConfig config, CancellationToken ct)
    {
        if (_turnPrompt is { } cached && cached.Agent == Session.Agent) return (cached.System, cached.Tools, cached.Delta);
        var agent = _s.AgentCatalog.Get(Session.Agent, Workspace.IsFolder ? Workspace.Path : null);
        var tools = _s.Tools.For(agent, Workspace);
        // Subagents never ask the user or spawn further subagents.
        if (Session.ParentId is not null) tools.RemoveAll(t => t.Kind is ToolKind.Agent or ToolKind.Interactive);
        var fingerprint = Text.Sha256(string.Join("\n", tools.Select(t => t.Name + "\u0000" + Text.FirstLine(t.Description, 400))));

        var (snapshot, overrides, shell) = await PromptSnapshotAsync(agent, config, ct);
        string system;
        List<string>? delta = null;
        if (Session.FrozenPrompt is null || Session.FrozenAgent != Session.Agent || Session.FrozenTools != fingerprint)
        {
            system = SystemPromptBuilder.Build(agent, Workspace, Session, tools, snapshot.ContextFiles, snapshot.Skills, overrides, shell,
                snapshot.Branch, snapshot.Notes);
            Session.FrozenPrompt = system;
            Session.FrozenAgent = Session.Agent;
            Session.FrozenTools = fingerprint;
            Session.PromptFacts = snapshot.Facts;
            _pendingFacts = null;
            _s.Store.SaveSession(Session);
        }
        else
        {
            system = Session.FrozenPrompt;
            delta = ContextUpdates.Build(Session.PromptFacts, snapshot);
            // Advanced only after the user message carrying the delta is persisted (see CommitFacts).
            _pendingFacts = delta is null ? null : snapshot.Facts;
        }
        _turnPrompt = (system, tools, delta, Session.Agent);
        return (system, tools, delta);
    }

    /// <summary>Marks the pending context delta as delivered; called after the user message holding it was stored.</summary>
    private void CommitFacts()
    {
        if (_pendingFacts is null) return;
        Session.PromptFacts = _pendingFacts;
        _pendingFacts = null;
        _s.Store.SaveSession(Session);
    }

    private async Task<(PromptSnapshot Snapshot, (string? Replace, string? Append) Overrides, ShellSpec Shell)> PromptSnapshotAsync(
        AgentDefinition agent, ServerConfig config, CancellationToken ct)
    {
        var dir = Workspace.IsFolder ? Workspace.Path : null;
        string? branch = null;
        if (dir is not null)
        {
            try
            {
                var info = await _s.Git.InfoAsync(dir, ct);
                if (info.IsRepo) branch = info.Branch;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Git is optional.
            }
        }
        var skills = _s.Skills.List(dir);
        var active = skills.Where(s => Session.ActiveSkills.Contains(s.Id)).ToList();
        var overrides = _s.ContextFiles.SystemOverrides(dir);
        var shell = ProcessRunner.ResolveShell(config.Shell);
        var snapshot = ContextUpdates.Snapshot(_s.ContextFiles.Load(dir), skills, active, _s.Knowledge.ForPrompt(Workspace.Id, Session.Agent),
            branch, Session.PermissionMode, Session.Language, shell.Name, overrides);
        return (snapshot, overrides, shell);
    }

    // --- streaming -------------------------------------------------------------

    /// <summary>
    /// Runs one model step, repeating it while the provider fails in a way that means "this request never got an answer":
    /// a gateway 499/408/429/5xx, a socket dropped mid-stream, a provider timeout, or a stream that closed without a single
    /// event. Without this, one hiccup of a local runtime ends the conversation on the first failed request; see
    /// <see cref="RetryPolicy"/> for what counts as transient. A retry restarts the step from scratch — keeping what the
    /// failed attempt streamed would splice two generations of the same answer together.
    /// </summary>
    private async Task<(List<ToolCall> Calls, string Finish)> StreamStepAsync(ProviderConfig provider, ModelInfo model, ChatRequest request,
        Message assistant, List<ITool> tools, int estimatedInput, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lastAttempt = attempt >= RetryPolicy.MaxAttempts;
            try
            {
                var (calls, finish, produced) = await StreamOnceAsync(provider, model, request, assistant, tools, estimatedInput, ct);
                // A stream that closed without producing anything is the same failure as a dropped connection: runtimes
                // answer 200 and hang up. The step body already wrote "empty response" on the card; repeat it first.
                if (produced || lastAttempt) return (calls, finish);
                await RetryStepAsync(assistant, "The model returned an empty response.", attempt, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && RetryPolicy.IsTransient(ex))
            {
                // Out of attempts: the error reaches the chat and ends the run, exactly as it did before retries existed.
                if (lastAttempt)
                {
                    ClearRetry(assistant);
                    throw;
                }
                await RetryStepAsync(assistant, RetryPolicy.Reason(ex), attempt, ct);
            }
        }
    }

    /// <summary>
    /// What happens between two attempts of one step: drop what the failed attempt streamed (its tool calls never ran),
    /// show which attempt is starting and why, then wait out the backoff.
    /// </summary>
    private async Task RetryStepAsync(Message assistant, string reason, int attempt, CancellationToken ct)
    {
        var wait = RetryPolicy.Delay(attempt);
        lock (assistant)
        {
            assistant.Text = "";
            assistant.Reasoning = null;
            assistant.ReasoningSignature = null;
            assistant.ToolCalls = null;
            assistant.Error = null;
            assistant.Retry = new RetryNotice { Attempt = attempt + 1, Attempts = RetryPolicy.MaxAttempts, Error = reason };
        }
        _s.Store.UpdateMessage(Session, assistant);
        _s.Log.LogWarning("Step of session {Session} failed ({Reason}); starting attempt {Attempt}/{Attempts} in {Delay}s",
            Session.Id, reason, attempt + 1, RetryPolicy.MaxAttempts, wait.TotalSeconds);
        await Task.Delay(wait, ct);
    }

    /// <summary>Drops a retry notice that has stopped being true (the run gave up on the step).</summary>
    private void ClearRetry(Message assistant)
    {
        lock (assistant)
        {
            if (assistant.Retry is null) return;
            assistant.Retry = null;
        }
        _s.Store.UpdateMessage(Session, assistant);
    }

    private async Task<(List<ToolCall> Calls, string Finish, bool Produced)> StreamOnceAsync(ProviderConfig provider, ModelInfo model, ChatRequest request,
        Message assistant, List<ITool> tools, int estimatedInput, CancellationToken ct)
    {
        var client = _s.Providers.CreateClient(provider, model.ModelId);
        var builders = new SortedDictionary<int, (ToolCall Call, StringBuilder Args)>();
        var stepClock = Stopwatch.StartNew();
        TimeSpan? firstToken = null;
        var approxOut = 0;
        Usage? usage = null;
        double? reportedTps = null;
        var finish = "stop";
        var lastPublish = DateTime.MinValue;

        double LiveTps()
        {
            if (firstToken is null) return 0;
            var seconds = (stepClock.Elapsed - firstToken.Value).TotalSeconds;
            return seconds > 0.2 ? approxOut / seconds : 0;
        }

        await foreach (var ev in client.StreamAsync(request, ct))
        {
            switch (ev)
            {
                case TextDelta t when t.Text.Length > 0:
                    firstToken ??= stepClock.Elapsed;
                    approxOut += Text.EstimateTokens(t.Text);
                    lock (assistant) assistant.Text += t.Text;
                    _s.Store.Delta(Session, assistant, "text", t.Text);
                    break;
                case ReasoningDelta r when r.Text.Length > 0:
                    firstToken ??= stepClock.Elapsed;
                    approxOut += Text.EstimateTokens(r.Text);
                    lock (assistant) assistant.Reasoning = (assistant.Reasoning ?? "") + r.Text;
                    _s.Store.Delta(Session, assistant, "reasoning", r.Text);
                    break;
                case ReasoningSignature sig:
                    lock (assistant) assistant.ReasoningSignature = (assistant.ReasoningSignature ?? "") + sig.Signature;
                    break;
                case ToolCallStarted started:
                    {
                        firstToken ??= stepClock.Elapsed;
                        var call = new ToolCall { Id = started.Id, Name = started.Name, Summary = started.Name };
                        builders[started.Index] = (call, new StringBuilder());
                        lock (assistant) (assistant.ToolCalls ??= []).Add(call);
                        _s.Store.UpdateMessage(Session, assistant, persist: false);
                        break;
                    }
                case ToolCallArgumentsDelta delta:
                    if (builders.TryGetValue(delta.Index, out var builder))
                    {
                        builder.Args.Append(delta.Delta);
                        approxOut += Text.EstimateTokens(delta.Delta);
                    }
                    break;
                case UsageReported u:
                    usage = u.Usage;
                    reportedTps = u.TokensPerSecond;
                    break;
                case StreamFinished f:
                    finish = f.Reason;
                    break;
            }
            if (DateTime.UtcNow - lastPublish > TimeSpan.FromMilliseconds(250))
            {
                lastPublish = DateTime.UtcNow;
                PublishMetrics(LiveTps());
            }
        }

        var byName = tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var calls = new List<ToolCall>();
        var produced = false;
        lock (assistant)
        {
            foreach (var (call, args) in builders.Values)
            {
                call.Arguments = args.ToString();
                try
                {
                    var parsed = Args.Parse(call.Arguments);
                    // Re-serialise so duplicate keys never reach the history replayed to providers.
                    call.Arguments = parsed.ToJsonString();
                    call.Input = parsed.ToJsonString(Json.Indented);
                    if (byName.TryGetValue(call.Name, out var tool))
                    {
                        var summary = tool.Summarize(parsed);
                        call.Summary = string.IsNullOrWhiteSpace(summary) ? call.Name : summary;
                    }
                }
                catch (ToolArgumentException)
                {
                    call.Input = call.Arguments;
                }
                calls.Add(call);
            }

            usage ??= new Usage { Input = estimatedInput, Output = approxOut };
            usage.Cost = model.Cost(usage);
            assistant.Usage = usage;
            assistant.DurationMs = (long)stepClock.Elapsed.TotalMilliseconds;
            produced = !string.IsNullOrWhiteSpace(assistant.Text) || !string.IsNullOrWhiteSpace(assistant.Reasoning) || calls.Count > 0;
            if (!produced) assistant.Error = "The model returned an empty response.";
            // Any attempt that ran to the end closes the retry cycle: `StreamStepAsync` re-sets the notice when this
            // emptiness is worth repeating, so a card never keeps a stale "retrying" line.
            assistant.Retry = null;
        }

        Session.Usage.Add(usage);
        Session.Context = new ContextInfo { Used = (int)Math.Min(int.MaxValue, usage.Input + usage.Output), Limit = model.ContextLength };
        var streamSeconds = firstToken is null ? 0 : (stepClock.Elapsed - firstToken.Value).TotalSeconds;
        var tps = reportedTps ?? (streamSeconds > 0 ? usage.Output / streamSeconds : 0);
        _s.Store.UpdateMessage(Session, assistant);
        _s.Store.SaveSession(Session);
        PublishMetrics(tps);
        return (calls, finish, produced);
    }

    private double _lastTps;

    private void PublishMetrics(double tps)
    {
        if (tps > 0) _lastTps = tps;
        var model = _s.Providers.Resolve(Session.ModelId)?.Model;
        _s.Store.PublishMetrics(Session, new
        {
            durationMs = (long)_clock.Elapsed.TotalMilliseconds,
            tokensPerSec = Math.Round(_lastTps, 1),
            usage = Session.Usage,
            cost = Session.Usage.Cost,
            costAvailable = model is { Local: false } && (model.InputPrice > 0 || model.OutputPrice > 0),
            context = Session.Context,
        });
    }

    // --- tools -------------------------------------------------------------------

    private async Task ExecuteToolsAsync(Message assistant, List<ToolCall> calls, List<ITool> tools, CancellationToken ct)
    {
        var byName = tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var parallel = calls.Count > 1 && calls.All(c => byName.TryGetValue(c.Name, out var t) && t.Kind is ToolKind.Read or ToolKind.Network or ToolKind.Agent);
        if (parallel)
        {
            await Task.WhenAll(calls.Select(c => ExecuteOneAsync(assistant, c, byName, ct)));
            return;
        }
        for (var i = 0; i < calls.Count; i++)
        {
            if (HasSteering)
            {
                // pi semantics: a steering message skips the remaining tool calls of the step.
                foreach (var skipped in calls.Skip(i))
                    Complete(assistant, skipped, ToolResult.Fail("Skipped: the user sent a new message before this tool ran."), TimeSpan.Zero);
                return;
            }
            await ExecuteOneAsync(assistant, calls[i], byName, ct);
        }
    }

    private async Task ExecuteOneAsync(Message assistant, ToolCall call, Dictionary<string, ITool> byName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            if (!byName.TryGetValue(call.Name, out var tool))
            {
                result = ToolResult.Fail($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", byName.Keys)}.");
            }
            else
            {
                var args = Args.Parse(call.Arguments);
                var denial = await AuthorizeAsync(tool, call, args, ct);
                if (denial is not null)
                {
                    result = ToolResult.Fail(denial);
                }
                else
                {
                    var ctx = new ToolContext
                    {
                        Session = Session,
                        Workspace = Workspace,
                        Cwd = Cwd,
                        Message = assistant,
                        Call = call,
                        Config = _s.Db.GetConfig(),
                        Changes = _s.Changes,
                        Host = this,
                    };
                    result = await tool.ExecuteAsync(ctx, args, ct);
                }
            }
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Fail($"{ex.Message} Check the tool's parameters and try again.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Complete(assistant, call, ToolResult.Fail("Aborted."), sw.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        Complete(assistant, call, result, sw.Elapsed);
    }

    private void Complete(Message assistant, ToolCall call, ToolResult result, TimeSpan elapsed)
    {
        var max = _s.Db.GetConfig().MaxToolOutput;
        lock (assistant)
        {
            call.Status = result.IsError ? "error" : "success";
            var display = Text.Truncate(result.Output, 20_000);
            if (result.IsError) call.Error = display;
            else call.Output = display;
            call.Result = Text.Truncate(result.Output, max);
            call.Duration = elapsed == TimeSpan.Zero ? "—" : Text.Duration(elapsed);
            if (result.Images is { Count: > 0 }) call.Images = result.Images;
            call.Completed = true;
        }
        _s.Store.UpdateMessage(Session, assistant);
    }

    public void ReportProgress(ToolCall call, string chunk)
    {
        var message = _current;
        if (message is null) return;
        lock (message)
        {
            var output = (call.Output ?? "") + chunk;
            call.Output = output.Length > 20_000 ? output[^20_000..] : output;
        }
        if (DateTime.UtcNow - _lastProgressPublish < TimeSpan.FromMilliseconds(300)) return;
        _lastProgressPublish = DateTime.UtcNow;
        _s.Store.UpdateMessage(Session, message, persist: false);
    }

    // --- permissions ------------------------------------------------------------

    private async Task<string?> AuthorizeAsync(ITool tool, ToolCall call, JsonObject args, CancellationToken ct)
    {
        if (tool.Kind is ToolKind.Read or ToolKind.Network or ToolKind.Interactive or ToolKind.Agent) return null;
        var mode = Session.Agent == "plan" ? "plan" : Session.PermissionMode;
        if (mode == "full") return null;
        if (mode is "read-only" or "plan" && tool.Kind is ToolKind.Write or ToolKind.Execute)
            return $"Blocked: the session is in '{mode}' mode, which does not allow `{tool.Name}`. Describe the change instead, or ask the user to switch the permission mode.";
        return await Root.RequestPermissionAsync(tool, call, args, ct);
    }

    private async Task<string?> RequestPermissionAsync(ITool tool, ToolCall call, JsonObject args, CancellationToken ct)
    {
        lock (Session.AllowedTools)
        {
            if (Session.AllowedTools.Contains(tool.Name)) return null;
        }
        var requestId = Ids.New("per");
        var card = _s.Store.AddMessage(Session, new Message
        {
            Role = "assistant",
            Synthetic = true,
            Model = ModelLabel(),
            Text = "I need your approval before I continue.",
            Permission = new PermissionCard
            {
                Tool = tool.Name,
                Description = DescribeCall(tool, call, args),
                Choices = [.. PermissionChoices],
                RequestId = requestId,
            },
        });
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissions[requestId] = (tcs, card);
        RefreshStatus();
        string decision;
        try
        {
            await using (ct.Register(() => tcs.TrySetCanceled()))
                decision = await tcs.Task;
        }
        finally
        {
            _permissions.TryRemove(requestId, out _);
            if (SettleCard(card)) _s.Store.UpdateMessage(Session, card);
            RefreshStatus();
        }
        return decision == "reject" ? $"The user denied permission to run `{tool.Name}`. Do not retry it; ask how to proceed or choose another approach." : null;
    }

    private static string DescribeCall(ITool tool, ToolCall call, JsonObject args) => tool.Kind switch
    {
        ToolKind.Execute => $"Run: {Args.Optional(args, "command", "cmd")}",
        ToolKind.Write when tool.Name == "edit" => $"Edit {call.Summary}",
        ToolKind.Write => $"Write {call.Summary}",
        ToolKind.Browser => $"Browser: {call.Summary}",
        _ => $"{tool.Name} {Text.FirstLine(args.ToJsonString(), 200)}",
    };

    public bool ReplyPermission(string requestId, string decision)
    {
        if (!_permissions.TryGetValue(requestId, out var pending)) return false;
        var card = pending.Card;
        lock (card)
        {
            if (card.Permission is { } p)
                p.Resolved = decision == "always" ? PermissionChoices[1] : decision == "reject" ? PermissionChoices[2] : PermissionChoices[0];
        }
        _s.Store.UpdateMessage(Session, card);
        if (decision == "always" && card.Permission is { } perm)
        {
            lock (Session.AllowedTools)
            {
                if (!Session.AllowedTools.Contains(perm.Tool)) Session.AllowedTools.Add(perm.Tool);
            }
            _s.Store.SaveSession(Session, publish: false);
        }
        pending.Tcs.TrySetResult(decision);
        return true;
    }

    /// <summary>Marks an unanswered permission/question card as expired. Returns true when it changed.</summary>
    public static bool SettleCard(Message card)
    {
        var changed = false;
        lock (card)
        {
            if (card.Permission is { Resolved: null } p)
            {
                p.Resolved = "Expired";
                changed = true;
            }
            if (card.Question is { Resolved: null } q)
            {
                q.Resolved = "Expired";
                changed = true;
            }
            if (card.QuestionSet is { Confirmed: not true } qs)
            {
                qs.Confirmed = true;
                changed = true;
            }
        }
        return changed;
    }

    private void RefreshStatus()
    {
        lock (_gate)
        {
            if (_finished) return;
        }
        var status = !_permissions.IsEmpty ? SessionStatus.Permission : !_questions.IsEmpty ? SessionStatus.Question : SessionStatus.Working;
        if (Session.Status != status) _s.Store.SetStatus(Session, status);
    }

    // --- questions --------------------------------------------------------------

    public async Task<List<string>> AskAsync(ToolContext ctx, List<QuestionRequest> questions, CancellationToken ct)
    {
        var formId = Ids.New("frm");
        // The card rides on the assistant message that produced the call, so the question reads as part of
        // that reply; a second question in the same step gets its own synthetic card message instead.
        var host = ctx.Message;
        bool taken;
        lock (host) taken = host.Question is not null || host.QuestionSet is not null;
        var card = taken
            ? _s.Store.AddMessage(Session, new Message { Role = "assistant", Synthetic = true, Model = ModelLabel() })
            : host;

        lock (card)
        {
            if (questions.Count == 1)
            {
                var q = questions[0];
                card.Question = new QuestionCard { Prompt = q.Prompt, Options = q.Options, FormId = formId, FieldKey = "q0", Multi = q.Multi };
            }
            else
            {
                card.QuestionSet = new QuestionSetCard
                {
                    FormId = formId,
                    Answers = [],
                    Questions = questions.Select((q, i) => new QuestionItem { Prompt = q.Prompt, Options = q.Options, FieldKey = $"q{i}", Multi = q.Multi }).ToList(),
                };
            }
        }
        _s.Store.UpdateMessage(Session, card);
        var tcs = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[formId] = (tcs, card);
        RefreshStatus();
        try
        {
            await using (ct.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        finally
        {
            _questions.TryRemove(formId, out _);
            if (SettleCard(card)) _s.Store.UpdateMessage(Session, card);
            RefreshStatus();
        }
    }

    public bool Answer(string formId, List<string?> answers)
    {
        if (!_questions.TryGetValue(formId, out var pending)) return false;
        var card = pending.Card;
        lock (card)
        {
            if (card.Question is { } q) q.Resolved = answers.FirstOrDefault() ?? "";
            if (card.QuestionSet is { } qs)
            {
                qs.Answers = answers;
                qs.Confirmed = true;
            }
        }
        _s.Store.UpdateMessage(Session, card);
        pending.Tcs.TrySetResult(answers.Select(a => a ?? "").ToList());
        return true;
    }

    /// <summary>A message typed while the agent waits on questions answers them all.</summary>
    public bool AnswerPendingWithText(string text)
    {
        var any = false;
        foreach (var (formId, pending) in _questions.ToList())
        {
            var count = pending.Card.QuestionSet?.Questions.Count ?? 1;
            any |= Answer(formId, Enumerable.Repeat<string?>(text, count).ToList());
        }
        return any;
    }

    // --- subagents ----------------------------------------------------------------

    public async Task<string> RunSubagentAsync(ToolContext ctx, string agent, string description, string prompt, CancellationToken ct)
    {
        var child = _s.Store.CreateSession(Workspace, description, template: Session, parentId: Session.Id, agent: agent);
        lock (ctx.Message) ctx.Call.ChildSessionId = child.Id;
        _s.Store.UpdateMessage(Session, ctx.Message, persist: false);

        var run = _agents.CreateChildRun(child, Workspace, Root, ct);
        run.TryEnqueue(new PendingPrompt(Ids.New("msg"), "prompt", prompt, null, []) { Announce = false }, steer: false);
        await run.RunAsync();
        ct.ThrowIfCancellationRequested();
        if (run.LastError is not null) throw new InvalidOperationException($"Subagent failed: {run.LastError}");
        var report = _s.Db.SessionMessages(child.Id).LastOrDefault(m => m.Role == "assistant" && !m.Synthetic && !string.IsNullOrWhiteSpace(m.Text))?.Text;
        if (child.Status == SessionStatus.Idle && report is null && run.WasAborted)
            throw new InvalidOperationException("The subagent was stopped by the user.");
        return report ?? "(The subagent finished without a report.)";
    }

    public bool WasAborted => _cts.IsCancellationRequested;

    // --- compaction ----------------------------------------------------------------

    /// <summary>
    /// Compacts the history and reports progress and outcome as a system notice in the chat.
    /// Returns true when the history was actually replaced by a summary.
    /// </summary>
    private async Task<bool> CompactAsync(bool auto, CancellationToken ct)
    {
        var live = _s.Db.SessionMessages(Session.Id).Where(m => !m.Compacted && !m.Synthetic).ToList();
        var before = Math.Max(Session.Context.Used, live.Sum(Estimate));
        var notice = new CompactionNotice { Status = "running", Auto = auto, Before = before };
        var card = _s.Store.AddMessage(Session, new Message { Role = "assistant", Synthetic = true, Compaction = notice });
        void Finish(string status, int? after = null, string? error = null, string? result = null)
        {
            lock (card)
            {
                card.Compaction = new CompactionNotice { Status = status, Auto = auto, Before = before, After = after, Error = error, Result = result };
            }
            _s.Store.UpdateMessage(Session, card);
        }

        try
        {
            var done = await CompactCoreAsync(auto, ct);
            if (done is null) { Finish("skipped"); return false; }
            Finish("done", done.Value.After, result: done.Value.Summary);
            return true;
        }
        catch (OperationCanceledException)
        {
            Finish("failed", error: "Aborted.");
            throw;
        }
        catch (Exception ex)
        {
            Finish("failed", error: ex.Message);
            throw;
        }
    }

    /// <summary>Returns the context size after compaction, or null when there was nothing to compact.</summary>
    private async Task<(int After, string Summary)?> CompactCoreAsync(bool auto, CancellationToken ct)
    {
        var (provider, model) = ResolveModel();
        var messages = _s.Db.SessionMessages(Session.Id).Where(m => !m.Compacted && !m.Synthetic).OrderBy(m => m.Seq).ToList();
        var previous = messages.Where(m => m.Summary == true).ToList();
        var conversation = messages.Where(m => m.Summary != true).ToList();
        if (conversation.Count < 2) return null;

        // Keep the newest messages that fit in ~30% of the window when compacting automatically.
        var cut = conversation.Count;
        if (auto)
        {
            var budget = (int)(model.ContextLength * 0.3);
            var kept = 0;
            cut = conversation.Count;
            for (var i = conversation.Count - 1; i > 0; i--)
            {
                kept += Estimate(conversation[i]);
                if (kept > budget) break;
                cut = i;
            }
            if (cut >= conversation.Count) cut = conversation.Count - 1;
        }
        var prefix = conversation.Take(cut).ToList();
        if (prefix.Count == 0) return null;

        var transcript = new StringBuilder();
        foreach (var s in previous) transcript.AppendLine("[Earlier summary]").AppendLine(s.Text).AppendLine();
        foreach (var m in prefix)
        {
            transcript.Append('[').Append(m.Role).AppendLine("]");
            if (!string.IsNullOrWhiteSpace(m.Expanded ?? m.Text)) transcript.AppendLine(m.Expanded ?? m.Text);
            foreach (var call in m.ToolCalls ?? [])
            {
                transcript.AppendLine($"(tool {call.Name} {Text.Truncate(call.Arguments, 600)})");
                transcript.AppendLine($"→ {Text.Truncate(call.Result ?? call.Output ?? call.Error ?? "", 1500)}");
            }
            transcript.AppendLine();
        }

        var maxInput = Math.Max(4000, (int)(model.ContextLength * 0.6) * 4);
        var request = new ChatRequest
        {
            Model = model.ModelId,
            System = "You summarize coding-agent conversations so the work can continue in a fresh context. Be factual and specific.",
            Messages =
            [
                ChatMessage.UserText(
                    "Summarize the conversation below. Include: the user's goals and constraints, key decisions, files read or changed (with paths), " +
                    "commands run and their outcomes, the current state, and the next steps. Use concise Markdown sections.\n\n<transcript>\n" +
                    Text.Truncate(transcript.ToString(), maxInput) + "\n</transcript>"),
            ],
            Reasoning = "off",
            MaxTokens = Math.Min(4096, model.MaxOutput),
            ModelSupportsReasoning = model.Reasoning,
            SessionId = Session.Id,
        };

        _s.Store.SetStatus(Session, SessionStatus.Working);
        // Thinking models can spend the whole output budget on reasoning (or ignore "off") and emit no text at all.
        // Retry once with a bigger budget, then fall back to whatever reasoning text was produced.
        var summary = new StringBuilder();
        var reasoning = new StringBuilder();
        for (var attempt = 0; attempt < 2 && summary.Length == 0; attempt++)
        {
            if (attempt == 1) request = new ChatRequest
            {
                Model = request.Model,
                System = request.System,
                Messages = request.Messages,
                Reasoning = "off",
                MaxTokens = Math.Max(request.MaxTokens, Math.Min(model.MaxOutput, 16384)),
                ModelSupportsReasoning = request.ModelSupportsReasoning,
                SessionId = request.SessionId,
            };
            reasoning.Clear();
            await foreach (var ev in _s.Providers.CreateClient(provider, model.ModelId).StreamAsync(request, ct))
            {
                if (ev is TextDelta t) summary.Append(t.Text);
                else if (ev is ReasoningDelta r) reasoning.Append(r.Text);
            }
            if (summary.ToString().Trim().Length == 0) summary.Clear();
        }
        if (summary.Length == 0 && reasoning.ToString().Trim().Length > 0) summary.Append(reasoning);
        if (summary.Length == 0)
            throw new InvalidOperationException($"Compaction failed: the model ({model.Label}) returned an empty summary even after a retry.");

        foreach (var m in prefix.Concat(previous))
        {
            m.Compacted = true;
            _s.Db.Messages.Update(m);
        }
        _s.Store.AddMessage(Session, new Message
        {
            Role = "assistant",
            Summary = true,
            Model = model.Label,
            Text = summary.ToString().Trim(),
        });
        var keptTokens = conversation.Skip(cut).Sum(Estimate) + Text.EstimateTokens(summary.ToString());
        Session.Context = new ContextInfo { Used = keptTokens, Limit = model.ContextLength };
        _s.Store.SaveSession(Session);
        PublishMetrics(0);
        return (keptTokens, summary.ToString().Trim());
    }

    private static int Estimate(Message m) =>
        Text.EstimateTokens(m.Expanded ?? m.Text) + Text.EstimateTokens(m.Reasoning)
        + (m.ToolCalls ?? []).Sum(c => Text.EstimateTokens(c.Arguments) + Text.EstimateTokens(c.Result ?? c.Output ?? c.Error));
}
