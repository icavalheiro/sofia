namespace Sofia.Server.Agent;

/// <summary>
/// Serialises access to "single client" models (a local runtime or home server that serves one conversation at a time and
/// re-computes its context when it switches). A model is leased to one root conversation (the session plus its subagents)
/// until that conversation's run ends; other conversations wait, subagents before sessions, first come first served.
/// </summary>
public sealed class ModelGate
{
    private sealed class Waiter(string owner, bool subagent, long seq)
    {
        public string Owner { get; } = owner;
        public bool Subagent { get; } = subagent;
        public long Seq { get; } = seq;
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class State
    {
        public string? Owner;
        public readonly List<Waiter> Waiting = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _models = [];
    private long _seq;

    /// <summary>Waits until <paramref name="owner"/> (root session id) holds the model. Re-entrant for the current owner.</summary>
    public async Task AcquireAsync(string modelId, string owner, bool subagent, CancellationToken ct, Action? onWait = null)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (!_models.TryGetValue(modelId, out var state)) _models[modelId] = state = new State();
            if (state.Owner is null || state.Owner == owner)
            {
                state.Owner = owner;
                return;
            }
            waiter = new Waiter(owner, subagent, ++_seq);
            state.Waiting.Add(waiter);
        }

        onWait?.Invoke();
        try
        {
            await using var _ = ct.Register(() => waiter.Tcs.TrySetCanceled(ct));
            await waiter.Tcs.Task;
        }
        catch
        {
            lock (_gate)
            {
                if (_models.TryGetValue(modelId, out var state)) state.Waiting.Remove(waiter);
            }
            throw;
        }
    }

    /// <summary>Releases every model leased to <paramref name="owner"/> and hands each to the next waiter.</summary>
    public void Release(string owner)
    {
        lock (_gate)
        {
            foreach (var state in _models.Values.Where(s => s.Owner == owner)) Handoff(state);
        }
    }

    /// <summary>Number of conversations waiting for the model (for diagnostics).</summary>
    public int Waiting(string modelId)
    {
        lock (_gate) return _models.TryGetValue(modelId, out var s) ? s.Waiting.Select(w => w.Owner).Distinct().Count() : 0;
    }

    private static void Handoff(State state)
    {
        // Cancelled waiters are skipped.
        state.Waiting.RemoveAll(w => w.Tcs.Task.IsCompleted);
        var next = state.Waiting.OrderBy(w => w.Subagent ? 0 : 1).ThenBy(w => w.Seq).FirstOrDefault();
        if (next is null)
        {
            state.Owner = null;
            return;
        }
        state.Owner = next.Owner;
        // Every pending request of the new owner proceeds together.
        foreach (var w in state.Waiting.Where(w => w.Owner == next.Owner).ToList())
        {
            state.Waiting.Remove(w);
            w.Tcs.TrySetResult();
        }
    }
}
