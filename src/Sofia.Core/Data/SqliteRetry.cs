using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Sofia.Core.Data;

/// <summary>
/// SQLite allows one writer: a second connection that writes while another one holds the write lock gets
/// <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c>. Both the entity store and the sequence allocator retry that instead of
/// failing an agent turn.
/// </summary>
internal static class SqliteRetry
{
    private const int MaxAttempts = 5;

    public static TResult Execute<TResult>(Func<TResult> action, ILogger? log = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsBusy(ex))
            {
                log?.LogDebug("Database busy on write attempt {Attempt}; retrying", attempt);
                Thread.Sleep(20 * attempt);
            }
        }
    }

    public static bool IsBusy(Exception ex) =>
        ex is SqliteException { SqliteErrorCode: 5 or 6 } || (ex.InnerException is { } inner && IsBusy(inner));
}
