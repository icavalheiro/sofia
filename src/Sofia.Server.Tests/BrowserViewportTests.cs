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

public class BrowserViewportTests
{
    private const string Home = "<html><head><title>Sofia viewport</title></head><body><h1>hi</h1></body></html>";

    private static ToolContext Context() => new()
    {
        Session = new Session { Id = "ses_vp" },
        Workspace = new Workspace { Id = "ws_vp" },
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
    public async Task Resize_persists_across_navigate_and_screenshot_matches()
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
                var bytes = Encoding.UTF8.GetBytes(Home);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        await using var service = new BrowserService(new EventBus(), NullLogger<BrowserService>.Instance);
        var shotDir = Directory.CreateTempSubdirectory("sofia-vp-shots");
        var tool = new BrowserTool(service, (bytes, mime, name) =>
        {
            var path = Path.Combine(shotDir.FullName, name);
            File.WriteAllBytes(path, bytes);
            return new Attachment { Id = "att_shot", Name = name, Mime = mime, Kind = "image", Bytes = bytes.Length, Size = bytes.Length + " B", StoragePath = path };
        });

        var url = $"http://127.0.0.1:{port}/";
        var first = await RunAsync(tool, new { action = "navigate", url });
        if (first.IsError && first.Output.Contains("Could not start a headless browser", StringComparison.Ordinal))
        {
            listener.Stop();
            shotDir.Delete(recursive: true);
            return;
        }
        Assert.False(first.IsError, first.Output);

        var resized = await OkAsync(tool, new { action = "resize", width = 180, height = 180 });
        Assert.Contains("Resized browser to 180x180", resized);

        var snap1 = await OkAsync(tool, new { action = "snapshot" });
        Assert.Contains("Viewport 180x180", snap1);

        var nav2 = await OkAsync(tool, new { action = "navigate", url });
        Assert.Contains("Navigated", nav2);

        var snap2 = await OkAsync(tool, new { action = "snapshot" });
        Assert.Contains("Viewport 180x180", snap2);

        var shot = await RunAsync(tool, new { action = "screenshot" });
        Assert.False(shot.IsError, shot.Output);
        Assert.Contains("Path: ", shot.Output);
        var image = Assert.Single(shot.Images!);
        Assert.True(File.Exists(image.StoragePath), image.StoragePath);
        var jpeg = File.ReadAllBytes(image.StoragePath);
        var (w, h) = JpegDimensions(jpeg);
        Assert.Equal(180, w);
        Assert.Equal(180, h);

        shotDir.Delete(recursive: true);
        listener.Stop();
    }

    private static (int Width, int Height) JpegDimensions(byte[] bytes)
    {
        // Find a SOF marker (0xFF 0xC0..0xCF except 0xC4,0xC8,0xCC) and read height/width.
        for (var i = 0; i < bytes.Length - 9; i++)
        {
            if (bytes[i] != 0xFF) continue;
            var marker = bytes[i + 1];
            if (marker < 0xC0 || marker > 0xCF || marker == 0xC4 || marker == 0xC8 || marker == 0xCC) continue;
            // bytes[i+2..i+3] length, i+4 precision, i+5..i+6 height, i+7..i+8 width
            var height = (bytes[i + 5] << 8) | bytes[i + 6];
            var width = (bytes[i + 7] << 8) | bytes[i + 8];
            return (width, height);
        }
        throw new InvalidOperationException("JPEG SOF marker not found.");
    }
}