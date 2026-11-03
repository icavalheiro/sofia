using System.Diagnostics;

namespace Sofia.Server.Infrastructure;

public sealed class BubblewrapSandbox : ISandbox
{
    private readonly string? _bwrap;

    public bool Available { get; }

    public SandboxStatus Status { get; }

    public BubblewrapSandbox()
    {
        _bwrap = ProcessRunner.FindOnPath("bwrap");
        Available = _bwrap is not null && Probe(_bwrap);
        Status = Available
            ? new SandboxStatus(true, "bubblewrap", null)
            : new SandboxStatus(false, "bubblewrap", "bwrap not found or not usable on this system.");
    }

    public Process Start(ProcessStartInfo psi, SandboxPolicy policy)
    {
        var bwrap = _bwrap ?? "bwrap";
        var psi2 = new ProcessStartInfo(bwrap)
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

        psi2.ArgumentList.Add("--ro-bind");
        psi2.ArgumentList.Add("/");
        psi2.ArgumentList.Add("/");
        psi2.ArgumentList.Add("--dev");
        psi2.ArgumentList.Add("/dev");
        psi2.ArgumentList.Add("--proc");
        psi2.ArgumentList.Add("/proc");
        foreach (var root in policy.WritableRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
        {
            psi2.ArgumentList.Add("--bind");
            psi2.ArgumentList.Add(root);
            psi2.ArgumentList.Add(root);
        }
        if (!policy.AllowNetwork)
        {
            psi2.ArgumentList.Add("--unshare-net");
        }
        psi2.ArgumentList.Add("--die-with-parent");
        if (!string.IsNullOrEmpty(psi.WorkingDirectory) && Directory.Exists(psi.WorkingDirectory))
        {
            psi2.ArgumentList.Add("--chdir");
            psi2.ArgumentList.Add(psi.WorkingDirectory);
        }
        psi2.ArgumentList.Add("--");
        psi2.ArgumentList.Add(psi.FileName);
        foreach (var a in psi.ArgumentList) psi2.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi2, EnableRaisingEvents = true };
        return process;
    }

    private static bool Probe(string bwrap)
    {
        try
        {
            var (exit, _, _) = ProcessRunner.ExecAsync(bwrap, ["--ro-bind", "/", "/", "true"], null, CancellationToken.None, TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }
}