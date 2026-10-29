using LiteDB;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Tests;

/// <summary>Session deletion (single and the UI's "delete all archived" loop) and its attachment cleanup.</summary>
public class SessionDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-tests-" + Guid.NewGuid().ToString("N"));

    private (SofiaDb Db, string Dir) Create()
    {
        Directory.CreateDirectory(_dir);
        return (new SofiaDb(new ConnectionString { Filename = ":memory:" }), _dir);
    }

    private Attachment File(string id)
    {
        var path = Path.Combine(_dir, id + ".txt");
        System.IO.File.WriteAllText(path, "data");
        return new Attachment { Id = id, Name = id + ".txt", StoragePath = path, Url = "/api/attachments/" + id };
    }

    private static Message UserMessage(string id, string sessionId, params Attachment[] attachments) => new()
    {
        Id = id,
        SessionId = sessionId,
        Role = "user",
        Text = "hi",
        // `null` instead of an empty list: messages without files must read back as absent.
        Attachments = attachments.Length == 0 ? null : [.. attachments],
    };

    [Fact]
    public void DeletingASessionDropsItsOwnAttachments()
    {
        var (db, _) = Create();
        using var keep = db;
        db.Sessions.Insert(new Session { Id = "a", WorkspaceId = "ws" });
        var only = File("only-a");
        db.Attachments.Insert(only);
        db.Messages.Insert(UserMessage("m1", "a", only));

        db.DeleteSession("a");

        Assert.Empty(db.Sessions.FindAll());
        Assert.Empty(db.Messages.FindAll());
        Assert.Null(db.Attachments.FindById("only-a"));
        Assert.False(System.IO.File.Exists(only.StoragePath));
    }

    [Fact]
    public void DeletingASessionKeepsAttachmentsAnotherSessionStillUses()
    {
        var (db, _) = Create();
        using var keep = db;
        db.Sessions.Insert(new Session { Id = "a", WorkspaceId = "ws" });
        db.Sessions.Insert(new Session { Id = "b", WorkspaceId = "ws" });
        var shared = File("shared");
        var own = File("own");
        db.Attachments.Insert(shared);
        db.Attachments.Insert(own);
        db.Messages.Insert(UserMessage("m1", "a", shared, own));
        db.Messages.Insert(UserMessage("m2", "b", shared));

        db.DeleteSession("a");

        Assert.Null(db.Sessions.FindById("a"));
        Assert.Null(db.Attachments.FindById("own"));
        Assert.NotNull(db.Attachments.FindById("shared"));
        Assert.True(System.IO.File.Exists(shared.StoragePath));

        db.DeleteSession("b");

        Assert.Null(db.Attachments.FindById("shared"));
        Assert.False(System.IO.File.Exists(shared.StoragePath));
    }

    [Fact]
    public void DeletingSessionsBackToBackLeavesNothingBehind()
    {
        var (db, _) = Create();
        using var keep = db;
        for (var i = 0; i < 4; i++)
        {
            db.Sessions.Insert(new Session { Id = "s" + i, WorkspaceId = "ws", Archived = true });
            db.Messages.Insert(UserMessage("m" + i, "s" + i, File("att" + i)));
        }
        foreach (var i in Enumerable.Range(0, 4))
            db.Attachments.Insert(db.Messages.FindById("m" + i)!.Attachments![0]);

        for (var i = 0; i < 4; i++) db.DeleteSession("s" + i);

        Assert.Empty(db.Sessions.FindAll());
        Assert.Empty(db.Messages.FindAll());
        Assert.Empty(db.Attachments.FindAll());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void DeletingASessionWithoutAttachmentsStillWorks()
    {
        var (db, _) = Create();
        using var keep = db;
        db.Sessions.Insert(new Session { Id = "a", WorkspaceId = "ws" });
        db.Messages.Insert(UserMessage("m1", "a"));

        db.DeleteSession("a");

        Assert.Empty(db.Sessions.FindAll());
        Assert.Empty(db.Messages.FindAll());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

