using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Browser;

/// <summary>
/// What Sofia remembers about a browser window it launched, so a server restart can re-attach to it (or reap it)
/// instead of opening a second window. Stored as <c>instance.json</c> inside the browser's profile directory.
/// </summary>
public sealed record BrowserInstance
{
    /// <summary>Catalog id of the engine that owns the window ('chrome', 'firefox', …).</summary>
    public string Engine { get; init; } = "";
    /// <summary>Absolute path of the executable that was started, used to verify a pid before killing it.</summary>
    public string Executable { get; init; } = "";
    public int Pid { get; init; }
    /// <summary>Loopback CDP port (Chromium only; 0 for engines that cannot be re-attached, such as Firefox).</summary>
    public int Port { get; init; }
    public string SessionId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>What <see cref="BrowserInstanceStore.Reconcile"/> decided for one recorded window.</summary>
public enum BrowserInstanceAction
{
    /// <summary>The window is alive and its conversation still exists: connect to it.</summary>
    Attach,
    /// <summary>The window is alive but its conversation is gone: kill the process.</summary>
    Kill,
    /// <summary>Nothing is listening any more (or the record is unusable): forget it and keep the profile.</summary>
    Prune,
}

public sealed record BrowserInstanceDecision(BrowserInstance Instance, BrowserInstanceAction Action);

public sealed record BrowserReconcileResult(
    IReadOnlyList<BrowserInstance> Attach,
    IReadOnlyList<BrowserInstance> Killed,
    IReadOnlyList<BrowserInstance> Pruned);

/// <summary>
/// The on-disk record of the browser windows Sofia launched (<c>&lt;profileDir&gt;/instance.json</c>) plus the rules
/// that decide, at startup, whether each recorded window is re-attached, killed or forgotten.
/// </summary>
public static class BrowserInstanceStore
{
    public const string FileName = "instance.json";

    public static string PathFor(string profileDir) => Path.Combine(profileDir, FileName);

    public static void Write(string profileDir, BrowserInstance instance)
    {
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(PathFor(profileDir), JsonSerializer.Serialize(instance, Json.Options));
    }

    public static BrowserInstance? Read(string profileDir)
    {
        try
        {
            var path = PathFor(profileDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BrowserInstance>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string profileDir)
    {
        try
        {
            var path = PathFor(profileDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale record is harmless: the next reconcile prunes it again.
        }
    }

    /// <summary>Every recorded window under <c>&lt;dataDir&gt;/browsers/&lt;sessionId&gt;/&lt;engine&gt;/</c>.</summary>
    public static IReadOnlyList<(string ProfileDir, BrowserInstance Instance)> Enumerate(string browsersRoot)
    {
        var found = new List<(string, BrowserInstance)>();
        if (!Directory.Exists(browsersRoot)) return found;
        foreach (var path in Directory.EnumerateFiles(browsersRoot, FileName, SearchOption.AllDirectories))
        {
            var profileDir = Path.GetDirectoryName(path);
            if (profileDir is null) continue;
            var instance = Read(profileDir);
            if (instance is not null) found.Add((profileDir, instance));
        }
        return found;
    }

    /// <summary>
    /// The three rules, as a pure function: alive + live conversation → attach; alive + dead conversation → kill
    /// (only when the pid still runs the recorded executable); nothing listening → prune.
    /// </summary>
    public static IReadOnlyList<BrowserInstanceDecision> Decide(
        IEnumerable<BrowserInstance> instances,
        IReadOnlyCollection<string> liveSessionIds,
        Func<int, bool> isListening,
        Func<int, string?> executableOfPid)
    {
        var live = liveSessionIds as ISet<string> ?? liveSessionIds.ToHashSet(StringComparer.Ordinal);
        var decisions = new List<BrowserInstanceDecision>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.SessionId) || instance.Port <= 0 || !isListening(instance.Port))
            {
                decisions.Add(new(instance, BrowserInstanceAction.Prune));
                continue;
            }
            if (live.Contains(instance.SessionId))
            {
                decisions.Add(new(instance, BrowserInstanceAction.Attach));
                continue;
            }
            // A recycled pid must never be killed: only trust it while it still runs the recorded executable.
            var current = instance.Pid > 0 ? executableOfPid(instance.Pid) : null;
            var sameProcess = current is not null && instance.Executable.Length > 0 && SameExecutable(current, instance.Executable);
            decisions.Add(new(instance, sameProcess ? BrowserInstanceAction.Kill : BrowserInstanceAction.Prune));
        }
        return decisions;
    }

    /// <summary>Applies <see cref="Decide"/>; windows to attach are returned so the caller can connect to them.</summary>
    public static BrowserReconcileResult Reconcile(
        string browsersRoot,
        IReadOnlyCollection<string> liveSessionIds,
        Func<int, bool>? isListening = null,
        Func<int, string?>? executableOfPid = null)
    {
        var recorded = Enumerate(browsersRoot);
        var decisions = Decide(
            recorded.Select(r => r.Instance),
            liveSessionIds,
            isListening ?? IsListening,
            executableOfPid ?? ExecutableOfPid);

        var attach = new List<BrowserInstance>();
        var killed = new List<BrowserInstance>();
        var pruned = new List<BrowserInstance>();
        var dirs = recorded.GroupBy(r => r.Instance).ToDictionary(g => g.Key, g => g.First().ProfileDir);
        foreach (var decision in decisions)
        {
            var profileDir = dirs[decision.Instance];
            switch (decision.Action)
            {
                case BrowserInstanceAction.Attach:
                    attach.Add(decision.Instance);
                    break;
                case BrowserInstanceAction.Kill:
                    if (Kill(decision.Instance, executableOfPid ?? ExecutableOfPid)) killed.Add(decision.Instance);
                    else pruned.Add(decision.Instance);
                    Delete(profileDir);
                    break;
                default:
                    pruned.Add(decision.Instance);
                    Delete(profileDir);
                    break;
            }
        }
        return new(attach, killed, pruned);
    }

    /// <summary>Kills a recorded window, but only if the pid still runs the executable that was recorded.</summary>
    public static bool Kill(BrowserInstance instance, Func<int, string?> executableOfPid)
    {
        if (instance.Pid <= 0) return false;
        var current = executableOfPid(instance.Pid);
        if (current is null) return false;
        if (instance.Executable.Length > 0 && !SameExecutable(current, instance.Executable)) return false;
        try
        {
            using var process = Process.GetProcessById(instance.Pid);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Executable behind a live pid (null when the pid is gone or cannot be inspected).</summary>
    public static string? ExecutableOfPid(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when something accepts TCP connections on the loopback debug port.</summary>
    public static bool IsListening(int port)
    {
        if (port <= 0 || port > 65_535) return false;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(300))) return false;
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool SameExecutable(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
