using System.Diagnostics;
using System.Net.Http;

namespace Sofia.Acp;

public sealed class ServerLauncher
{
    private readonly string _baseUrl;
    private Process? _process;
    private bool _owned;

    public ServerLauncher(string? baseUrl = null)
    {
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("SOFIA_SERVER_URL") ?? "http://127.0.0.1:5199").TrimEnd('/');
    }

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (await ProbeAsync(ct)) return;
        var exe = ResolveServer();
        if (exe is null) throw new FileNotFoundException("Sofia.Server binary not found. Build the server or set SOFIA_SERVER_PATH.");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SOFIA_URLS"] = _baseUrl;
        _process = new Process { StartInfo = psi };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _owned = true;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new InvalidOperationException($"Sofia.Server exited ({_process.ExitCode}).");
            if (await ProbeAsync(CancellationToken.None)) return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Sofia.Server did not become healthy.");
    }

    public void Stop()
    {
        if (!_owned || _process is null) return;
        try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } } catch { }
        _owned = false;
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync(_baseUrl + "/api/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string? ResolveServer()
    {
        var env = Environment.GetEnvironmentVariable("SOFIA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var exeName = OperatingSystem.IsWindows() ? "Sofia.Server.exe" : "Sofia.Server";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Sofia.Server", "bin", config, "net10.0", exeName);
                if (File.Exists(candidate)) return candidate;
            }
            var baseExe = Path.Combine(dir.FullName, exeName);
            if (File.Exists(baseExe)) return baseExe;
            dir = dir.Parent;
        }
        return null;
    }
}
