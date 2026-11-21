using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Loads attachment media for the transcript. The URL comes from <see cref="Sofia.Client.ISofiaBackend.AttachmentUrl"/>
/// (which carries the access token for remote/browser sessions); an <c>avares://</c> URL is read from the app's own
/// assets, which is what the fixture uses so the harness stays server-free. Results are cached per attachment id and
/// announced through <see cref="Loaded"/> so the thumbnail repaints without a row rebuild.
/// </summary>
public sealed class AttachmentLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);

    /// <summary>Maps a wire URL to the URL a client can fetch (the backend adds the access token).</summary>
    public Func<string, string>? UrlResolver { get; set; }

    /// <summary>Raised on the UI thread once one attachment finished loading (or failed).</summary>
    public event Action<Attachment>? Loaded;

    public string SourceFor(Attachment attachment) =>
        string.IsNullOrEmpty(attachment.Url) ? "" : UrlResolver?.Invoke(attachment.Url) ?? attachment.Url;

    public Bitmap? BitmapFor(Attachment attachment)
    {
        if (_bitmaps.TryGetValue(attachment.Id, out var cached)) return cached;
        // App assets are local files: load them synchronously so a fixture thumbnail/lighbox is there on first paint.
        var source = SourceFor(attachment);
        if (source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)) return FromAsset(attachment);
        if (_loading.Contains(attachment.Id)) return null;
        _loading.Add(attachment.Id);
        _ = LoadAsync(attachment);
        return null;
    }

    public byte[]? BytesFor(Attachment attachment) => _bytes.TryGetValue(attachment.Id, out var bytes) ? bytes : null;

    /// <summary>Marks a fixture/served asset as already loaded (screenshots) so no HTTP round trip is needed.</summary>
    public Bitmap? FromAsset(Attachment attachment)
    {
        if (_bitmaps.TryGetValue(attachment.Id, out var cached)) return cached;
        try
        {
            var uri = new Uri(SourceFor(attachment));
            using var stream = AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);
            _bitmaps[attachment.Id] = bitmap;
            return bitmap;
        }
        catch
        {
            _bitmaps[attachment.Id] = null;
            return null;
        }
    }

    private async Task LoadAsync(Attachment attachment)
    {
        Bitmap? bitmap = null;
        byte[]? bytes = null;
        try
        {
            var source = SourceFor(attachment);
            if (source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var asset = AssetLoader.Open(new Uri(source));
                using var memory = new MemoryStream();
                await asset.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                bytes = await Http.GetByteArrayAsync(uri);
            }

            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
        }
        catch
        {
            bitmap = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _bitmaps[attachment.Id] = bitmap;
            _bytes[attachment.Id] = bytes;
            _loading.Remove(attachment.Id);
            Loaded?.Invoke(attachment);
        });
    }
}
