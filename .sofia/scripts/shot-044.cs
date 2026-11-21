#:package Microsoft.Playwright@1.65.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Spec 044 verification helper: drives the installed Chrome against the throwaway server and writes screenshots.
// Usage: dotnet run .sofia/scripts/shot-044.cs -- <url> <out.png> [width] [height] [deviceScale] [reducedMotion]
//        [fullPage] [waitMs]

using Microsoft.Playwright;

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5301/";
var outPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("shot.png");
var width = args.Length > 2 ? int.Parse(args[2]) : 1920;
var height = args.Length > 3 ? int.Parse(args[3]) : 1080;
var scale = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
var reduceMotion = args.Length > 5 && args[5] == "1";
var fullPage = args.Length > 6 && args[6] == "1";
var waitMs = args.Length > 7 ? int.Parse(args[7]) : 15000;

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Channel = "chrome",
    Headless = true,
    Args = ["--no-sandbox", "--use-gl=swiftshader", "--enable-unsafe-swiftshader"],
});
var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = width, Height = height },
    DeviceScaleFactor = (float)scale,
    ReducedMotion = reduceMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference,
});
var page = await context.NewPageAsync();
var logs = new List<string>();
page.Console += (_, message) => logs.Add($"[{message.Type}] {message.Text}");
page.PageError += (_, error) => logs.Add($"[pageerror] {error}");
page.RequestFailed += (_, request) => logs.Add($"[requestfailed] {request.Url} {request.Failure}");

Console.WriteLine($"navigating {url} at {width}x{height}@{scale} reducedMotion={reduceMotion}");
await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
try
{
    await page.WaitForSelectorAsync("#out canvas", new PageWaitForSelectorOptions { Timeout = waitMs });
    Console.WriteLine("canvas found");
}
catch (Exception ex)
{
    Console.WriteLine("canvas NOT found: " + ex.Message);
}
await page.WaitForTimeoutAsync(2500);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = outPath, FullPage = fullPage });
Console.WriteLine("screenshot: " + outPath);

var diagnostics = await page.EvaluateAsync<string>(
    "() => { const c = document.querySelector('#out canvas'); const r = c ? c.getBoundingClientRect() : null; " +
    "return JSON.stringify({ title: document.title, canvas: r ? (r.width + 'x' + r.height) : 'none', " +
    "boot: !!document.getElementById('boot'), keys: Object.keys(localStorage), text: document.body.innerText.slice(0, 300) }); }");
Console.WriteLine("diagnostics: " + diagnostics);

foreach (var line in logs.TakeLast(60)) Console.WriteLine(line);
await context.CloseAsync();
