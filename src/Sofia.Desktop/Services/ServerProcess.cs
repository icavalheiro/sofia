using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Sofia.Server.Infrastructure;

namespace Sofia.Desktop.Services;

public sealed class ServerProcess : IDisposable
{
    public const string BaseUrl = "http://127.0.0.1:5199";

    public bool IsOwned { get; private set; }
    public string Status { get; private set; } = "idle";
    public string? ExecutablePath { get; private set; }

    private Process? _process;
    private StreamWriter? _log;
    private bool _disposed;
    private IntPtr _jobHandle = IntPtr.Zero;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Reuse an already-healthy server (no child process, no LiteDB lock collision).
        if (await ProbeHealthAsync(ct))
        {
            Status = "reused";
            IsOwned = false;
            Console.WriteLine("[sofia.desktop] Reusing an already-healthy Sofia.Server on " + BaseUrl);
            return;
        }

        ExecutablePath = ServerLocator.Resolve();
        if (ExecutablePath is null)
            throw new FileNotFoundException(
                "Sofia.Server binary not found. Build the server project or set SOFIA_SERVER_PATH.");
        Console.WriteLine("[sofia.desktop] Starting Sofia.Server: " + ExecutablePath);

        var logDir = Path.Combine(SofiaOptions.DefaultDataDir(), "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "server.log");
        _log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        _log.WriteLine($"[{DateTime.UtcNow:O}] starting {ExecutablePath}");

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SOFIA_URLS"] = BaseUrl;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log?.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log?.WriteLine("[err] " + e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        AssignToJob(_process);
        IsOwned = true;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                Console.WriteLine("[sofia.desktop] Sofia.Server exited before becoming healthy (code " + _process.ExitCode + "). See " + logPath);
                throw new InvalidOperationException(
                    $"Sofia.Server exited before becoming healthy (code {_process.ExitCode}). See {(_log is null ? "logs" : logPath)}.");
            }
            if (await ProbeHealthAsync(CancellationToken.None))
            {
                Status = "started";
                Console.WriteLine("[sofia.desktop] Sofia.Server is healthy on " + BaseUrl);
                return;
            }
            await Task.Delay(500, ct);
        }
        Console.WriteLine("[sofia.desktop] Sofia.Server did not become healthy within 30s.");
        throw new TimeoutException("Sofia.Server did not become healthy within 30s.");
    }

    public void Stop()
    {
        if (!IsOwned || _process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch { /* best effort */ }
        finally
        {
            IsOwned = false;
            Status = "stopped";
        }
    }

    private static async Task<bool> ProbeHealthAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync(BaseUrl + "/api/health", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Contains("\"ok\"") || body.Contains("ok");
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _process?.Dispose(); } catch { }
        try { _log?.Dispose(); } catch { }
        if (_jobHandle != IntPtr.Zero)
        {
            try { CloseHandle(_jobHandle); } catch { }
            _jobHandle = IntPtr.Zero;
        }
    }

    private void AssignToJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, ref info, (uint)size)) return;
            if (!AssignProcessToJobObject(_jobHandle, process.Handle)) return;
        }
        catch
        {
            // Best effort: if assignment fails (e.g. already in a job), fall back to explicit Stop().
            if (_jobHandle != IntPtr.Zero) { try { CloseHandle(_jobHandle); } catch { } _jobHandle = IntPtr.Zero; }
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
