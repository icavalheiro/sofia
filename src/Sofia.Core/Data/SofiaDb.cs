using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Sofia.Contracts.Domain;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Data;

/// <summary>
/// SQLite store (EF Core). One shared instance; every call takes a pooled <see cref="SofiaDbContext"/>, reads push
/// down to SQL and writes are serialized behind a single gate (SQLite has one writer).
/// </summary>
public sealed class SofiaDb : IDisposable
{
    private const string TemporaryPath = ":memory:";

    private readonly DbContextOptions<SofiaDbContext> _options;
    private readonly PooledDbContextFactory<SofiaDbContext> _contexts;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger<SofiaDb>? _log;
    private readonly SqliteConnection? _keeper;
    private readonly bool _pooling;
    private DateTime _lastCompactUtc = DateTime.MinValue;

    /// <summary>Name of the message-ordering counter in the <c>counters</c> table.</summary>
    private const string MessageSeqCounter = "message_seq";

    public SofiaDb(SofiaOptions options, ILogger<SofiaDb>? log = null) : this(options.DbPath, log) { }

    /// <param name="path">SQLite file path; <c>:memory:</c> gives a private in-memory database (tests).</param>
    public SofiaDb(string path, ILogger<SofiaDb>? log = null)
    {
        _log = log;
        string connectionString;
        if (string.IsNullOrWhiteSpace(path) || path == TemporaryPath || path == ":temp:")
        {
            // A shared-cache memory database: the keeper connection owns it, every pooled context sees the same
            // schema and rows, and disposing the keeper erases it (no files in %TEMP%).
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"file:sofia-{Guid.NewGuid():N}?mode=memory&cache=shared",
                Pooling = false,
            }.ToString();
            _keeper = new SqliteConnection(connectionString);
            _keeper.Open();
        }
        else
        {
            _pooling = true;
            // First start after the LiteDB era: convert the old store before anything opens the new one.
            LiteDbMigration.RunIfNeeded(path, log);
            connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        }

        _options = new DbContextOptionsBuilder<SofiaDbContext>().UseSqlite(connectionString).Options;
        _contexts = new PooledDbContextFactory<SofiaDbContext>(_options);

        using (var ctx = _contexts.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
            // WAL lets readers run while a turn is writing; it is a property of the file, so once is enough.
            if (_pooling) ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            EnsureCounterTable(ctx);
        }

        Workspaces = Set(ctx => ctx.Workspaces, w => w.Id);
        Sessions = Set(ctx => ctx.Sessions, s => s.Id);
        Messages = Set(ctx => ctx.Messages, m => m.Id);
        Providers = Set(ctx => ctx.Providers, p => p.Id);
        McpServers = Set(ctx => ctx.McpServers, m => m.Id);
        Snapshots = Set(ctx => ctx.Snapshots, s => s.Id);
        FileReads = Set(ctx => ctx.FileReads, r => r.Id);
        Attachments = Set(ctx => ctx.Attachments, a => a.Id);
        Tokens = Set(ctx => ctx.Tokens, t => t.Id);
        Config = Set(ctx => ctx.Config, c => c.Id);
        Knowledge = Set(ctx => ctx.Knowledge, k => k.Id);
        Catalog = Set(ctx => ctx.Catalog, c => c.Id);
        Memory = Set(ctx => ctx.Memory, m => m.Id);
        Tasks = Set(ctx => ctx.Tasks, t => t.Id);
    }

    /// <summary>
    /// Creates the <c>counters</c> table and seeds the message sequence. <c>EnsureCreated</c> only builds a schema for
    /// a new database, so stores created before the counter existed run this idempotent bootstrap instead.
    /// </summary>
    private static void EnsureCounterTable(SofiaDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS counters (name TEXT NOT NULL PRIMARY KEY, value INTEGER NOT NULL);
            INSERT INTO counters(name, value)
              SELECT 'message_seq', COALESCE((SELECT MAX(Seq) FROM messages), 0)
              WHERE NOT EXISTS (SELECT 1 FROM counters WHERE name = 'message_seq');
            """);
    }

    private EntitySet<T> Set<T>(Func<SofiaDbContext, DbSet<T>> set, Func<T, string> id) where T : class =>
        new(_contexts, _writeGate, set, id, _log);

    public EntitySet<Workspace> Workspaces { get; }
    public EntitySet<Session> Sessions { get; }
    public EntitySet<Message> Messages { get; }
    public EntitySet<ProviderConfig> Providers { get; }
    public EntitySet<McpServerConfig> McpServers { get; }
    public EntitySet<FileSnapshot> Snapshots { get; }
    public EntitySet<FileReadRecord> FileReads { get; }
    public EntitySet<Attachment> Attachments { get; }
    public EntitySet<AccessToken> Tokens { get; }
    public EntitySet<ServerConfig> Config { get; }
    public EntitySet<KnowledgeNote> Knowledge { get; }
    public EntitySet<CatalogCache> Catalog { get; }
    public EntitySet<MemoryEntry> Memory { get; }
    public EntitySet<TaskItem> Tasks { get; }

    /// <summary>
    /// Global monotonic message sequence (order inside a session follows it). Allocated with one atomic SQL statement,
    /// so two hosts writing the same store never hand out the same value; gaps are harmless.
    /// </summary>
    public long NextSeq()
    {
        _writeGate.Wait();
        try
        {
            return SqliteRetry.Execute(() =>
            {
                using var ctx = _contexts.CreateDbContext();
                var connection = ctx.Database.GetDbConnection();
                ctx.Database.OpenConnection();
                try
                {
                    using var command = connection.CreateCommand();
                    // RETURNING needs SQLite >= 3.35; the bundled e_sqlite3 is newer (MessageSequenceTests proves it).
                    command.CommandText = $"UPDATE counters SET value = value + 1 WHERE name = '{MessageSeqCounter}' RETURNING value;";
                    var value = command.ExecuteScalar();
                    if (value is null or DBNull)
                        throw new InvalidOperationException($"The '{MessageSeqCounter}' counter row is missing from the store.");
                    return Convert.ToInt64(value);
                }
                finally
                {
                    ctx.Database.CloseConnection();
                }
            }, _log);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ServerConfig GetConfig() => Config.FindById("config") ?? new ServerConfig();

    public void SaveConfig(ServerConfig config) => Config.Upsert(config);

    public List<Message> SessionMessages(string sessionId) =>
        Messages.Find(m => m.SessionId == sessionId).OrderBy(m => m.Seq).ToList();

    /// <summary>Removes every `memory` entry of a workspace; call it when the workspace is deleted.</summary>
    public int DeleteWorkspaceMemory(string workspaceId) => Memory.DeleteMany(m => m.WorkspaceId == workspaceId);

    /// <summary>Removes every `task` entry of a workspace; call it when the workspace is deleted.</summary>
    public int DeleteWorkspaceTasks(string workspaceId) => Tasks.DeleteMany(t => t.WorkspaceId == workspaceId);

    public void DeleteSession(string sessionId)
    {
        foreach (var child in Sessions.Find(s => s.ParentId == sessionId).ToList())
            DeleteSession(child.Id);
        var attachmentIds = Messages.Find(m => m.SessionId == sessionId)
            .SelectMany(m => m.Attachments ?? [])
            .Select(a => a.Id)
            .Distinct()
            .ToList();
        Messages.DeleteMany(m => m.SessionId == sessionId);
        foreach (var id in attachmentIds)
        {
            try
            {
                // Keep the attachment if another session's message still references it. The reference lives inside a
                // JSON column, so find candidates with a substring match and confirm them by deserializing only those.
                if (MessagesReferenced(id)) continue;
                var attachment = Attachments.FindById(id);
                if (attachment is null) continue;
                try { if (File.Exists(attachment.StoragePath)) File.Delete(attachment.StoragePath); } catch (IOException) { }
                Attachments.Delete(id);
            }
            catch (Exception ex)
            {
                // A leaked attachment file is harmless; a session left behind with its messages already gone is not.
                _log?.LogWarning(ex, "Could not clean up attachment {AttachmentId}", id);
            }
        }
        Snapshots.DeleteMany(s => s.SessionId == sessionId);
        FileReads.DeleteMany(r => r.SessionId == sessionId);
        Knowledge.DeleteMany(k => k.Scope == KnowledgeScope.Chat && k.SessionId == sessionId);
        Tasks.DeleteMany(t => t.SessionId == sessionId);
        Sessions.Delete(sessionId);
    }

    /// <summary>True when any message still embeds the attachment (the id only appears in the JSON column of a reference).</summary>
    private bool MessagesReferenced(string attachmentId)
    {
        using var ctx = _contexts.CreateDbContext();
        var connection = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        try
        {
            var candidateIds = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id FROM messages WHERE instr(Attachments, @id) > 0";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = attachmentId;
                command.Parameters.Add(parameter);
                using var reader = command.ExecuteReader();
                while (reader.Read()) candidateIds.Add(reader.GetString(0));
            }
            if (candidateIds.Count == 0) return false;
            return Messages.Find(m => candidateIds.Contains(m.Id))
                .Any(m => m.Attachments?.Any(a => a.Id == attachmentId) == true);
        }
        finally
        {
            ctx.Database.CloseConnection();
        }
    }

    /// <summary>
    /// Reclaims free pages. Unlike LiteDB, SQLite only needs <c>VACUUM</c>: there is no rebuild, no marker file and no
    /// `-temp`/`-backup` copies to clean up.
    /// </summary>
    public void Compact()
    {
        // Debounce: a burst of deletes (e.g. "delete all archived") must not vacuum the file N times.
        if ((DateTime.UtcNow - _lastCompactUtc).TotalSeconds < 10) return;
        _lastCompactUtc = DateTime.UtcNow;
        try
        {
            _writeGate.Wait();
            try
            {
                using var ctx = _contexts.CreateDbContext();
                ctx.Database.ExecuteSqlRaw("VACUUM;");
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            // A failed shrink must never fail the delete that requested it.
            _log?.LogDebug(ex, "VACUUM failed; the database keeps its size");
        }
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        // Dropping the keeper closes the last connection of an in-memory database, which erases it.
        _keeper?.Dispose();
    }
}
