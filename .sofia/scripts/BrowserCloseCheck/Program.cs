// Windowed check: closing the Chrome window Sofia opened must actually close it. The reported bug was the window
// closing and reopening by itself, because a replacement page was created while Chromium was shutting down.
// Run from the repo root: dotnet run --project .sofia/scripts/BrowserCloseCheck

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Browser;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

var repo = Environment.CurrentDirectory;
var dataDir = Path.Combine(repo, ".sofia", "scripts", ".tmp", $"close-check-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(dataDir);
Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", null);

var options = new SofiaOptions { DataDir = dataDir };
// In-memory store: this check is about the window, and a fresh LiteDB file trips the endpoint scanner on this machine.
using var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:", Connection = LiteDB.ConnectionType.Direct });
var sessionId = "ses_close_check";
db.Sessions.Insert(new Session { Id = sessionId, WorkspaceId = "ws_check", Title = "browser close check" });
var service = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);
var tool = new BrowserTool(service);

ToolContext Context() => new()
{
    Session = new Session { Id = sessionId },
    Workspace = new Workspace { Id = "ws_check" },
    Cwd = repo,
    Message = null!,
    Call = null!,
    Config = new ServerConfig(),
    Changes = null!,
    Host = null!,
};

async Task<string> Run(string action)
{
    var args = new JsonObject { ["action"] = action };
    if (action == "navigate") args["url"] = "about:blank";
    var result = await tool.ExecuteAsync(Context(), args, CancellationToken.None);
    var text = (result.IsError ? "ERROR: " : "") + result.Output;
    Console.WriteLine($"--- {action}: {Trim(text, 300)}");
    return text;
}

int? BrowserPid() => BrowserInstanceStore.Enumerate(service.BrowsersRoot).Select(e => (int?)e.Instance.Pid).FirstOrDefault();

(bool Exited, int Windows) Probe(int pid)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        process.Refresh();
        return (process.HasExited, process.MainWindowHandle == IntPtr.Zero ? 0 : 1);
    }
    catch (ArgumentException)
    {
        return (true, 0);
    }
}

var failures = new List<string>();
try
{
    await Run("navigate");
    var pid = BrowserPid() ?? throw new InvalidOperationException("no instance record: the window was not started by Sofia");
    using (var process = System.Diagnostics.Process.GetProcessById(pid))
    {
        for (var i = 0; i < 20 && process.MainWindowHandle == IntPtr.Zero; i++) { await Task.Delay(150); process.Refresh(); }
        Console.WriteLine($"window: pid={pid} handle={process.MainWindowHandle} title='{process.MainWindowTitle}'");
        if (!process.CloseMainWindow()) failures.Add("CloseMainWindow() refused the close request");
    }

    var gone = false;
    for (var i = 0; i < 100 && !gone; i++)
    {
        await Task.Delay(100);
        gone = Probe(pid).Exited;
    }
    var session = service.Find(sessionId);
    Console.WriteLine($"after close: process-exited={gone} session={(session is null ? "forgotten" : "still tracked")} " +
                      $"instance-records={BrowserInstanceStore.Enumerate(service.BrowsersRoot).Count}");
    if (!gone) failures.Add("the browser process survived the window close (window reopened?)");
    if (session is not null) failures.Add("the session was not forgotten after the window closed");

    // A closed window must not be a dead end: the next call opens a fresh one.
    await Run("snapshot");
    var reopenedPid = BrowserPid();
    Console.WriteLine($"reopened: session={(service.Find(sessionId) is null ? "none" : "tracked")} pid={reopenedPid}");
    if (reopenedPid is null || reopenedPid == pid) failures.Add("the next call did not open a fresh window");
    await Run("close");
    await Task.Delay(1000);
    Console.WriteLine($"after close action: instance records={BrowserInstanceStore.Enumerate(service.BrowsersRoot).Count}");
}
finally
{
    await service.DisposeAsync();
    try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures.Count == 0 ? "PASS: the window closes and stays closed" : "FAIL:\n - " + string.Join("\n - ", failures));
return failures.Count == 0 ? 0 : 1;

static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
