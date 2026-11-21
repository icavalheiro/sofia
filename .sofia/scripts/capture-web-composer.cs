// Spec 042 evidence: captures the running Vue UI's composer at the epic's three sizes so the Avalonia port can be
// compared side by side. Read-only: it only navigates to the already-running server (no prompt, no session write).
// Run from the repository root:  dotnet run .sofia/scripts/capture-web-composer.cs [url] [out-dir]
#:package Microsoft.Playwright@1.65.0

using Microsoft.Playwright;

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

foreach (var (name, width, height, scale) in new[]
         {
             ("1080p", 1920, 1080, 1.0),
             ("2k", 2560, 1440, 1.333),
             ("4k", 3840, 2160, 2.0),
         })
{
    var page = await browser.NewPageAsync(new BrowserNewPageOptions
    {
        ViewportSize = new ViewportSize { Width = width, Height = height },
        DeviceScaleFactor = (float)scale,
    });
    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    await page.WaitForTimeoutAsync(1800);

    // Full frame once (1080p) for the transcript/composer relationship, plus the composer band on its own.
    if (name == "1080p")
    {
        var full = Path.Combine(outDir, "042-web-1080p.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = full });
        Console.WriteLine($"{full} {new FileInfo(full).Length} bytes");
    }

    var composer = await page.Locator("footer.composer").First.BoundingBoxAsync();
    if (composer is null)
    {
        Console.WriteLine("composer not found");
        continue;
    }

    var clip = new Clip
    {
        X = 0,
        Y = Math.Max(0, composer.Y - 260),
        Width = width,
        Height = Math.Min(height - Math.Max(0, composer.Y - 260), composer.Height + 260),
    };
    var path = Path.Combine(outDir, $"042-web-composer-{name}.png");
    await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Clip = clip });
    Console.WriteLine($"{path} {new FileInfo(path).Length} bytes clip={clip.Y:0}+{clip.Height:0}");
    await page.CloseAsync();
}

await browser.CloseAsync();
