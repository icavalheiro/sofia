using System.Diagnostics;
using System.Text;

namespace Sofia.Server.Infrastructure;

public sealed record ShellSpec(string Executable, string Name, Func<string, IEnumerable<string>> Arguments);

public sealed record ProcessResult(string Output, int? ExitCode, bool TimedOut, bool Cancelled);

/// <summary>Runs shell commands and plain executables with merged, size-capped output.</summary>
public static class ProcessRunner
{
    private static readonly Lazy<string?> Pwsh = new(() => FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"));

    public static ShellSpec ResolveShell(string? configured)
    {
        var choice = configured?.Trim();
        if (!string.IsNullOrEmpty(choice))
        {
            var lower = Path.GetFileNameWithoutExtension(choice).ToLowerInvariant();
            if (lower is "pwsh" or "powershell")
                return new ShellSpec(choice, "PowerShell", PowerShellArgs);
            if (lower is "cmd")
                return new ShellSpec(choice, "cmd", c => ["/d", "/s", "/c", c]);
            return new ShellSpec(choice, lower, c => ["-lc", c]);
        }

        if (OperatingSystem.IsWindows())
            return new ShellSpec(Pwsh.Value ?? "powershell.exe", "PowerShell", PowerShellArgs);

        return File.Exists("/bin/bash")
            ? new ShellSpec("/bin/bash", "bash", c => ["-lc", c])
            : new ShellSpec("/bin/sh", "sh", c => ["-c", c]);
    }

    private static IEnumerable<string> PowerShellArgs(string command) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command];

    public static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are skipped.
            }
        }
        return null;
    }

    public static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> args, string? cwd)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        return psi;
    }

    public static Task<ProcessResult> RunShellAsync(ShellSpec shell, string command, string? cwd, TimeSpan timeout,
        Action<string>? onOutput, int maxChars, CancellationToken ct) =>
        RunAsync(StartInfo(shell.Executable, shell.Arguments(command), cwd), timeout, onOutput, maxChars, ct);

    public static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, Action<string>? onOutput,
        int maxChars, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var gate = new object();
        var dropped = 0L;

        void Append(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                if (output.Length < maxChars * 2) output.Append(line).Append('\n');
                else dropped += line.Length + 1;
            }
            onOutput?.Invoke(line + "\n");
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            Kill(process);
        }

        // Let the async readers flush the last lines.
        try { process.WaitForExit(2000); } catch { /* already gone */ }

        string text;
        lock (gate)
        {
            text = output.ToString().TrimEnd('\n');
            if (dropped > 0) text += $"\n… [{dropped} more characters of output dropped]";
        }
        int? exit = null;
        try { if (process.HasExited) exit = process.ExitCode; } catch { /* not started */ }
        return new ProcessResult(Text.Truncate(text, maxChars), exit, timedOut, cancelled);
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may exit between the check and the kill.
        }
    }

    /// <summary>Runs an executable and returns trimmed stdout, or null on failure.</summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> ExecAsync(string fileName, IEnumerable<string> args,
        string? cwd, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var psi = StartInfo(fileName, args, cwd);
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
