// Verification helper for plan-040: captures the running Vue sidebar at the epic's three sizes so the Avalonia port
// can be compared side by side. Read-only: it only navigates to the already-running server.
// Run from the repository root:  dotnet run .sofia/scripts/capture-web-sidebar.cs [url] [out-dir]
#:package Microsoft.Playwright@1.65.0

using Microsoft.Playwright;

// File-based apps disable reflection-based JSON by default; Playwright's protocol serializer needs it.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5199";
var outDir = args.Length > 1 ? args[1] : ".specs/spec_assets";
Directory.CreateDirectory(outDir);

using var playwright = await Playwright.CreateAsync();
var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
    ExecutablePath = File.Exists(chrome) ? chrome : null,
});

foreach (var (name, width, height) in new[]
         {
             ("1080p", 1920, 1080),
             ("2k", 2560, 1440),
             ("4k", 3840, 2160),
         })
{
    var page = await browser.NewPageAsync(new BrowserNewPageOptions
    {
        ViewportSize = new ViewportSize { Width = width, Height = height },
        DeviceScaleFactor = height >= 2160 ? 2 : height >= 1440 ? 1.333f : 1f,
    });
    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    await page.WaitForTimeoutAsync(1800);
    var path = Path.Combine(outDir, $"040-web-{name}.png");
    await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
    Console.WriteLine($"{path} {new FileInfo(path).Length} bytes");
    await page.CloseAsync();
}

await browser.CloseAsync();
