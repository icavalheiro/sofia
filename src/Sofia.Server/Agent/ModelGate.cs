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

    private sealed class SlotWaiter(string session, long seq)
    {
        public string Session { get; } = session;
        public long Seq { get; } = seq;
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SlotState
    {
        public bool Busy;
        public string? Last;
        public readonly List<SlotWaiter> Waiting = [];
    }

    private readonly Dictionary<string, SlotState> _slots = [];

    /// <summary>
    /// Request-level slot: a single-client runtime has one decoding slot, so even the session and subagents of one root
    /// conversation must stream one at a time (interleaving them makes the server re-process the context every switch).
    /// The session that streamed last goes first when it asks again, so a conversation keeps its warm context.
    /// Dispose the returned lease when the stream ends.
    /// </summary>
    public async Task<IDisposable> AcquireSlotAsync(string modelId, string session, CancellationToken ct, Action? onWait = null)
    {
        SlotWaiter waiter;
        lock (_gate)
        {
            if (!_slots.TryGetValue(modelId, out var state)) _slots[modelId] = state = new SlotState();
            if (!state.Busy)
            {
                state.Busy = true;
                state.Last = session;
                return new SlotLease(this, modelId);
            }
            waiter = new SlotWaiter(session, ++_seq);
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
                if (_slots.TryGetValue(modelId, out var state))
                {
                    state.Waiting.Remove(waiter);
                    // Granted at the same moment it was cancelled: pass the slot on.
                    if (waiter.Tcs.Task.IsCompletedSuccessfully) NextSlot(state);
                }
            }
            throw;
        }
        return new SlotLease(this, modelId);
    }

    private void ReleaseSlot(string modelId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(modelId, out var state)) NextSlot(state);
        }
    }

    private static void NextSlot(SlotState state)
    {
        state.Waiting.RemoveAll(w => w.Tcs.Task.IsCompleted);
        var next = state.Waiting.OrderBy(w => w.Session == state.Last ? 0 : 1).ThenBy(w => w.Seq).FirstOrDefault();
        if (next is null)
        {
            state.Busy = false;
            return;
        }
        state.Waiting.Remove(next);
        state.Last = next.Session;
        state.Busy = true;
        next.Tcs.TrySetResult();
    }

    private sealed class SlotLease(ModelGate gate, string modelId) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.ReleaseSlot(modelId);
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
