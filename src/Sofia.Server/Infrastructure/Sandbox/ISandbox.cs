using System.Diagnostics;
using Sofia.Server.Domain;

namespace Sofia.Server.Infrastructure;

/// <summary>Policy describing what a sandboxed process may write to and whether it may reach the network.</summary>
public sealed record SandboxPolicy(IReadOnlyList<string> WritableRoots, bool AllowNetwork)
{
    public static SandboxPolicy? Resolve(ServerConfig config, Workspace? workspace, Session? session)
        => ResolveEffective(config, workspace, session);

    public static SandboxPolicy? Effective(ServerConfig config, Workspace? workspace, Session? session)
        => ResolveEffective(config, workspace, session);

    private static SandboxPolicy? ResolveEffective(ServerConfig config, Workspace? workspace, Session? session)
    {
        var enabled = session?.Sandbox ?? workspace?.Sandbox ?? config.Sandbox;
        if (!enabled) return null;
        if (!SandboxFactory.Create().Status.Available) return null;
        var roots = new List<string>();
        if (workspace is { IsFolder: true } && !string.IsNullOrEmpty(workspace.Path)) roots.Add(workspace.Path);
        var temp = Path.GetTempPath();
        if (!roots.Contains(temp, StringComparer.OrdinalIgnoreCase)) roots.Add(temp);
        return new SandboxPolicy(roots, config.SandboxNetwork);
    }
};

/// <summary>Helper for resolving the effective sandbox policy (alias of <see cref="SandboxPolicy"/>).</summary>
public static class SandboxResolver
{
    public static SandboxPolicy? Resolve(ServerConfig config, Workspace? workspace, Session? session) => SandboxPolicy.Resolve(config, workspace, session);
    public static SandboxPolicy? Effective(ServerConfig config, Workspace? workspace, Session? session) => SandboxPolicy.Effective(config, workspace, session);
}

/// <summary>Status of an <see cref="ISandbox"/> backend.</summary>
public sealed record SandboxStatus(bool Available, string Backend, string? Reason);

/// <summary>Launches or rewrites a process so it runs under an OS-level sandbox.</summary>
public interface ISandbox
{
    SandboxStatus Status { get; }

    /// <summary>
    /// Returns a process whose <see cref="Process.StartInfo"/> carries the sandboxed invocation.
    /// Rewriting backends return a not-yet-started process; launching backends (Windows) start it themselves.
    /// </summary>
    Process Start(ProcessStartInfo psi, SandboxPolicy policy);
}

/// <summary>Picks the backend for the current operating system and probes availability once.</summary>
public static class SandboxFactory
{
    private static readonly Lazy<ISandbox> Instance = new(CreateCore);

    public static ISandbox Create() => Instance.Value;

    private static ISandbox CreateCore()
    {
        if (OperatingSystem.IsWindows()) return new WindowsLowIntegritySandbox();
        if (OperatingSystem.IsMacOS()) return new SeatbeltSandbox();
        if (OperatingSystem.IsLinux()) return new BubblewrapSandbox();
        return new NoSandbox("Unsupported operating system.");
    }
}