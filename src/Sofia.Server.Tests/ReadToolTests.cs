using Sofia.Server.Domain;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

public class ReadToolTests
{
    private static ToolContext Context(string cwd) => new()
    {
        Session = new Session { Id = "s", WorkspaceId = "ws", Agent = "build" }, Workspace = new Workspace { Id = "ws", Kind = "folder" }, Cwd = cwd,
        Message = null!, Call = null!, Config = null!, Changes = null!, Host = null!,
    };

    private static Attachment Save(byte[] bytes, string mime, string name) =>
        new() { Id = "att", Name = name, Mime = mime, Kind = mime.Split('/')[0], Bytes = bytes.Length, Size = $"{bytes.Length} B" };

    [Fact]
    public async Task ReturnsImagesAsAttachmentsEvenWithAWrongExtension()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
            await File.WriteAllBytesAsync(Path.Combine(dir, "shot.dat"), png);

            var result = await new ReadTool(Save).ExecuteAsync(Context(dir), Args.Parse("""{"path":"shot.dat"}"""), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("image/png", Assert.Single(result.Images!).Mime);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("a.dat", "audio", "audio/wav", "RIFF\0\0\0\0WAVEfmt ")]
    [InlineData("a.dat", "video", "video/x-msvideo", "RIFF\0\0\0\0AVI LIST")]
    [InlineData("a.bin", "audio", "audio/mpeg", "ID3\u0004\0\0\0\0\0\0")]
    [InlineData("a.mp3", "audio", "audio/mpeg", "\u00FF\u00FB\u0090\0\0\0\0\0")]
    [InlineData("a.m4a", "audio", "audio/mp4", "\0\0\0\u0020ftypisom")]
    [InlineData("a.mp4", "video", "video/mp4", "\0\0\0\u0020ftypisom")]
    [InlineData("a.mov", "video", "video/quicktime", "\0\0\0\u0014ftypqt  ")]
    [InlineData("a.webm", "video", "video/webm", "\u001A\u0045\u00DF\u00A3\0\0\0\0")]
    [InlineData("a.ogg", "audio", "audio/ogg", "OggS\0\u0002\0\0")]
    [InlineData("a.flac", "audio", "audio/flac", "fLaC\0\0\0\u0022")]
    public async Task ReturnsAudioAndVideoAsAttachments(string name, string kind, string mime, string header)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, name), header.Select(c => (byte)c).ToArray());

            var result = await new ReadTool(Save).ExecuteAsync(Context(dir), Args.Parse($$"""{"path":"{{name}}"}"""), CancellationToken.None);

            Assert.False(result.IsError);
            var media = Assert.Single(result.Images!);
            Assert.Equal(mime, media.Mime);
            Assert.Equal(kind, media.Kind);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Utf16TextWithAFrameSyncLookalikeStaysText()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "notes.txt"), [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]);

            var result = await new ReadTool(Save).ExecuteAsync(Context(dir), Args.Parse("""{"path":"notes.txt"}"""), CancellationToken.None);

            Assert.Null(result.Images);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FailsForImagesWhenNoImageStoreIsAvailable()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "a.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);

            var result = await new ReadTool().ExecuteAsync(Context(dir), Args.Parse("""{"path":"a.jpg"}"""), CancellationToken.None);

            Assert.True(result.IsError);
        }
        finally { Directory.Delete(dir, true); }
    }
}
