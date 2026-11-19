#pragma warning disable CA1416 // window capture is Windows-only, and so is this check

// Windowed verification for spec 036: the emulated viewport must change the page layout and the screenshots while the
// real OS window keeps its own size. Evidence lands in .specs/spec_assets/036-*.

using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
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
var dataDir = Path.Combine(repo, ".sofia", "scripts", ".tmp", $"viewport-check-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(dataDir);
// Windowed by default; SOFIA_CHECK_HEADLESS=1 keeps the run off the desktop (the emulation works the same there).
Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS",
    Environment.GetEnvironmentVariable("SOFIA_CHECK_HEADLESS") is "1" or "true" ? "1" : null);

const string html = """
<html><head><title>Viewport check</title><style>
body { margin: 0; font: 16px/1.4 sans-serif; background: #f3efe9; color: #2b2b2b; }
h1 { margin: 16px; font-size: 28px; }
#flex { display: flex; flex-wrap: wrap; gap: 8px; margin: 0 16px; }
#flex div { width: 260px; height: 80px; background: #a9744f; color: #fff; display: flex; align-items: center; justify-content: center; }
</style></head><body>
<h1 id="size">measuring…</h1>
<div id="flex">
  <div>one</div><div>two</div><div>three</div><div>four</div><div>five</div><div>six</div>
</div>
<script>
  const update = () => {
    document.getElementById('size').textContent =
      innerWidth + ' x ' + innerHeight + '  ·  dpr ' + devicePixelRatio + '  ·  matchMedia(max-width:500px) ' +
      matchMedia('(max-width: 500px)').matches;
  };
  update();
  addEventListener('resize', update);
</script>
</body></html>
""";

// Use an external static server when its URL is provided (SOFIA_CHECK_URL), so the verification does not depend on an
// in-process listener; otherwise serve the same page from this process.
var externalUrl = Environment.GetEnvironmentVariable("SOFIA_CHECK_URL");
using var listener = new HttpListener();
var port = Random.Shared.Next(20000, 40000);
var url = externalUrl ?? $"http://127.0.0.1:{port}/";
if (externalUrl is null)
{
    listener.Prefixes.Add(url);
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
}
Console.WriteLine($"serving {url}");

var options = new SofiaOptions { DataDir = dataDir };
// In-memory store: this check is about the viewport, and a freshly created LiteDB file trips the endpoint scanner on
// this machine (IOException while opening), which has nothing to do with what is being verified.
using var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:", Connection = LiteDB.ConnectionType.Direct });
var sessionId = "ses_viewport_check";
db.Sessions.Insert(new Session { Id = sessionId, WorkspaceId = "ws_check", Title = "viewport check" });
var service = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);

Attachment Save(byte[] bytes, string mime, string name)
{
    var path = Path.Combine(dataDir, name);
    File.WriteAllBytes(path, bytes);
    return new Attachment { Id = "att", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
}

var tool = new BrowserTool(service, Save);

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

async Task<string> Run(JsonObject args)
{
    var result = await tool.ExecuteAsync(Context(), args, CancellationToken.None);
    // First windowed Chrome launch of a run occasionally exits 0 in this shell (profile handshake with a previous
    // process); the next attempt starts normally. Retry once so the evidence run is not lost to that flake.
    if (result.IsError && result.Output.Contains("exited while starting", StringComparison.Ordinal))
    {
        Console.WriteLine($"--- retrying after: {Text.Trim(result.Output, 160)}");
        result = await tool.ExecuteAsync(Context(), args, CancellationToken.None);
    }
    var text = (result.IsError ? "ERROR: " : "") + result.Output;
    Console.WriteLine($"--- {args.ToJsonString()}\n{Text.Trim(text, 900)}\n");
    var image = result.Images?.FirstOrDefault();
    if (image is not null) Console.WriteLine($"    [image] {image.StoragePath} ({image.Bytes} bytes)");
    return text;
}

JsonObject Args(params (string Key, string Value)[] pairs)
{
    var json = new JsonObject();
    foreach (var (key, value) in pairs)
        json[key] = key is "submit" or "full_page" or "mobile"
            ? JsonValue.Create(bool.Parse(value))
            : key is "width" or "height" or "tab"
                ? JsonValue.Create(int.Parse(value))
                : key is "device_scale_factor"
                    ? JsonValue.Create(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                    : JsonValue.Create(value);
    return json;
}

try
{
    Console.WriteLine($"Installed: {string.Join(", ", service.Installed().Select(e => e.Id))}");
    Console.WriteLine($"Headless: {BrowserService.Headless}, data dir: {dataDir}\n");

    // 1. Chrome: emulation changes the layout and the screenshots; the OS window keeps its size.
    await Run(Args(("action", "navigate"), ("url", url), ("browser", "chrome")));
    await Run(Args(("action", "screenshot"), ("saveTo", Path.Combine(assets, "036-viewport-native.jpg"))));
    int? nativePid = BrowserService.Headless ? null : BrowserInstanceStore.Enumerate(service.BrowsersRoot).First().Instance.Pid;
    if (nativePid is not null) Console.WriteLine($"window (native): {CaptureWindow(nativePid.Value, Path.Combine(assets, "036-viewport-native-window.png"))}\n");

    await Run(Args(("action", "viewport"), ("width", "400"), ("height", "800")));
    await Run(Args(("action", "screenshot"), ("saveTo", Path.Combine(assets, "036-viewport-chrome-400x800.jpg"))));
    if (nativePid is not null) Console.WriteLine($"window (emulated): {CaptureWindow(nativePid.Value, Path.Combine(assets, "036-viewport-windowed.png"))}\n");

    await Run(Args(("action", "viewport"), ("width", "400"), ("height", "800"), ("mobile", "true"), ("device_scale_factor", "2")));

    // 2. Reset returns the page to the window's size.
    await Run(Args(("action", "viewport")));
    await Run(Args(("action", "screenshot"), ("saveTo", Path.Combine(assets, "036-viewport-reset.jpg"))));
    await Run(Args(("action", "close")));

    // 4. Firefox: width/height only; mobile/dsf are reported as unsupported.
    await Run(Args(("action", "navigate"), ("url", url), ("browser", "firefox")));
    await Run(Args(("action", "viewport"), ("width", "480"), ("height", "800"), ("mobile", "true"), ("device_scale_factor", "2")));
    await Run(Args(("action", "screenshot"), ("saveTo", Path.Combine(assets, "036-viewport-firefox.jpg"))));
    await Run(Args(("action", "viewport")));
    await Run(Args(("action", "close")));
}
finally
{
    await service.DisposeAsync();
    listener.Stop();
    try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
}

Console.WriteLine("done");

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
        if (handle == IntPtr.Zero) return "no window handle";
        GetWindowRect(handle, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        var printed = PrintWindow(handle, hdc, 2); // PW_RENDERFULLCONTENT
        graphics.ReleaseHdc(hdc);
        bitmap.Save(path, ImageFormat.Png);
        return $"[{width}x{height}] captured={printed} -> {path}";
    }
    catch (Exception ex)
    {
        return "window capture failed: " + ex.Message;
    }
}

[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

struct RECT { public int Left, Top, Right, Bottom; }

file static class Text
{
    public static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
