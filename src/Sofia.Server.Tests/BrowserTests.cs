using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Browser;
using Sofia.Server.Domain;
using Sofia.Server.Events;
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

/// <summary>Drives a real headless browser against a tiny local site. Passes without asserting when no Chrome/Edge/Chromium can start.</summary>
public class BrowserToolTests
{
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

    private static ToolContext Context() => new()
    {
        Session = new Session { Id = "ses_test" },
        Workspace = new Workspace { Id = "ws_test" },
        Cwd = ".",
        Message = null!,
        Call = null!,
        Config = new ServerConfig(),
        Changes = null!,
        Host = null!,
    };

    private static async Task<ToolResult> RunAsync(BrowserTool tool, object args) =>
        await tool.ExecuteAsync(Context(), (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(args))!, CancellationToken.None);

    private static async Task<string> OkAsync(BrowserTool tool, object args)
    {
        var result = await RunAsync(tool, args);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    [Fact]
    public async Task Reads_clicks_and_types_through_refs_and_streams_frames()
    {
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
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        await using var service = new BrowserService(new EventBus(), NullLogger<BrowserService>.Instance);
        var shotDir = Directory.CreateTempSubdirectory("sofia-shots");
        var tool = new BrowserTool(service, (bytes, mime, name) =>
        {
            var path = Path.Combine(shotDir.FullName, name);
            File.WriteAllBytes(path, bytes);
            return new Attachment { Id = "att_shot", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
        });
        var first = await RunAsync(tool, new { action = "navigate", url = $"http://127.0.0.1:{port}/" });
        if (first.IsError && first.Output.Contains("Could not start a headless browser", StringComparison.Ordinal)) return;
        Assert.False(first.IsError, first.Output);
        var snapshot = first.Output;
        Assert.Equal($"0 * http://127.0.0.1:{port}/", await OkAsync(tool, new { action = "tabs" }));

        Assert.Contains("Title: Sofia test", snapshot);
        Assert.Contains("# Welcome", snapshot);
        Assert.Contains("textbox \"Your name\"", snapshot);
        Assert.Contains("button \"Go\"", snapshot);
        Assert.Contains("link \"Next page\"", snapshot);

        var live = service.Find("ses_test")!;
        var viewer = await live.AddViewerAsync();
        var gotFrame = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in viewer.Wake.ReadAllAsync(timeout.Token))
        {
            var (_, _, frame) = viewer.Take();
            if (frame is { Length: > 100 })
            {
                gotFrame = true;
                break;
            }
        }
        Assert.True(gotFrame, "expected a screencast frame");

        var typed = await OkAsync(tool, new { action = "type", @ref = RefOf(snapshot, "textbox"), text = "Sofia", submit = true });
        Assert.Contains("hello Sofia", typed);

        var next = await OkAsync(tool, new { action = "click", @ref = RefOf(typed, "link \"Next page\"") });
        Assert.Contains("Title: Second", next);
        Assert.Contains("## Second page", next);

        var back = await OkAsync(tool, new { action = "back" });
        Assert.Contains("Title: Sofia test", back);

        var missing = await RunAsync(tool, new { action = "click", @ref = "e999" });
        Assert.True(missing.IsError);

        foreach (var args in new object[] { new { action = "screenshot" }, new { action = "screenshot", full_page = true }, new { action = "screenshot", @ref = RefOf(back, "link \"Next page\"") } })
        {
            var shot = await RunAsync(tool, args);
            Assert.False(shot.IsError, shot.Output);
            var image = Assert.Single(shot.Images!);
            var jpeg = File.ReadAllBytes(image.StoragePath);
            Assert.True(jpeg.Length > 500 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "expected a JPEG");
        }
        shotDir.Delete(recursive: true);

        await live.RemoveViewerAsync(viewer);
        await service.CloseAsync("ses_test");
        Assert.Null(service.Find("ses_test"));
    }

    private static string RefOf(string snapshot, string marker)
    {
        var line = snapshot.Split('\n').First(l => l.Contains(marker));
        return line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
    }
}
