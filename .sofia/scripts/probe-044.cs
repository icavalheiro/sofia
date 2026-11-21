#:package Microsoft.Playwright@1.65.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Spec 044 browser probes: localStorage persistence, WebAudio playback counter, matchMedia reduced motion and the
// 401 → pairing flow. Usage: dotnet run .sofia/scripts/probe-044.cs -- <url> [pairingCode]

using Microsoft.Playwright;

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5301/";
var pairingCode = args.Length > 1 ? args[1] : null;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Channel = "chrome",
    Headless = true,
    Args = ["--no-sandbox", "--use-gl=swiftshader", "--enable-unsafe-swiftshader", "--autoplay-policy=no-user-gesture-required"],
});
var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
    ReducedMotion = ReducedMotion.Reduce,
});
var page = await context.NewPageAsync();
var logs = new List<string>();
page.Console += (_, message) => logs.Add($"[{message.Type}] {message.Text}");
page.PageError += (_, error) => logs.Add($"[pageerror] {error}");

// Count WebAudio buffer sources: a click sound proves the same six WAVs reach the browser audio graph.
await page.AddInitScriptAsync(@"
window.__sofiaSounds = 0;
window.__sofiaAudio = 0;
const originalCreate = AudioContext.prototype.createBufferSource;
AudioContext.prototype.createBufferSource = function () {
    window.__sofiaSounds++;
    return originalCreate.call(this);
};
const OriginalAudio = window.AudioContext;
window.AudioContext = function (...args) { window.__sofiaAudio++; return new OriginalAudio(...args); };
window.AudioContext.prototype = OriginalAudio.prototype;
");
await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
await page.WaitForSelectorAsync("#out canvas", new PageWaitForSelectorOptions { Timeout = 40000 });
await page.WaitForTimeoutAsync(3000);

Console.WriteLine("reduced motion: " + await page.EvaluateAsync<bool>("() => matchMedia('(prefers-reduced-motion: reduce)').matches"));
Console.WriteLine("audio contexts: " + await page.EvaluateAsync<int>("() => window.__sofiaAudio"));

// Type into the composer (draft autosave on session switch) and press "New chat" (plays the click sound).
await page.Mouse.ClickAsync(960, 1016);
await page.Keyboard.TypeAsync("hello browser");
await page.WaitForTimeoutAsync(1200);
await page.Mouse.ClickAsync(74, 132); // "New chat" button
await page.WaitForTimeoutAsync(1500);
Console.WriteLine("sound sources: " + await page.EvaluateAsync<int>("() => window.__sofiaSounds"));

var storage = await page.EvaluateAsync<string>(
    "() => JSON.stringify(Object.fromEntries(Object.keys(localStorage).map(k => [k, localStorage.getItem(k)?.slice(0, 120)])))");
Console.WriteLine("localStorage: " + storage);

if (pairingCode is { Length: > 0 })
{
    var gateShot = Path.GetFullPath(Path.Combine(".specs", "spec_assets", "044-browser-pairing.png"));
    await page.ScreenshotAsync(new PageScreenshotOptions { Path = gateShot });
    Console.WriteLine("pairing gate screenshot: " + gateShot);

    var paired = await page.EvaluateAsync<string>(
        "(code) => fetch('/api/pair/connect', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code }) })" +
        ".then(r => r.text().then(t => r.status + ' ' + t.slice(0, 160)))", pairingCode);
    Console.WriteLine("pair/connect: " + paired);
    var workspaces = await page.EvaluateAsync<string>(
        "() => fetch('/api/workspaces').then(r => r.text().then(t => r.status + ' ' + t.slice(0, 80)))");
    Console.WriteLine("workspaces without token: " + workspaces);
    if (paired.Length > 3)
    {
        var token = paired[(paired.IndexOf('{'))..];
        Console.WriteLine("workspaces with token: " + await page.EvaluateAsync<string>(
            "(raw) => { const body = JSON.parse(raw); return fetch('/api/workspaces', { headers: { authorization: 'Bearer ' + body.token } })" +
            ".then(r => r.text().then(t => r.status + ' ' + t.slice(0, 80))); }", token));
    }
}

var screenshot = Path.GetFullPath(Path.Combine(".specs", "spec_assets", "044-browser-probe.png"));
Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot });
Console.WriteLine("screenshot: " + screenshot);
foreach (var line in logs.TakeLast(30)) Console.WriteLine(line);
await context.CloseAsync();
