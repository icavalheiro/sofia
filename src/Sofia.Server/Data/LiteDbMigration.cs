using System.Text;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sofia.Server.Domain;

namespace Sofia.Server.Data;

/// <summary>
/// One-shot migration of a legacy LiteDB file into the SQLite store. It runs before the first connection is opened,
/// never touches the source on failure, and is idempotent: the source is renamed to <c>*.migrated</c> and a marker is
/// left next to the SQLite database.
///
/// LiteDB is kept as a package only for this: delete this file (and the package reference) once migrating from 0.x
/// data is no longer supported (1.0).
/// </summary>
internal static class LiteDbMigration
{
    private const int BatchSize = 500;

    public static void RunIfNeeded(string databasePath, ILogger? log)
    {
        if (!IsFileDatabase(databasePath)) return;

        var full = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var marker = full + ".migrated";
        if (File.Exists(marker)) return;

        // Two shapes of legacy data: the target itself is still a LiteDB file (an upgrade in place), or the sibling
        // LiteDB file from before the filename change (`sofia.db` next to `sofia.sqlite`).
        var source = IsLiteDbFile(full) ? full
            : IsLiteDbFile(Path.Combine(dir, "sofia.db")) ? Path.Combine(dir, "sofia.db")
            : null;
        if (source is null) return;
        if (!string.Equals(source, full, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return; // never overwrite SQLite data

        var staging = full + ".migrating";
        TryDelete(staging);
        log?.LogInformation("Migrating the LiteDB database {Source} to SQLite {Target}; this runs once", source, full);
        try
        {
            Migrate(source, staging, log);
            File.Move(staging, full, overwrite: true);
            var backup = string.Equals(source, full, StringComparison.OrdinalIgnoreCase) ? full + ".litedb.migrated" : source + ".migrated";
            TryDelete(backup);
            File.Move(source, backup);
            File.WriteAllText(marker, $"Migrated from {source} to SQLite on {DateTime.UtcNow:O}. Keep the backup for one release cycle, then delete it.{Environment.NewLine}", Encoding.UTF8);
            log?.LogInformation("LiteDB migration finished; the original was kept as {Backup}", backup);
        }
        catch (Exception ex)
        {
            TryDelete(staging);
            log?.LogError(ex, "LiteDB migration from {Source} failed; nothing was changed", source);
            throw new InvalidOperationException(
                $"Could not migrate the LiteDB database '{source}' to SQLite. The original file was left untouched; " +
                "move it away to start with an empty database, or report the error above.", ex);
        }
    }

    private static void Migrate(string source, string target, ILogger? log)
    {
        // Same mapper settings the old store wrote with, so empty strings stay empty instead of turning into null.
        var mapper = new BsonMapper { SerializeNullValues = false, EmptyStringToNull = false, TrimWhitespace = false };
        using var lite = new LiteDatabase(new ConnectionString { Filename = source, ReadOnly = true, Connection = ConnectionType.Direct }, mapper);

        var options = new DbContextOptionsBuilder<SofiaDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString())
            .Options;
        using var ctx = new SofiaDbContext(options);
        ctx.Database.EnsureCreated();
        using var transaction = ctx.Database.BeginTransaction();

        var counts = new List<string>
        {
            $"workspaces={Copy(lite.GetCollection<Workspace>("workspaces"), ctx.Workspaces, ctx, log)}",
            $"sessions={Copy(lite.GetCollection<Session>("sessions"), ctx.Sessions, ctx, log)}",
            $"messages={Copy(lite.GetCollection<Message>("messages"), ctx.Messages, ctx, log)}",
            $"attachments={Copy(lite.GetCollection<Attachment>("attachments"), ctx.Attachments, ctx, log)}",
            $"snapshots={Copy(lite.GetCollection<FileSnapshot>("snapshots"), ctx.Snapshots, ctx, log)}",
            $"file_reads={Copy(lite.GetCollection<FileReadRecord>("file_reads"), ctx.FileReads, ctx, log)}",
            $"providers={Copy(lite.GetCollection<ProviderConfig>("providers"), ctx.Providers, ctx, log)}",
            $"mcp={Copy(lite.GetCollection<McpServerConfig>("mcp"), ctx.McpServers, ctx, log)}",
            $"tokens={Copy(lite.GetCollection<AccessToken>("tokens"), ctx.Tokens, ctx, log)}",
            $"config={Copy(lite.GetCollection<ServerConfig>("config"), ctx.Config, ctx, log)}",
            $"knowledge={Copy(lite.GetCollection<KnowledgeNote>("knowledge"), ctx.Knowledge, ctx, log)}",
            $"catalog={Copy(lite.GetCollection<CatalogCache>("catalog"), ctx.Catalog, ctx, log)}",
            $"memory={Copy(lite.GetCollection<MemoryEntry>("memory"), ctx.Memory, ctx, log)}",
            $"tasks={Copy(lite.GetCollection<TaskItem>("tasks"), ctx.Tasks, ctx, log)}",
        };
        transaction.Commit();
        log?.LogInformation("LiteDB migration: {Counts}", string.Join(", ", counts));
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
            if (copied % 5_000 == 0) log?.LogInformation("LiteDB migration: {Count} {Entity} records copied", copied, typeof(T).Name);
        }
        return copied;
    }

    private static bool IsFileDatabase(string path) =>
        !string.IsNullOrWhiteSpace(path) && path != ":memory:" && path != ":temp:" && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static bool IsLiteDbFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var header = new byte[64];
            var read = stream.Read(header, 0, header.Length);
            return Encoding.ASCII.GetString(header, 0, read).Contains("LiteDB", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
