using System.Diagnostics;
using Sofia.Server.Agent;
using Sofia.Server.Events;

namespace Sofia.Server.Infrastructure;

public sealed record BackgroundProcessInfo(
    string Id, string Command, string? Cwd, string? SessionId, int? Pid, string Status, int? ExitCode,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>Tracks long-running commands started in the background (dev servers, watchers) so they can be listed, read and killed.</summary>
public sealed class ProcessManager(ILogger<ProcessManager> log) : IHostedService
{
    private const int MaxLines = 500;
    private const int MaxKept = 50;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly object _gate = new();
    private int _next;

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Command { get; init; }
        public string? Cwd { get; init; }
        public string? SessionId { get; init; }
        public required Process Process { get; init; }
        public int? Pid { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public int? ExitCode { get; set; }
        public bool Killed { get; set; }
        public readonly LinkedList<string> Lines = new();
    }

    public BackgroundProcessInfo Start(ShellSpec shell, string command, string? cwd, string? sessionId)
    {
        var psi = ProcessRunner.StartInfo(shell.Executable, shell.Arguments(command), cwd);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var entry = new Entry
        {
            Id = "bg_" + Interlocked.Increment(ref _next), Command = command, Cwd = cwd, SessionId = sessionId, Process = process,
        };
        void Append(string? line)
        {
            if (line is null) return;
            lock (_gate)
            {
                entry.Lines.AddLast(line);
                if (entry.Lines.Count > MaxLines) entry.Lines.RemoveFirst();
            }
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                entry.EndedAt = DateTimeOffset.UtcNow;
                try { entry.ExitCode = process.ExitCode; } catch { /* disposed */ }
            }
        };
        process.Start();
        entry.Pid = process.Id;
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate)
        {
            _entries[entry.Id] = entry;
            Prune();
            return ToInfo(entry);
        }
    }

    public List<BackgroundProcessInfo> List(string? sessionId = null)
    {
        lock (_gate)
            return _entries.Values.Where(e => sessionId is null || e.SessionId == sessionId)
                .OrderBy(e => e.StartedAt).Select(ToInfo).ToList();
    }

    public (BackgroundProcessInfo Info, string Output) Output(string id, int tail = 100)
    {
        lock (_gate)
        {
            var e = Find(id);
            var lines = e.Lines.Skip(Math.Max(0, e.Lines.Count - Math.Max(1, tail)));
            return (ToInfo(e), string.Join('\n', lines));
        }
    }

    public BackgroundProcessInfo Kill(string id)
    {
        Entry e;
        lock (_gate)
        {
            e = Find(id);
            e.Killed = true;
        }
        ProcessRunner.Kill(e.Process);
        try { e.Process.WaitForExit(2000); } catch { /* gone */ }
        lock (_gate)
        {
            e.EndedAt ??= DateTimeOffset.UtcNow;
            return ToInfo(e);
        }
    }

    /// <summary>Kills every running process of a conversation (used when it is deleted).</summary>
    public void KillSession(string sessionId)
    {
        foreach (var p in List(sessionId).Where(p => p.Status == "running")) Kill(p.Id);
    }

    private Entry Find(string id) =>
        _entries.TryGetValue(id, out var e) ? e : throw new NotFoundException($"Background process '{id}' not found.");

    private static BackgroundProcessInfo ToInfo(Entry e)
    {
        var running = e.EndedAt is null && !SafeExited(e.Process);
        var status = running ? "running" : e.Killed ? "killed" : "exited";
        return new BackgroundProcessInfo(e.Id, e.Command, e.Cwd, e.SessionId, e.Pid, status, running ? null : e.ExitCode, e.StartedAt, e.EndedAt);
    }

    private static bool SafeExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private void Prune()
    {
        var finished = _entries.Values.Where(e => e.EndedAt is not null).OrderBy(e => e.EndedAt).ToList();
        foreach (var e in finished.Take(Math.Max(0, finished.Count - MaxKept)))
        {
            _entries.Remove(e.Id);
            e.Process.Dispose();
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct)
    {
        List<Entry> all;
        lock (_gate) all = _entries.Values.ToList();
        foreach (var e in all)
        {
            log.LogInformation("Stopping background process {Id} ({Command})", e.Id, e.Command);
            ProcessRunner.Kill(e.Process);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Kills a conversation's background processes together with the conversation.</summary>
public sealed class ProcessCleanupHandler(ProcessManager processes) : IEventHandler
{
    public Task HandleAsync(SofiaEvent e, CancellationToken ct)
    {
        if (e.Type == EventTypes.SessionDeleted && e.SessionId is not null) processes.KillSession(e.SessionId);
        return Task.CompletedTask;
    }
}

