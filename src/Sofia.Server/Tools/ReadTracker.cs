using System.Security.Cryptography;
using System.Text;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Per-session read-before-edit state: which files the session has read (or written) and how they looked on disk
/// at that moment. Persisted in LiteDB (`file_reads`) so a server restart does not force re-reads, and scoped per
/// session id, so subagents track their own reads independently of the parent.
/// </summary>
public sealed class ReadTracker(SofiaDb db)
{
    /// <summary>OS-aware normalised key: full path, compared case-insensitively on Windows/macOS.</summary>
    public static string Key(string absolutePath) => PathUtil.Normalize(absolutePath);

    private static bool SameKey(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Records (or refreshes) that the session read this file; partial reads count. Pass the raw content to store its
    /// hash in the same round trip, so a later touch-only rewrite is recognised as unchanged.
    /// </summary>
    public void RecordRead(string sessionId, string absolutePath, string? rawContent = null)
    {
        try
        {
            if (!File.Exists(absolutePath)) return;
            var info = new FileInfo(absolutePath);
            Upsert(sessionId, absolutePath, info.Length, info.LastWriteTimeUtc,
                rawContent is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent))).ToLowerInvariant());
        }
        catch (IOException)
        {
            // The file vanished between the read and this call; nothing to track.
        }
    }

    /// <summary>
    /// Records that the session itself wrote the file, so a following edit does not demand a re-read. The content hash
    /// is stored too: on filesystems with coarse mtime granularity (or when the write lands in the same tick as the
    /// recorded one) the length/mtime check drifts, and the hash proves the disk still holds what we wrote.
    /// </summary>
    public void RecordWrite(string sessionId, string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return;
            var info = new FileInfo(absolutePath);
            using var fs = File.OpenRead(absolutePath);
            var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            Upsert(sessionId, absolutePath, info.Length, info.LastWriteTimeUtc, hash);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Drops the record (the file was deleted by a tool).</summary>
    public void RecordDelete(string sessionId, string absolutePath)
    {
        var record = Get(sessionId, absolutePath);
        if (record is not null) db.FileReads.Delete(record.Id);
    }

    /// <summary>Moves the record from one path to another (file move/rename by a tool).</summary>
    public void RecordMove(string sessionId, string fromPath, string toPath)
    {
        var record = Get(sessionId, fromPath);
        if (record is null)
        {
            RecordWrite(sessionId, toPath);
            return;
        }
        try
        {
            var info = File.Exists(toPath) ? new FileInfo(toPath) : null;
            UpsertRaw(record, Key(toPath), info?.Length ?? 0, info?.LastWriteTimeUtc ?? record.LastWriteUtc, null);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The read-before-edit check: null when the file may be edited, otherwise a message telling the model exactly
    /// what to do. Passes for files that never existed (new files are exempt) and for every call when the policy is off.
    /// </summary>
    public string? Check(string sessionId, string absolutePath, bool enabled, string? cwd = null)
    {
        if (!enabled) return null;
        var rel = PathUtil.Relative(cwd ?? Environment.CurrentDirectory, absolutePath);
        if (!File.Exists(absolutePath)) return null;   // new file: nothing to have read

        var record = Get(sessionId, absolutePath);
        if (record is null)
            return $"You must `read` {rel} in this conversation before editing it.";

        var info = new FileInfo(absolutePath);
        if (info.Length == record.Length && info.LastWriteTimeUtc == record.LastWriteUtc) return null;

        // Length or mtime moved: compare hashes to tell a real change from a touch-only rewrite.
        string? currentHash;
        try
        {
            using var fs = File.OpenRead(absolutePath);
            currentHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return $"{rel} changed on disk since you last read it (modified {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC). Read it again before editing.";
        }

        if (record.Sha256 is not null && currentHash == record.Sha256)
        {
            // Touch-only rewrite: refresh the fingerprint and allow the edit.
            UpsertRaw(record, record.Path, info.Length, info.LastWriteTimeUtc, record.Sha256);
            return null;
        }

        return $"{rel} changed on disk since you last read it (modified {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC). Read it again before editing.";
    }

    private void Upsert(string sessionId, string absolutePath, long length, DateTime lastWriteUtc, string? sha256) =>
        UpsertRaw(Get(sessionId, absolutePath) ?? new FileReadRecord { SessionId = sessionId }, Key(absolutePath), length, lastWriteUtc, sha256);

    private void UpsertRaw(FileReadRecord record, string key, long length, DateTime lastWriteUtc, string? sha256)
    {
        // The id is "{sessionId}|{path}": without it every record would share the empty default id and upserts collide.
        record.Id = record.SessionId + "|" + key;
        record.Path = key;
        record.Length = length;
        record.LastWriteUtc = lastWriteUtc;
        record.Sha256 = sha256;
        record.UpdatedAt = DateTime.UtcNow;
        db.FileReads.Upsert(record);
    }

    private FileReadRecord? Get(string sessionId, string absolutePath)
    {
        var key = Key(absolutePath);
        return db.FileReads.Find(r => r.SessionId == sessionId).FirstOrDefault(r => SameKey(r.Path, key));
    }
}
