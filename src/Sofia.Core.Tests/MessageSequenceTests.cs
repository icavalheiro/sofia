using Microsoft.Data.Sqlite;
using Sofia.Contracts.Domain;
using Sofia.Core.Data;

namespace Sofia.Core.Tests;

/// <summary>
/// Message ordering is global to the store, not to the process: two hosts (the server and the desktop head, or two
/// servers) can write the same <c>~/.sofia</c>. The sequence therefore lives in the database and is allocated with one
/// atomic statement, and a store created before the counter existed keeps counting from its highest seq.
/// </summary>
public class MessageSequenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-seq-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "sofia.sqlite");

    public MessageSequenceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void TwoInstancesOnOneFileNeverShareASequence()
    {
        using var first = new SofiaDb(DbPath);
        using var second = new SofiaDb(DbPath);

        var allocated = new List<long>();
        var gate = new object();
        for (var round = 0; round < 100; round++)
        {
            // Interleave the two writers so an in-memory counter would collide immediately.
            var a = first.NextSeq();
            var b = second.NextSeq();
            lock (gate) allocated.AddRange([a, b]);
        }

        Assert.Equal(200, allocated.Count);
        Assert.Equal(200, allocated.Distinct().Count());
        var ordered = allocated.OrderBy(v => v).ToList();
        for (var i = 1; i < ordered.Count; i++) Assert.True(ordered[i] > ordered[i - 1], $"{ordered[i]} is not after {ordered[i - 1]}");
        Assert.Equal(200, ordered[^1]);
    }

    [Fact]
    public void StoreWithoutTheCounterTableBootstrapsFromTheHighestMessage()
    {
        // A store written by the previous build: messages already numbered, no `counters` table at all.
        using (var db = new SofiaDb(DbPath))
        {
            db.Messages.Insert(new Message { Id = "msg_1", SessionId = "ses_1", Role = "user", Text = "hi", Seq = 41 });
        }
        DropCountersTable();

        using var reopened = new SofiaDb(DbPath);
        Assert.Equal(42, reopened.NextSeq());
        Assert.Equal(43, reopened.NextSeq());
    }

    [Fact]
    public void StoreThatLostItsCounterRowReseeds()
    {
        using (var db = new SofiaDb(DbPath))
        {
            db.Messages.Insert(new Message { Id = "msg_1", SessionId = "ses_1", Role = "user", Text = "hi", Seq = 17 });
        }
        Execute("DELETE FROM counters;");

        using var reopened = new SofiaDb(DbPath);
        Assert.Equal(18, reopened.NextSeq());
    }

    private void DropCountersTable() => Execute("DROP TABLE counters;");

    private void Execute(string sql)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }
    }
}
