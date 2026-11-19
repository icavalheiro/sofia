#pragma warning disable CA1416 // screen capture is Windows-only, and so is this check

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Browser;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

var repo = Environment.CurrentDirectory;
var assets = Path.Combine(repo, ".specs", "spec_assets");
Directory.CreateDirectory(assets);
var dataDir = Path.Combine(Path.GetTempPath(), $"sofia-window-check-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(dataDir);
// Windowed: this check is about the real window, so ignore any inherited headless setting.
Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", null);

// A browser window that outlives a "restart" is left running on purpose: log in, then clean it up at the end.
var options = new SofiaOptions { DataDir = dataDir };
var db = new SofiaDb(options);
var sessionId = "ses_check";
// The reconciler only re-attaches windows whose conversation still exists.
db.Sessions.Insert(new Session { Id = sessionId, WorkspaceId = "ws_check", Title = "browser window check" });
var service = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);
BrowserTool tool = null!;

Console.WriteLine($"Installed engines: {string.Join(", ", service.Installed().Select(e => $"{e.Id} ({e.Version})"))}");
Console.WriteLine($"Headless default: {BrowserService.Headless}");
tool = new BrowserTool(service, (bytes, mime, name) =>
{
    var path = Path.Combine(dataDir, name);
    File.WriteAllBytes(path, bytes);
    return new Attachment { Id = "att", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
});

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

async Task<string> Run(JsonObject args, bool shot = false)
{
    var result = await tool.ExecuteAsync(Context(), args, CancellationToken.None);
    var text = result.IsError ? $"ERROR: {result.Output}" : result.Output;
    Console.WriteLine($"--- {args.ToJsonString()}\n{Trim(text, 800)}");
    // The screenshot must show the window as the user sees it, so give the compositor a moment.
    if (shot) { Thread.Sleep(1500); CaptureDesktop(Path.Combine(assets, $"035-{args["browser"]?.GetValue<string>() ?? "chrome"}-window.png")); }
    return text;
}

// 1. Chrome: a real window started by Sofia itself, driven over CDP.
await Run(Args(("action", "launch"), ("browser", "chrome")));
var chromePid = BrowserInstanceStore.Enumerate(service.BrowsersRoot).First().Instance.Pid;
Console.WriteLine($"Window: {CaptureWindow(chromePid, Path.Combine(assets, "035-chrome-window.png"))}");
CaptureDesktop(Path.Combine(assets, "035-desktop.png"));
await Run(Args(("action", "navigate"), ("url", "google.com")));
var search = await Run(Args(("action", "snapshot")));
await Run(Args(("action", "evaluate"), ("script", "() => JSON.stringify({ webdriver: navigator.webdriver, ua: navigator.userAgent.slice(0, 80) })")));
var line = search.Split('\n').FirstOrDefault(l => l.Contains("combobox") || l.Contains("textbox")) ?? "";
var start = line.IndexOf('[');
var end = line.IndexOf(']');
if (start >= 0 && end > start)
{
    await Run(Args(("action", "type"), ("ref", line[(start + 1)..end]), ("text", "OtxIdle"), ("submit", "true")));
    Thread.Sleep(2500);
    await Run(Args(("action", "screenshot")));
    Console.WriteLine($"Window after search: {CaptureWindow(chromePid, Path.Combine(assets, "035-chrome-search-window.png"))}");
}
Console.WriteLine($"Instance record: {Describe(service.BrowsersRoot)}");

// 2. The user closes the window: the session must notice and forget it; the next call reopens one.
var live = service.Find(sessionId)!;
var pid = BrowserInstanceStore.Enumerate(service.BrowsersRoot).First().Instance.Pid;
BrowserInstanceStore.Kill(new BrowserInstance { Pid = pid, Executable = "" }, BrowserInstanceStore.ExecutableOfPid);
Thread.Sleep(3000);
Console.WriteLine($"After the window was killed: Find={(service.Find(sessionId) is null ? "null" : "still open")}, engine={live.EngineId}");
await Run(Args(("action", "snapshot")));
Console.WriteLine($"Reopened record: {Describe(service.BrowsersRoot)}");

// 3. Restart: a graceful shutdown leaves the window open; the next process re-attaches instead of opening another.
await service.DisposeAsync();
var beforeRestart = BrowserInstanceStore.Enumerate(dataDir.Length > 0 ? Path.Combine(dataDir, "browsers") : "").ToList();
var restarted = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);
await restarted.ReconcileAsync(CancellationToken.None);
var reattached = restarted.Find(sessionId);
Console.WriteLine($"After restart: records={beforeRestart.Count}, reattached={(reattached is null ? "no" : $"{reattached.EngineId} {reattached.Page.Url}")}");
await restarted.ForgetAsync(sessionId);
Console.WriteLine($"After forget: Find={(restarted.Find(sessionId) is null ? "null" : "open")}, records={BrowserInstanceStore.Enumerate(Path.Combine(dataDir, "browsers")).Count}");
await restarted.DisposeAsync();

// 4. Firefox: the installed build over the moz-firefox WebDriver BiDi channel (experimental).
var firefoxService = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);
tool = new BrowserTool(firefoxService, (bytes, mime, name) =>
{
    var path = Path.Combine(dataDir, name);
    File.WriteAllBytes(path, bytes);
    return new Attachment { Id = "att", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
});
await Run(Args(("action", "launch"), ("browser", "firefox")));
await Run(Args(("action", "navigate"), ("url", "example.com"), ("browser", "firefox")));
var ff = await Run(Args(("action", "screenshot"), ("browser", "firefox")));
var ffPath = ff.Split('\n').FirstOrDefault(l => l.StartsWith("Path: "))?.Replace("Path: ", "").Trim();
if (ffPath is not null && File.Exists(ffPath)) File.Copy(ffPath, Path.Combine(assets, "035-firefox-page.jpg"), true);
await Run(Args(("action", "close")));
await firefoxService.DisposeAsync();

db.Dispose();
try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
Console.WriteLine($"Temp data dir removed: {!Directory.Exists(dataDir)}");
Console.WriteLine("done");

static JsonObject Args(params (string Key, string Value)[] pairs)
{
    var json = new JsonObject();
    foreach (var (key, value) in pairs)
        json[key] = key is "submit" or "full_page" ? JsonValue.Create(bool.Parse(value)) : JsonValue.Create(value);
    return json;
}

static string Describe(string browsersRoot) =>
    string.Join("; ", BrowserInstanceStore.Enumerate(browsersRoot).Select(e => $"{e.Instance.Engine} pid={e.Instance.Pid} port={e.Instance.Port} session={e.Instance.SessionId}"));

static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "...";

// Captures the whole virtual desktop, so the evidence shows the actual browser window (not just the page).
static void CaptureDesktop(string path)
{
    var width = GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
    var height = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
    var left = GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
    var top = GetSystemMetrics(77);    // SM_YVIRTUALSCREEN
    using var bitmap = new Bitmap(width, height);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
    bitmap.Save(path, ImageFormat.Png);
    Console.WriteLine($"screenshot -> {path} ({width}x{height})");
}

[DllImport("user32.dll")]
static extern int GetSystemMetrics(int index);

// Captures the browser window itself (native chrome included) so the evidence does not depend on window z-order.
static string CaptureWindow(int pid, string path)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        var handle = process.MainWindowHandle;
        for (var attempt = 0; attempt < 20 && handle == IntPtr.Zero; attempt++)
        {
            Thread.Sleep(150);
            process.Refresh();
            handle = process.MainWindowHandle;
        }
        if (handle == IntPtr.Zero) return "no window handle (the browser window is not visible)";
        GetWindowRect(handle, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        var printed = PrintWindow(handle, hdc, 2); // PW_RENDERFULLCONTENT
        graphics.ReleaseHdc(hdc);
        bitmap.Save(path, ImageFormat.Png);
        var title = new System.Text.StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        return $"[{width}x{height}] title={title} captured={printed} -> {path}";
    }
    catch (Exception ex)
    {
        return "window capture failed: " + ex.Message;
    }
}

[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

struct RECT { public int Left, Top, Right, Bottom; }
