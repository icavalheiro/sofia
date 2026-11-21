#:package Microsoft.Playwright@1.65.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Captures the Vue web UI (the parity reference for spec 041) at the three epic sizes.
// Usage: dotnet run .sofia/scripts/webshot.cs -- <url> <out-dir> <slug> [clickSelector]
using Microsoft.Playwright;

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5199";
var outDir = args.Length > 1 ? args[1] : ".specs/spec_assets";
var slug = args.Length > 2 ? args[2] : "041-web";
var scroll = args.Length > 3 ? args[3] : "keep";

var sizes = new (string Name, int Width, int Height, float Scale)[]
{
    ("1080p", 1920, 1080, 1f),
    ("2k", 2560, 1440, 1.333f),
    ("4k", 3840, 2160, 2f),
};

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
    Channel = "chrome",
});
foreach (var (name, width, height, scale) in sizes)
{
    var context = await browser.NewContextAsync(new BrowserNewContextOptions
    {
        ViewportSize = new ViewportSize { Width = width, Height = height },
        DeviceScaleFactor = scale,
    });
    var page = await context.NewPageAsync();
    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
    await page.WaitForTimeoutAsync(4_000);
    if (args.Length > 4) { await page.ClickAsync(args[4], new PageClickOptions { Timeout = 5_000 }); await page.WaitForTimeoutAsync(1_500); }
    if (scroll is "top" or "bottom")
    {
        await page.EvaluateAsync(@"dir => { const el = document.querySelector('.messages'); if (el) el.scrollTop = dir === 'top' ? 0 : el.scrollHeight; }", scroll);
        await page.WaitForTimeoutAsync(800);
    }
    else if (int.TryParse(scroll, out var pixels))
    {
        await page.EvaluateAsync("px => { const el = document.querySelector('.messages'); if (el) el.scrollTop = px; }", pixels);
        await page.WaitForTimeoutAsync(800);
    }
    var path = Path.Combine(outDir, $"{slug}-{name}.png");
    await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
    Console.WriteLine(path);
    await context.CloseAsync();
}
