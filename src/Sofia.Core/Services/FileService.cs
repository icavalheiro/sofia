using Sofia.Contracts.Domain;
using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Services;

/// <summary>Project file listing for the <c>@</c> mention picker.</summary>
public sealed class FileService
{
    /// <summary>Upper bound of the listing: a huge monorepo must not stall the picker.</summary>
    public const int MaxFiles = 2_000;

    /// <summary>Every file under <paramref name="directory"/>, relative and `/`-separated, sorted.</summary>
    public List<string> List(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [];
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0 && results.Count < MaxFiles)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files)
            {
                if (results.Count >= MaxFiles) break;
                var rel = Path.GetRelativePath(directory, f).Replace('\\', '/');
                if (!results.Contains(rel, StringComparer.Ordinal)) results.Add(rel);
            }
            foreach (var d in dirs)
            {
                if (PathUtil.IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                // Skip hidden folders (dot-prefixed) to keep the list focused on project files.
                if (Path.GetFileName(d).StartsWith('.')) continue;
                stack.Push(d);
            }
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }
}

/// <summary>Stores uploaded and tool-produced media under <c>&lt;dataDir&gt;/attachments</c> and serves it back.</summary>
public sealed class AttachmentService(SofiaDb db, SofiaOptions options)
{
    /// <summary>Saves an upload stream; the id is generated here so the caller only supplies the metadata.</summary>
    public async Task<Attachment> SaveAsync(Stream content, string fileName, string? contentType, long length, CancellationToken ct)
    {
        var id = Ids.New("att");
        var name = Path.GetFileName(fileName);
        var storage = Path.Combine(options.AttachmentsDir, id + Path.GetExtension(name));
        await using (var stream = File.Create(storage)) await content.CopyToAsync(stream, ct);
        var mime = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        var attachment = new Attachment
        {
            Id = id,
            Name = name,
            Mime = mime,
            Kind = KindOf(mime),
            Bytes = length,
            Size = Text.HumanSize(length),
            Url = $"/api/attachments/{id}",
            StoragePath = storage,
        };
        db.Attachments.Insert(attachment);
        return attachment;
    }

    /// <summary>Saves bytes already in memory (a screenshot, an image read from disk, a rendered sound).</summary>
    public Attachment SaveBytes(byte[] bytes, string mime, string name)
    {
        var id = Ids.New("att");
        var storage = Path.Combine(options.AttachmentsDir, id + Path.GetExtension(name));
        File.WriteAllBytes(storage, bytes);
        var attachment = new Attachment
        {
            Id = id,
            Name = name,
            Mime = mime,
            Kind = KindOf(mime),
            Bytes = bytes.Length,
            Size = Text.HumanSize(bytes.Length),
            Url = $"/api/attachments/{id}",
            StoragePath = storage,
        };
        db.Attachments.Insert(attachment);
        return attachment;
    }

    public Attachment Get(string id) => db.Attachments.FindById(id) ?? throw new NotFoundException("Attachment not found.");

    private static string KindOf(string mime) =>
        mime.StartsWith("image/") ? "image" : mime.StartsWith("video/") ? "video" : mime.StartsWith("audio/") ? "audio" : "file";
}
