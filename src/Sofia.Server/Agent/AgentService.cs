using System.Collections.Concurrent;
using System.Text.Json;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Providers;

namespace Sofia.Server.Agent;

public sealed record PromptInput(
    string Text,
    List<string>? Attachments = null,
    string? Delivery = null,
    string? ModelId = null,
    string? Agent = null,
    string? PermissionMode = null,
    string? ReasoningMode = null,
    string? Language = null);

public sealed class ConflictException(string message) : Exception(message);

public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Owns the active runs: prompts, steering, abort, approvals, answers, compaction, revert.</summary>
public sealed class AgentService(RunServices services, CommandService commands, IHostApplicationLifetime lifetime, KeepAwakeService awake, ILogger<AgentService> log)
{
    private static readonly HashSet<string> PermissionModes = ["read-only", "ask", "plan", "full"];
    private static readonly HashSet<string> ReasoningModes = ["off", "low", "medium", "high"];

    private readonly ConcurrentDictionary<string, SessionRun> _runs = new();
    private readonly ConcurrentDictionary<string, byte> _reviewing = new();
    private readonly object _gate = new();

    private SofiaDb Db => services.Db;
    private SessionStore Store => services.Store;

    public bool IsActive(string sessionId) => _runs.ContainsKey(sessionId);

    public (Session Session, Workspace Workspace) Load(string sessionId)
    {
        var session = Db.Sessions.FindById(sessionId) ?? throw new NotFoundException("Session not found.");
        var workspace = Db.Workspaces.FindById(session.WorkspaceId) ?? throw new NotFoundException("Workspace not found.");
        // Active runs own the live session object; always hand that one out while running.
        if (_runs.TryGetValue(sessionId, out var run)) session = run.Session;
        return (session, workspace);
    }

    /// <summary>Applies per-prompt overrides (model, agent, modes) to the session.</summary>
    public Session ApplySettings(Session session, string? modelId, string? agent, string? permissionMode, string? reasoningMode, string? title = null)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(modelId) && modelId != session.ModelId)
        {
            if (services.Providers.Resolve(modelId) is not { } resolved) throw new ArgumentException($"Unknown model '{modelId}'.");
            if (Db.GetConfig().DisabledModels.Contains(modelId)) throw new ArgumentException($"Model '{modelId}' is disabled in Settings.");
            session.ModelId = modelId;
            session.Context.Limit = resolved.Model.ContextLength;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(agent) && agent != session.Agent)
        {
            var workspaceDir = Db.Workspaces.FindById(session.WorkspaceId) is { IsFolder: true } ws ? ws.Path : null;
            if (services.AgentCatalog.Find(agent, workspaceDir) is null) throw new ArgumentException($"Unknown agent '{agent}'.");
            session.Agent = agent;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(permissionMode) && permissionMode != session.PermissionMode)
        {
            if (!PermissionModes.Contains(permissionMode)) throw new ArgumentException($"Unknown permission mode '{permissionMode}'.");
            session.PermissionMode = permissionMode;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(reasoningMode) && reasoningMode != session.ReasoningMode)
        {
            if (!ReasoningModes.Contains(reasoningMode)) throw new ArgumentException($"Unknown reasoning mode '{reasoningMode}'.");
            session.ReasoningMode = reasoningMode;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(title) && title.Trim() != session.Title)
        {
            session.Title = title.Trim();
            changed = true;
        }
        if (changed) Store.SaveSession(session);
        return session;
    }

    public string Prompt(string sessionId, PromptInput input, string? expanded = null)
    {
        var (session, workspace) = Load(sessionId);
        ApplySettings(session, input.ModelId, input.Agent, input.PermissionMode, input.ReasoningMode);
        if (!string.IsNullOrWhiteSpace(input.Language) && input.Language.Trim() != session.Language)
        {
            session.Language = input.Language.Trim();
            Store.SaveSession(session);
        }
        var attachments = (input.Attachments ?? [])
            .Select(id => Db.Attachments.FindById(id) ?? throw new ArgumentException($"Unknown attachment '{id}'."))
            .ToList();
        if (string.IsNullOrWhiteSpace(input.Text) && attachments.Count == 0) throw new ArgumentException("The prompt is empty.");

        var prompt = new PendingPrompt(Ids.New("msg"), "prompt", input.Text.Trim(), expanded ?? ExpandMentions(session, workspace, input.Text), attachments);
        var steer = input.Delivery == "steer";
        lock (_gate)
        {
            if (_runs.TryGetValue(sessionId, out var active))
            {
                // A message typed while the agent waits on a question answers it.
                if (input.Delivery != "queue" && active.HasPendingQuestion && attachments.Count == 0)
                {
                    Store.AddMessage(active.Session, new Message { Id = prompt.MessageId, Role = "user", Text = prompt.Text, Synthetic = true });
                    active.AnswerPendingWithText(prompt.Text);
                    return prompt.MessageId;
                }
                if (active.TryEnqueue(prompt, steer)) return prompt.MessageId;
            }
            StartRun(session, workspace, prompt);
        }
        return prompt.MessageId;
    }

    private void StartRun(Session session, Workspace workspace, PendingPrompt first)
    {
        var run = new SessionRun(services, this, session, workspace, null, lifetime.ApplicationStopping);
        run.TryEnqueue(first with { Announce = false }, steer: false);
        _runs[session.Id] = run;
        awake.SetBusy(true);
        run.Start();
    }

    /// <summary>
    /// `@agent` mentions: the user sees their text as typed, the model also gets a note to hand the work to the
    /// mentioned subagents with the `task` tool. Only possible when the active agent can use `task`.
    /// </summary>
    private string? ExpandMentions(Session session, Workspace workspace, string text)
    {
        var dir = workspace.IsFolder ? workspace.Path : null;
        var catalog = services.AgentCatalog;
        var active = catalog.Get(session.Agent, dir);
        if (!active.HasTaskTool) return null;
        var mentioned = catalog.Mentions(text, dir).Where(a => a.ReadOnly || active.Tools.Contains("task")).ToList();
        if (mentioned.Count == 0) return null;
        return $"{text.Trim()}\n\n<agent_mentions>\n{AgentCatalog.DelegationHint(mentioned.Select(a => a.Id))}\n</agent_mentions>";
    }

    public async Task<string> CommandAsync(string sessionId, string name, string? arguments, CancellationToken ct = default)
    {
        var (session, workspace) = Load(sessionId);
        if (name == "compact")
        {
            Compact(sessionId);
            return "";
        }
        var dir = workspace.IsFolder ? workspace.Path : null;
        var command = commands.List(dir).FirstOrDefault(c => c.Name == name)
            ?? throw new NotFoundException($"Unknown command '/{name}'.");
        var catalog = services.AgentCatalog;
        var target = command.Agent is { } wanted
            ? catalog.Find(wanted, dir) ?? throw new ArgumentException($"Command '/{name}' uses unknown agent '{wanted}'.")
            : null;
        var args = arguments?.Trim() ?? "";
        var display = $"/{name}{(args.Length > 0 ? " " + args : "")}";

        // Shell injection (!`cmd`) only runs without asking in full-access sessions.
        var text = await CommandService.ExpandDynamicAsync(CommandService.Expand(command.Template, args), dir,
            session.PermissionMode == "full", ProcessRunner.ResolveShell(Db.GetConfig().Shell), ct);

        // A subtask (or a subagent-only agent) is delegated through `task`; any other agent takes over the session.
        string? delegateTo = null;
        if (command.Subtask || target?.Mode == "subagent")
            delegateTo = target is null ? "general" : target.IsDelegable ? target.Id : null;
        var switchTo = target is not null && delegateTo is null ? target.Id : null;
        if (delegateTo is not null && catalog.Get(switchTo ?? session.Agent, dir).HasTaskTool)
            text = $"Run the task below as a subtask: call the `task` tool with agent \"{delegateTo}\", passing the task as its prompt with every detail it needs, then report the result to the user.\n\n<task>\n{text}\n</task>";
        return Prompt(session.Id, new PromptInput(display, Agent: switchTo, ModelId: command.Model), text);
    }

    internal SessionRun CreateChildRun(Session child, Workspace workspace, SessionRun root, CancellationToken parent)
    {
        var run = new SessionRun(services, this, child, workspace, root, parent);
        _runs[child.Id] = run;
        return run;
    }

    internal void Finished(SessionRun run)
    {
        lock (_gate)
        {
            _runs.TryRemove(new KeyValuePair<string, SessionRun>(run.Session.Id, run));
            awake.SetBusy(!_runs.IsEmpty);
        }
        // Only a top-level run that ended normally is worth learning from; subagents report to their parent.
        if (run.Root == run && run.Session.ParentId is null && run.LastError is null && !run.WasAborted)
            _ = ReviewAsync(run.Session.Id, run.Workspace);
    }

    /// <summary>Background knowledge review: never blocks or fails the conversation it learns from.</summary>
    private async Task ReviewAsync(string sessionId, Workspace workspace)
    {
        if (!Db.GetConfig().Knowledge || !_reviewing.TryAdd(sessionId, 0)) return;
        var ct = lifetime.ApplicationStopping;
        try
        {
            var session = Db.Sessions.FindById(sessionId);
            if (session is null) return;
            var (drafts, upTo) = await services.Knowledge.ExtractAsync(session, workspace, ct);
            // A newer run may own the live session object by now; always write through that one.
            var live = Db.Sessions.FindById(sessionId) is null ? null : Load(sessionId).Session;
            if (live is null) return;
            live.KnowledgeSeq = Math.Max(live.KnowledgeSeq, upTo);
            if (drafts.Count > 0) services.Knowledge.Commit(live, workspace, drafts);
            else Store.SaveSession(live, publish: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Knowledge review of session {Session} failed", sessionId);
        }
        finally
        {
            _reviewing.TryRemove(sessionId, out _);
        }
    }

    public void Abort(string sessionId)
    {
        if (_runs.TryGetValue(sessionId, out var run)) run.Abort();
    }

    public void ReplyPermission(string sessionId, string requestId, string decision)
    {
        if (decision is not ("once" or "always" or "reject")) throw new ArgumentException("decision must be once, always or reject.");
        if (!_runs.TryGetValue(sessionId, out var run) || !run.ReplyPermission(requestId, decision))
            throw new ConflictException("This permission request is no longer pending.");
    }

    public void Answer(string sessionId, string formId, List<JsonElement?> answers)
    {
        var values = answers.Select(a => a switch
        {
            null => null,
            { ValueKind: JsonValueKind.Array } arr => string.Join(", ", arr.EnumerateArray().Select(x => x.ToString())),
            { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            { } v => v.ToString(),
        }).ToList();
        if (!_runs.TryGetValue(sessionId, out var run) || !run.Answer(formId, values))
            throw new ConflictException("This question is no longer pending.");
    }

    public void Compact(string sessionId)
    {
        var (session, workspace) = Load(sessionId);
        lock (_gate)
        {
            if (_runs.TryGetValue(sessionId, out var active) && active.TryEnqueue(PendingPrompt.Compact(), steer: false)) return;
            StartRun(session, workspace, PendingPrompt.Compact());
        }
    }

    public (List<string> Removed, List<string> Restored) Revert(string sessionId, string messageId)
    {
        var (session, _) = Load(sessionId);
        if (IsActive(sessionId)) throw new ConflictException("Stop the running reply before reverting.");
        var target = Db.Messages.FindById(messageId);
        if (target is null || target.SessionId != sessionId) throw new NotFoundException("Message not found in this session.");
        var removed = Db.SessionMessages(sessionId).Where(m => m.Seq >= target.Seq).Select(m => m.Id).ToList();
        var restored = services.Changes.Revert(sessionId, target.Seq);
        Store.DeleteMessages(session, removed);
        session.Context.Used = 0;
        Store.SaveSession(session);
        return (removed, restored);
    }

    public Session Branch(string sessionId, string messageId)
    {
        var (session, workspace) = Load(sessionId);
        if (IsActive(sessionId)) throw new ConflictException("Stop the running reply before branching.");
        var target = Db.Messages.FindById(messageId);
        if (target is null || target.SessionId != sessionId) throw new NotFoundException("Message not found in this session.");
        return Store.BranchSession(session, workspace, target);
    }

    public Dictionary<string, string> Statuses()
    {
        var result = new Dictionary<string, string>();
        foreach (var run in _runs.Values) result[run.Session.Id] = run.Session.Status;
        foreach (var s in Db.Sessions.Find(s => s.Status == SessionStatus.Error)) result.TryAdd(s.Id, s.Status);
        return result;
    }
}

/// <summary>Event-driven title: the first user message names a fresh conversation.</summary>
public sealed class TitleHandler(AgentService agents, SessionStore store) : IEventHandler
{
    public Task HandleAsync(SofiaEvent e, CancellationToken ct)
    {
        if (e.Type != EventTypes.MessageCreated || e.SessionId is null) return Task.CompletedTask;
        if (!e.Data.TryGetProperty("role", out var role) || role.GetString() != "user") return Task.CompletedTask;
        Session session;
        try
        {
            // While a run is active this is the run's live object, so its later saves keep the title.
            session = agents.Load(e.SessionId).Session;
        }
        catch (NotFoundException)
        {
            return Task.CompletedTask;
        }
        if (session.Title != Session.Defaults.Title) return Task.CompletedTask;
        var text = e.Data.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var title = Text.FirstLine(text, 60).TrimEnd('…').Trim();
        if (title.Length == 0) return Task.CompletedTask;
        session.Title = title;
        store.SaveSession(session);
        return Task.CompletedTask;
    }
}
