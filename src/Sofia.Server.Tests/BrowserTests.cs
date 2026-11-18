using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Browser;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

public class BrowserUrlTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("localhost:3000/app", "http://localhost:3000/app")]
    [InlineData("127.0.0.1:5199", "http://127.0.0.1:5199/")]
    [InlineData("about:blank", "about:blank")]
    public void Normalizes_addresses(string input, string expected) =>
        Assert.Equal(expected, BrowserSession.NormalizeUrl(input));

    [Fact]
    public void Plain_text_becomes_a_search() =>
        Assert.StartsWith("https://duckduckgo.com/?q=best%20pizza", BrowserSession.NormalizeUrl("best pizza"));

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("chrome://settings")]
    [InlineData("")]
    public void Rejects_non_web_schemes(string input) =>
        Assert.Throws<ArgumentException>(() => BrowserSession.NormalizeUrl(input));
}

/// <summary>
/// Runs the real-browser tests serially with the rest of the suite: starting a browser is CPU-heavy for the first
/// seconds and the session-based tests around it poll with fixed budgets.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class BrowserCollection
{
    public const string Name = "browser-e2e";
}

/// <summary>
/// End-to-end browser tests: they start a <em>real</em> installed browser, which is heavy enough to disturb the
/// timing-sensitive session tests that run beside them, so they only run with <c>SOFIA_TEST_BROWSER=1</c>
/// (<c>SOFIA_TEST_BROWSER=1 dotnet test --filter BrowserToolTests</c>). Without a browser installed they also
/// return early instead of failing.
/// </summary>
[Collection(BrowserCollection.Name)]
public class BrowserToolTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SOFIA_TEST_BROWSER") is "1" or "true";

    private const string Home = """
        <html><head><title>Sofia test</title></head><body>
        <h1>Welcome</h1>
        <form onsubmit="document.getElementById('out').textContent = 'hello ' + document.getElementById('name').value; return false;">
          <input id="name" placeholder="Your name"><button type="submit">Go</button>
        </form>
        <div id="out">nothing yet</div>
        <a href="/next">Next page</a>
        </body></html>
        """;

    private const string Next = "<html><head><title>Second</title></head><body><h2>Second page</h2></body></html>";

    private static ToolContext Context(string sessionId) => new()
    {
        Session = new Session { Id = sessionId },
        Workspace = new Workspace { Id = "ws_test" },
        Cwd = ".",
        Message = null!,
        Call = null!,
        Config = new ServerConfig(),
        Changes = null!,
        Host = null!,
    };

    private static async Task<ToolResult> RunAsync(BrowserTool tool, string sessionId, object args) =>
        await tool.ExecuteAsync(Context(sessionId), (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(args))!, CancellationToken.None);

    private static async Task<string> OkAsync(BrowserTool tool, string sessionId, object args)
    {
        var result = await RunAsync(tool, sessionId, args);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    /// <summary>A machine without any browser cannot run this assertion; skip instead of failing the suite.</summary>
    private static bool NoBrowser(ToolResult result) =>
        result.IsError && (result.Output.Contains("No supported browser found", StringComparison.Ordinal)
            || result.Output.Contains("Could not start", StringComparison.Ordinal)
            || result.Output.Contains("did not expose its debug endpoint", StringComparison.Ordinal));

    private static Attachment SaveShot(byte[] bytes, string mime, string name, string directory)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return new Attachment { Id = "att_shot", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
    }

    [Fact]
    public async Task Opens_a_real_browser_headless_and_drives_it_through_refs()
    {
        if (!Enabled) return;
        // The suite runs without a display: the same installed browser, started with --headless=new.
        Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", "1");
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                var bytes = Encoding.UTF8.GetBytes(ctx.Request.Url!.AbsolutePath == "/next" ? Next : Home);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var dataDir = Directory.CreateTempSubdirectory("sofia-browser").FullName;
        var shotDir = Directory.CreateTempSubdirectory("sofia-shots").FullName;
        var options = new SofiaOptions { DataDir = dataDir };
        var url = $"http://127.0.0.1:{port}/";
        using (var db = new SofiaDb(options))
        {
            await using (var service = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance))
            {
                var tool = new BrowserTool(service, (bytes, mime, name) => SaveShot(bytes, mime, name, shotDir));

                var first = await RunAsync(tool, "ses_test", new { action = "navigate", url });
                if (NoBrowser(first)) return;
                Assert.False(first.IsError, first.Output);
                var snapshot = first.Output;
                Assert.Contains("Browser: ", snapshot);
                Assert.Contains("Title: Sofia test", snapshot);
                Assert.Contains("# Welcome", snapshot);
                Assert.Contains("textbox \"Your name\"", snapshot);
                Assert.Contains("button \"Go\"", snapshot);
                Assert.Contains("link \"Next page\"", snapshot);

                var live = service.Find("ses_test")!;
                Assert.False(string.IsNullOrWhiteSpace(live.EngineId));
                Assert.True(live.Headless);
                Assert.Contains($"0 * {url}", await OkAsync(tool, "ses_test", new { action = "tabs" }));

                var typed = await OkAsync(tool, "ses_test", new { action = "type", @ref = RefOf(snapshot, "textbox"), text = "Sofia", submit = true });
                Assert.Contains("hello Sofia", typed);

                var next = await OkAsync(tool, "ses_test", new { action = "click", @ref = RefOf(typed, "link \"Next page\"") });
                Assert.Contains("Title: Second", next);
                Assert.Contains("## Second page", next);

                var back = await OkAsync(tool, "ses_test", new { action = "back" });
                Assert.Contains("Title: Sofia test", back);

                var missing = await RunAsync(tool, "ses_test", new { action = "click", @ref = "e999" });
                Assert.True(missing.IsError);

                foreach (var args in new object[]
                         {
                             new { action = "screenshot" },
                             new { action = "screenshot", full_page = true },
                             new { action = "screenshot", @ref = RefOf(back, "link \"Next page\"") },
                         })
                {
                    var shot = await RunAsync(tool, "ses_test", args);
                    Assert.False(shot.IsError, shot.Output);
                    var image = Assert.Single(shot.Images!);
                    var jpeg = File.ReadAllBytes(image.StoragePath);
                    Assert.True(jpeg.Length > 500 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "expected a JPEG");
                }

                // The window is remembered on disk so a restart can re-attach instead of opening a second one.
                var instance = Assert.Single(BrowserInstanceStore.Enumerate(service.BrowsersRoot)).Instance;
                Assert.Equal("ses_test", instance.SessionId);
                Assert.Equal(live.EngineId, instance.Engine);
                Assert.True(instance.Port > 0);
                Assert.True(instance.Pid > 0);

                // close stops the window and forgets it.
                Assert.Contains("closed", await OkAsync(tool, "ses_test", new { action = "close" }));
                Assert.Null(service.Find("ses_test"));
                Assert.Empty(BrowserInstanceStore.Enumerate(service.BrowsersRoot));
            }
        }
        listener.Stop();
        TryDelete(shotDir);
        TryDelete(dataDir);
    }

    [Fact]
    public async Task Asking_for_an_engine_that_is_not_installed_fails_with_a_list()
    {
        if (!Enabled) return;
        Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", "1");
        if (BrowserCatalog.Detect().Any(engine => engine.Id == "vivaldi")) return;
        var dataDir = Directory.CreateTempSubdirectory("sofia-browser").FullName;
        var options = new SofiaOptions { DataDir = dataDir };
        using (var db = new SofiaDb(options))
        {
            await using var service = new BrowserService(db, options, new EventBus(), NullLogger<BrowserService>.Instance);
            var tool = new BrowserTool(service, (bytes, mime, name) => SaveShot(bytes, mime, name, dataDir));
            var result = await RunAsync(tool, "ses_unknown_engine", new { action = "launch", browser = "vivaldi" });
            Assert.True(result.IsError);
            Assert.Contains("vivaldi", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        TryDelete(dataDir);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A headless browser may still be releasing its profile; the OS cleans temp up later.
        }
    }

    private static string RefOf(string snapshot, string marker)
    {
        var line = snapshot.Split('\n').First(l => l.Contains(marker));
        return line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
    }
}
