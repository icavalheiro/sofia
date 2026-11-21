// Verification helper for plan-038: proves a real store keeps its ordering and gained the counters table.
// Run from the repository root:  dotnet run .sofia/scripts/inspect-seq.cs <path-to-sofia.sqlite>
#:package Microsoft.Data.Sqlite@10.0.11

using Microsoft.Data.Sqlite;

var path = args.Length > 0 ? args[0] : throw new ArgumentException("Pass the database path.");
using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
connection.Open();

long Scalar(string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    var value = command.ExecuteScalar();
    return value is null or DBNull ? -1 : Convert.ToInt64(value);
}

var messages = Scalar("SELECT count(*) FROM messages");
var maxSeq = Scalar("SELECT COALESCE(MAX(Seq), 0) FROM messages");
var duplicateSeqs = Scalar("SELECT count(*) FROM (SELECT Seq FROM messages GROUP BY Seq HAVING count(*) > 1)");
var counter = Scalar("SELECT COALESCE((SELECT value FROM counters WHERE name = 'message_seq'), -1)");
var tables = Scalar("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'counters'");

Console.WriteLine($"messages={messages} maxSeq={maxSeq} duplicateSeqs={duplicateSeqs} countersTable={tables} counterValue={counter}");
Console.WriteLine(duplicateSeqs == 0 && tables == 1 && counter >= maxSeq - 1
    ? "OK: ordering intact, counter seeded at or above the highest message"
    : "FAIL: ordering or counter looks wrong");
