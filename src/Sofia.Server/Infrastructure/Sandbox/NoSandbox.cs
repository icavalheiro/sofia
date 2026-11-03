using System.Diagnostics;

namespace Sofia.Server.Infrastructure;

public sealed class NoSandbox(string reason = "No sandbox backend available.") : ISandbox
{
    public SandboxStatus Status { get; } = new(false, "none", reason);

    public Process Start(ProcessStartInfo psi, SandboxPolicy policy)
    {
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        return process;
    }
}