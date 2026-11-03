using System.Diagnostics;

namespace Sofia.Server.Infrastructure;

/// <summary>
/// Windows sandbox backend. A full Low-integrity implementation requires P/Invoke and is not yet available;
/// commands therefore run unsandboxed and the status reports the backend as unavailable.
/// </summary>
public sealed class WindowsLowIntegritySandbox : ISandbox
{
    public SandboxStatus Status { get; } = new(false, "windows-low-integrity", "Windows Low-integrity sandbox is not available in this build.");

    public Process Start(ProcessStartInfo psi, SandboxPolicy policy)
    {
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        return process;
    }
}