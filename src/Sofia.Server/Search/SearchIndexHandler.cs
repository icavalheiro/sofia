using System.Collections.Concurrent;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;

namespace Sofia.Server.Search;

/// <summary>
/// Keeps the search index in step with the event bus. Streaming assistant messages publish a
/// <c>message.updated</c> per delta batch, so updates are debounced per message id: each new update
/// reschedules a single reindex that only runs once the message has been quiet for <see cref="Debounce"/>.
/// The debounced upsert reads the message back from LiteDB and skips it if it no longer exists, which also
/// closes the race where a delete lands between scheduling and firing.
/// </summary>
public sealed class SearchIndexHandler(ISessionSearch search, SofiaDb db) : IEventHandler
{
    private readonly ISessionSearch _search = search;
    private readonly SofiaDb _db = db;
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.Ordinal);

    /// <summary>Quiet period before a debounced reindex runs.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    public Task HandleAsync(SofiaEvent e, CancellationToken ct)
    {
        switch (e.Type)
        {
            case EventTypes.MessageCreated:
                if (TryGetMessage(e.Data, out var created)) _search.Upsert(created);
                break;

            case EventTypes.MessageUpdated:
                if (TryGetId(e.Data, "id") is { } updatedId) Schedule(updatedId);
                break;

            case EventTypes.MessageDeleted:
                if (e.Data.TryGetProperty("ids", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in ids.EnumerateArray())
                        if (!string.IsNullOrEmpty(item.GetString())) CancelAndRemove(item.GetString()!);
                break;

            case EventTypes.SessionDeleted:
                {
                    var sid = e.Data.TryGetProperty("id", out var idProp) ? idProp.GetString() : e.SessionId;
                    if (!string.IsNullOrEmpty(sid)) RemoveSession(sid!);
                    break;
                }

            // session.updated (title change): titles are resolved at query time from db.Sessions, so the index
            // itself needs no update.
        }
        return Task.CompletedTask;
    }

    private void Schedule(string messageId)
    {
        // HandleAsync runs on a single reader thread, so rescheduling is not racy with other updates; only timer
        // callbacks (thread pool) overlap, and those are idempotent upserts.
        if (_pending.TryRemove(messageId, out var old)) old.Dispose();
        _pending[messageId] = new Timer(OnPending, messageId, Debounce, Timeout.InfiniteTimeSpan);
    }

    private void OnPending(object? state)
    {
        var id = (string?)state;
        if (id is null) return;
        _pending.TryRemove(id, out _);
        try
        {
            var msg = _db.Messages.FindById(id);
            // A delete that landed after scheduling removes the message from LiteDB; skip it rather than reindex.
            if (msg is not null) _search.Upsert(msg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a late fire during shutdown must not take the process down with it.
        }
    }

    private void CancelAndRemove(string messageId)
    {
        if (_pending.TryRemove(messageId, out var timer)) timer.Dispose();
        _search.Remove(messageId);
    }

    private void RemoveSession(string sessionId)
    {
        // Drop any pending timers for the session's messages before removing them, so a late upsert can't revive one.
        foreach (var id in _db.SessionMessages(sessionId).Select(m => m.Id)) CancelAndRemove(id);
        _search.RemoveSession(sessionId);
    }

    private bool TryGetMessage(System.Text.Json.JsonElement data, out Domain.Message message)
    {
        if (TryGetId(data, "id") is { } id)
        {
            message = _db.Messages.FindById(id)!;
            return message is not null;
        }
        message = default!;
        return false;
    }

    private static string? TryGetId(System.Text.Json.JsonElement data, string name) =>
        data.TryGetProperty(name, out var p) ? p.GetString() : null;
}
