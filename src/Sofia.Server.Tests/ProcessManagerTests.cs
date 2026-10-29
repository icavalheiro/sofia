using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tests;

public class ProcessManagerTests
{
    private static readonly ShellSpec Shell = ProcessRunner.ResolveShell(null);

    private static string LongRunning => Shell.Name == "PowerShell" ? "Start-Sleep -Seconds 30" : "sleep 30";

    private static ProcessManager NewManager() => new(NullLogger<ProcessManager>.Instance);

    /// <summary>Waits until the process leaves the running state (the exit event is asynchronous).</summary>
    private static async Task<BackgroundProcessInfo> WaitFinished(ProcessManager pm, string id)
    {
        for (var i = 0; i < 100; i++)
        {
            var info = Assert.Single(pm.List(), p => p.Id == id);
            if (info.Status != "running") return info;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Process {id} did not finish in time.");
    }

    [Fact]
    public async Task ClearDropsFinishedProcessesAndKeepsRunningOnes()
    {
        var pm = NewManager();
        try
        {
            var finished = pm.Start(Shell, "echo done", cwd: null, sessionId: "s1");
            await WaitFinished(pm, finished.Id);

            var running = pm.Start(Shell, LongRunning, cwd: null, sessionId: "s1");
            Assert.Equal("running", running.Status);

            Assert.Equal(1, pm.Clear());

            var remaining = pm.List();
            var only = Assert.Single(remaining);
            Assert.Equal(running.Id, only.Id);
            Assert.Equal("running", only.Status);
        }
        finally
        {
            await pm.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ClearWithNothingFinishedIsANoOp()
    {
        var pm = NewManager();
        try
        {
            pm.Start(Shell, LongRunning, cwd: null, sessionId: "s1");

            Assert.Equal(0, pm.Clear());
            Assert.Single(pm.List());
        }
        finally
        {
            await pm.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KillThenClearLeavesAnEmptyList()
    {
        var pm = NewManager();
        try
        {
            var p = pm.Start(Shell, LongRunning, cwd: null, sessionId: "s1");
            Assert.Equal("killed", pm.Kill(p.Id).Status);

            Assert.Equal(1, pm.Clear());
            Assert.Empty(pm.List());
        }
        finally
        {
            await pm.StopAsync(CancellationToken.None);
        }
    }
}
