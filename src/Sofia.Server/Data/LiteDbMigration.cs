using System.Text;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sofia.Server.Domain;

namespace Sofia.Server.Data;

/// <summary>
/// One-shot migration of a legacy LiteDB file into the SQLite store, run before the first connection is opened.
///
/// Safety rules (learned the hard way — a silent skip made a 725 MB history look empty):
/// <list type="bullet">
///   <item>a legacy file that exists but cannot be read is an error, never a reason to start empty;</item>
///   <item>a SQLite store left behind empty by an earlier failed attempt is replaced by the import;</item>
///   <item>a SQLite store that already holds conversations is never overwritten, the app refuses to start;</item>
///   <item>the source is only renamed after the import was committed and verified row by row.</item>
/// </list>
///
/// LiteDB is kept as a package only for this: delete this file (and the package reference) once migrating from 0.x
/// data is no longer supported (1.0).
/// </summary>
internal static class LiteDbMigration
{
    private const int BatchSize = 500;

    /// <summary>Fragment of the LiteDB 5 file header, found near the start of the first page.</summary>
    private const string LiteDbMagic = "LiteDB file";

    public static void RunIfNeeded(string databasePath, ILogger? log)
    {
        if (!IsFileDatabase(databasePath)) return;

        var full = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(full) ?? ".";
        var marker = full + ".migrated";

        // Two shapes of legacy data: the configured file is still LiteDB (an upgrade in place), or the `sofia.db`
        // from before the filename change sits next to the new `sofia.sqlite`.
        var sibling = Path.Combine(directory, "sofia.db");
        string? source = null;
        foreach (var candidate in new[] { full, sibling })
        {
            if (!File.Exists(candidate)) continue;
            if (Probe(candidate) == LegacyFile.NotLiteDb) continue;
            source = candidate;
            break;
        }

        if (source is null)
        {
            log?.LogInformation("No legacy LiteDB store next to {Path}; using the SQLite database", full);
            return;
        }

        if (File.Exists(marker))
        {
            log?.LogInformation("Legacy LiteDB store {Source} was imported earlier ({Marker}); ignoring it", source, marker);
            return;
        }

        // Two instances started at once must not import into the same target: the loser would fail on duplicate keys
        // (or worse, leave a half-written store). The file lock makes the loser stop with a clear message instead.
        var lockPath = full + ".migration.lock";
        FileStream gate;
        try
        {
            gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"Another Sofia instance is already importing '{source}' into SQLite ({lockPath}). Nothing was modified; " +
                "wait for it to finish and start again.");
        }

        try
        {
            Import(source, full, marker, log);
        }
        finally
        {
            gate.Dispose();
            TryDelete(lockPath);
        }
    }

    private static void Import(string source, string full, string marker, ILogger? log)
    {
        // Re-check under the lock: another instance may have finished the import while this one was starting.
        if (File.Exists(marker))
        {
            log?.LogInformation("Legacy LiteDB store {Source} was imported by another instance; using {Target}", source, full);
            return;
        }

        // An in-place upgrade has no separate target to protect; everything else must not clobber conversations.
        if (!samePath(source, full))
        {
            var target = InspectTarget(full);
            if (target == TargetStore.HasConversations)
                throw new InvalidOperationException(
                    $"Sofia found the legacy LiteDB database '{source}' but the SQLite database '{full}' already contains " +
                    "conversations created by this version. Nothing was changed. Move or rename the SQLite database " +
                    "(and its -wal/-shm files) away to import the legacy data instead, or delete the legacy file to keep the new one.");
            if (target == TargetStore.Unreadable)
                throw new InvalidOperationException(
                    $"Sofia found the legacy LiteDB database '{source}' but could not read the SQLite database '{full}' " +
                    "(it may be open in another Sofia instance). Nothing was changed; close the other instance and start again.");
        }

        var staging = full + ".migrating";
        TryDelete(staging);
        var megabytes = new FileInfo(source).Length / (1024.0 * 1024.0);
        log?.LogInformation("Importing the legacy LiteDB store {Source} ({Size:F0} MB) into SQLite {Target}; this runs once",
            source, megabytes, full);
        try
        {
            Migrate(source, staging, log);

            // Keep the LiteDB file as a backup, then put the imported database in place.
            if (samePath(source, full))
            {
                var inPlaceBackup = full + ".litedb.migrated";
                TryDelete(inPlaceBackup);
                File.Move(full, inPlaceBackup);
                File.Move(staging, full);
                log?.LogInformation("LiteDB store kept as {Backup}", inPlaceBackup);
            }
            else
            {
                // An empty SQLite store from an earlier failed start is replaced by the imported data.
                DeleteStore(full);
                File.Move(staging, full);
                var backup = source + ".migrated";
                TryDelete(backup);
                File.Move(source, backup);
                log?.LogInformation("LiteDB store kept as {Backup}", backup);
            }
            File.WriteAllText(marker,
                $"Imported from {source} to SQLite on {DateTime.UtcNow:O}. Keep the backup for one release cycle, then delete it.{Environment.NewLine}",
                Encoding.UTF8);
            log?.LogInformation("Legacy store imported; {Target} is now the active database ({Marker})", full, marker);
        }
        catch (Exception ex)
        {
            TryDelete(staging);
            TryDelete(staging + "-wal");
            TryDelete(staging + "-shm");
            log?.LogError(ex, "LiteDB migration from {Source} failed; the original file was left untouched", source);
            throw new InvalidOperationException(
                $"Could not import the LiteDB database '{source}' into SQLite. The original file was left untouched. " +
                "If another Sofia instance is running, close it first (it keeps the database open) and start again.", ex);
        }
    }

    private static void Migrate(string source, string target, ILogger? log)
    {
        // Same mapper settings the old store wrote with, so empty strings stay empty instead of turning into null.
        var mapper = new BsonMapper { SerializeNullValues = false, EmptyStringToNull = false, TrimWhitespace = false };
        using var lite = new LiteDatabase(
            new ConnectionString { Filename = source, ReadOnly = true, Connection = ConnectionType.Direct }, mapper);

        var options = new DbContextOptionsBuilder<SofiaDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString())
            .Options;
        using var ctx = new SofiaDbContext(options);
        ctx.Database.EnsureCreated();
        using var transaction = ctx.Database.BeginTransaction();

        var counts = new Dictionary<string, int>
        {
            ["workspaces"] = Copy(lite.GetCollection<Workspace>("workspaces"), ctx.Workspaces, ctx, log),
            ["sessions"] = Copy(lite.GetCollection<Session>("sessions"), ctx.Sessions, ctx, log),
            ["messages"] = Copy(lite.GetCollection<Message>("messages"), ctx.Messages, ctx, log),
            ["attachments"] = Copy(lite.GetCollection<Attachment>("attachments"), ctx.Attachments, ctx, log),
            ["snapshots"] = Copy(lite.GetCollection<FileSnapshot>("snapshots"), ctx.Snapshots, ctx, log),
            ["file_reads"] = Copy(lite.GetCollection<FileReadRecord>("file_reads"), ctx.FileReads, ctx, log),
            ["providers"] = Copy(lite.GetCollection<ProviderConfig>("providers"), ctx.Providers, ctx, log),
            ["mcp_servers"] = Copy(lite.GetCollection<McpServerConfig>("mcp"), ctx.McpServers, ctx, log),
            ["tokens"] = Copy(lite.GetCollection<AccessToken>("tokens"), ctx.Tokens, ctx, log),
            ["server_config"] = Copy(lite.GetCollection<ServerConfig>("config"), ctx.Config, ctx, log),
            ["knowledge"] = Copy(lite.GetCollection<KnowledgeNote>("knowledge"), ctx.Knowledge, ctx, log),
            ["catalog"] = Copy(lite.GetCollection<CatalogCache>("catalog"), ctx.Catalog, ctx, log),
            ["memory"] = Copy(lite.GetCollection<MemoryEntry>("memory"), ctx.Memory, ctx, log),
            ["tasks"] = Copy(lite.GetCollection<TaskItem>("tasks"), ctx.Tasks, ctx, log),
        };
        transaction.Commit();
        Verify(target, counts);
        log?.LogInformation("LiteDB import: {Counts}", string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    /// <summary>Streams one legacy collection into its table in batches, so a large history does not have to fit in memory.</summary>
    private static int Copy<T>(ILiteCollection<T> collection, DbSet<T> set, SofiaDbContext ctx, ILogger? log) where T : class
    {
        var copied = 0;
        foreach (var batch in collection.FindAll().Chunk(BatchSize))
        {
            set.AddRange(batch);
            ctx.SaveChanges();
            // Detach after every batch: the full history must not stay tracked by one context.
            ctx.ChangeTracker.Clear();
            copied += batch.Length;
            if (copied % 5_000 == 0) log?.LogInformation("LiteDB import: {Count} {Entity} records copied", copied, typeof(T).Name);
        }
        return copied;
    }

    /// <summary>Guards against an "imported nothing" success: every table must hold what the copy reported.</summary>
    private static void Verify(string target, IReadOnlyDictionary<string, int> counts)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        connection.Open();
        foreach (var (table, expected) in counts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}";
            var actual = Convert.ToInt32(command.ExecuteScalar());
            if (actual != expected)
                throw new InvalidOperationException($"Import verification failed: table '{table}' holds {actual} rows, expected {expected}.");
        }
    }

    private enum LegacyFile { NotLiteDb, Readable }

    private enum TargetStore { Missing, Empty, HasConversations, Unreadable }

    /// <summary>
    /// Classifies a candidate legacy file. A file that is there but open for writing (a running 0.x server) is an
    /// error, not a "not legacy" answer: skipping it silently is what left a real history behind an empty store.
    /// </summary>
    private static LegacyFile Probe(string path)
    {
        if (!File.Exists(path)) return LegacyFile.NotLiteDb;
        string header;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            header = Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            throw InUse(path);
        }
        if (!header.Contains(LiteDbMagic, StringComparison.Ordinal)) return LegacyFile.NotLiteDb;

        // The header is readable, but a writer attached to the file (a running 0.x server) would give us a torn
        // import and its own share mode is what would make LiteDB fail later with a confusing error.
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            throw InUse(path);
        }
        return LegacyFile.Readable;
    }

    private static InvalidOperationException InUse(string path) =>
        new($"The legacy LiteDB database '{path}' is in use by another process (another Sofia instance is probably " +
            "still running). Close it and start this build again; nothing was modified.");

    /// <summary>What an existing SQLite target contains; conversations are the line that must not be crossed.</summary>
    private static TargetStore InspectTarget(string path)
    {
        if (!File.Exists(path)) return TargetStore.Missing;
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM messages";
            return Convert.ToInt64(command.ExecuteScalar()) > 0 ? TargetStore.HasConversations : TargetStore.Empty;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // "no such table": a new/empty SQLite file (an earlier start that skipped the import).
            return TargetStore.Empty;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return TargetStore.Unreadable;
        }
    }

    private static void DeleteStore(string path)
    {
        // File databases keep pooled connections alive after the contexts are gone; drop them so the (empty) store
        // can be replaced. This runs at startup, before anything else opens the imported database.
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The empty SQLite database '{file}' could not be replaced (is another Sofia instance using it?). " +
                    "Close it and start again; the legacy data was not touched.", ex);
            }
        }
    }

    private static bool IsFileDatabase(string path) =>
        !string.IsNullOrWhiteSpace(path) && path != ":memory:" && path != ":temp:" && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static bool samePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
