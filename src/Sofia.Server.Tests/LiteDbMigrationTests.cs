using LiteDB;
using Microsoft.Data.Sqlite;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Tests;

/// <summary>
/// The one-shot LiteDB import. These are the scenarios that matter in the field: a fresh upgrade, a store left
/// behind by an earlier (failed) start, a second process holding the legacy file, and a target that must not be
/// overwritten because it already has conversations.
/// </summary>
public class LiteDbMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-migrate-" + Guid.NewGuid().ToString("N"));
    private string SqlitePath => Path.Combine(_dir, "sofia.sqlite");
    private string LegacyPath => Path.Combine(_dir, "sofia.db");

    public LiteDbMigrationTests() => Directory.CreateDirectory(_dir);

    /// <summary>Writes a legacy LiteDB file the way the old store did (same mapper settings).</summary>
    private void WriteLegacyStore()
    {
        var mapper = new BsonMapper { SerializeNullValues = false, EmptyStringToNull = false, TrimWhitespace = false };
        using var lite = new LiteDatabase(LegacyPath, mapper) { UtcDate = true };
        lite.GetCollection<Workspace>("workspaces").Insert(new Workspace { Id = "ws_1", Name = "Project", Path = @"C:\work", Kind = "folder" });
        var session = new Session { Id = "ses_1", WorkspaceId = "ws_1", Title = "Legacy conversation", FrozenPrompt = "frozen" };
        lite.GetCollection<Session>("sessions").Insert(session);
        lite.GetCollection<Message>("messages").Insert(new Message
        {
            Id = "msg_1",
            SessionId = "ses_1",
            Role = "assistant",
            Text = "done",
            Seq = 7,
            ToolCalls = [new ToolCall { Id = "call_1", Name = "bash", Status = "success", Arguments = "{}", Result = "file.txt", Completed = true }],
        });
        lite.GetCollection<Message>("messages").Insert(new Message { Id = "msg_2", SessionId = "ses_1", Role = "user", Text = "hello", Seq = 8 });
        lite.Checkpoint();
    }

    /// <summary>Creates a SQLite store the way an earlier start of this build would, without any legacy file around.</summary>
    private void WriteSqliteStore(bool withConversation)
    {
        using var db = new SofiaDb(SqlitePath);
        db.Workspaces.Insert(new Workspace { Id = "ws_new", Name = "New", Kind = "chat" });
        db.Sessions.Insert(new Session { Id = "ses_new", WorkspaceId = "ws_new", Title = "New conversation" });
        if (withConversation)
            db.Messages.Insert(new Message { Id = "msg_new", SessionId = "ses_new", Role = "user", Text = "written after the upgrade", Seq = 1 });
    }

    private static long CountMessages(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void ImportsALegacyStoreOnFirstStart()
    {
        WriteLegacyStore();

        using (var db = new SofiaDb(SqlitePath))
        {
            Assert.Equal("Legacy conversation", db.Sessions.FindById("ses_1")!.Title);
            Assert.Equal(2, db.Messages.Count());
            var message = db.Messages.FindById("msg_1")!;
            Assert.Equal("file.txt", message.ToolCalls![0].Result);
            Assert.Equal("frozen", db.Sessions.FindById("ses_1")!.FrozenPrompt);
        }

        Assert.True(File.Exists(SqlitePath));
        Assert.True(File.Exists(LegacyPath + ".migrated"), "the legacy file must be kept as a backup");
        Assert.True(File.Exists(SqlitePath + ".migrated"), "the migration marker must be written");
        Assert.False(File.Exists(LegacyPath), "the legacy file must be renamed away");
        Assert.False(File.Exists(SqlitePath + ".migrating"), "the staging file must not be left behind");
    }

    [Fact]
    public void DoesNotImportTwice()
    {
        WriteLegacyStore();
        using (var _ = new SofiaDb(SqlitePath)) { }

        // A leftover legacy file (the backup is renamed, but a copy may exist) must not be imported again.
        File.Copy(LegacyPath + ".migrated", LegacyPath);
        using var db = new SofiaDb(SqlitePath);
        Assert.Equal(2, db.Messages.Count());
    }

    [Fact]
    public void ReplacesAnEmptyStoreLeftByAnEarlierStart()
    {
        // This is the state a failed first start leaves behind: schema, the auto-created chat, but no messages.
        WriteSqliteStore(withConversation: false);
        Assert.Equal(0, CountMessages(SqlitePath));
        WriteLegacyStore();

        using var db = new SofiaDb(SqlitePath);
        Assert.Equal(2, db.Messages.Count());
        Assert.Equal("Legacy conversation", db.Sessions.FindById("ses_1")!.Title);
        Assert.True(File.Exists(LegacyPath + ".migrated"));
    }

    [Fact]
    public void RefusesToOverwriteAStoreThatHasConversations()
    {
        WriteSqliteStore(withConversation: true);
        WriteLegacyStore();

        var error = Assert.Throws<InvalidOperationException>(() => new SofiaDb(SqlitePath));
        Assert.Contains("already contains", error.Message);
        Assert.Equal(1, CountMessages(SqlitePath));
        Assert.True(File.Exists(LegacyPath), "the legacy file must be left untouched");
        Assert.False(File.Exists(SqlitePath + ".migrated"));
    }

    [Fact]
    public void FailsInsteadOfSkippingWhenTheLegacyStoreIsOpenForWriting()
    {
        WriteLegacyStore();
        using (var writer = new FileStream(LegacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var error = Assert.Throws<InvalidOperationException>(() => new SofiaDb(SqlitePath));
            Assert.Contains("in use by another process", error.Message);
            Assert.False(File.Exists(SqlitePath), "an empty SQLite store must not be created when the import could not run");
            Assert.True(File.Exists(LegacyPath));
        }

        // Once the other instance is gone the next start imports normally.
        using var db = new SofiaDb(SqlitePath);
        Assert.Equal(2, db.Messages.Count());
    }

    [Fact]
    public void RefusesToImportWhileAnotherInstanceIsImporting()
    {
        WriteLegacyStore();
        var lockPath = SqlitePath + ".migration.lock";
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<InvalidOperationException>(() => new SofiaDb(SqlitePath));
            Assert.Contains("already importing", error.Message);
            Assert.False(File.Exists(SqlitePath));
            Assert.True(File.Exists(LegacyPath));
        }

        // The lock is gone with the other instance: the next start imports normally.
        File.Delete(lockPath);
        using var db = new SofiaDb(SqlitePath);
        Assert.Equal(2, db.Messages.Count());
    }

    public void Dispose()
    {
        // File databases keep pooled connections alive; drop them so the directory can go.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
