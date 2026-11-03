using System.Diagnostics;
using System.Text;

namespace Sofia.Server.Infrastructure;

public sealed class SeatbeltSandbox : ISandbox
{
    private static readonly string SandboxExec = "/usr/bin/sandbox-exec";

    public bool Available { get; }

    public SandboxStatus Status { get; }

    public SeatbeltSandbox()
    {
        Available = File.Exists(SandboxExec);
        Status = Available
            ? new SandboxStatus(true, "seatbelt", null)
            : new SandboxStatus(false, "seatbelt", "sandbox-exec not found on this system.");
    }

    public Process Start(ProcessStartInfo psi, SandboxPolicy policy)
    {
        var profile = BuildProfile(policy);
        var psi2 = new ProcessStartInfo(SandboxExec)
        {
            RedirectStandardOutput = psi.RedirectStandardOutput,
            RedirectStandardError = psi.RedirectStandardError,
            RedirectStandardInput = psi.RedirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = psi.StandardOutputEncoding,
            StandardErrorEncoding = psi.StandardErrorEncoding,
            WorkingDirectory = psi.WorkingDirectory,
        };
        foreach (var kv in psi.Environment) psi2.Environment[kv.Key] = kv.Value;

        psi2.ArgumentList.Add("-p");
        psi2.ArgumentList.Add(profile);
        psi2.ArgumentList.Add(psi.FileName);
        foreach (var a in psi.ArgumentList) psi2.ArgumentList.Add(a);

        var tempRoot = policy.WritableRoots.FirstOrDefault(r => r.Contains("Temp", StringComparison.OrdinalIgnoreCase)
            || r.Contains("tmp", StringComparison.OrdinalIgnoreCase))
            ?? policy.WritableRoots.LastOrDefault();
        if (!string.IsNullOrEmpty(tempRoot))
            psi2.Environment["TMPDIR"] = tempRoot;

        var process = new Process { StartInfo = psi2, EnableRaisingEvents = true };
        return process;
    }

    public static string BuildProfile(SandboxPolicy policy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(version 1)");
        sb.AppendLine("(allow default)");
        sb.AppendLine("(deny file-write*)");
        sb.Append("(allow file-write*");
        foreach (var root in policy.WritableRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            sb.Append(" (subpath \"").Append(Escape(root)).Append("\")");
        }
        sb.AppendLine(" (literal \"/dev/null\"))");
        if (!policy.AllowNetwork)
        {
            sb.AppendLine("(deny network*)");
        }
        return sb.ToString();
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}