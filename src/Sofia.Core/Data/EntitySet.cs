using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Sofia.Core.Data;

/// <summary>
/// Entity accessor over EF Core that keeps the LiteDB call surface (<c>Find</c>, <c>FindById</c>, <c>Upsert</c>,
/// <c>DeleteMany</c>, ...) while every operation runs on its own pooled <see cref="SofiaDbContext"/>. Reads are
/// LINQ queries pushed down to SQLite; mutations are serialized behind one write gate (SQLite has a single writer).
/// </summary>
public sealed class EntitySet<T>(IDbContextFactory<SofiaDbContext> contexts, SemaphoreSlim writeGate,
    Func<SofiaDbContext, DbSet<T>> set, Func<T, string> id, ILogger? log = null) where T : class
{
    private readonly IDbContextFactory<SofiaDbContext> _contexts = contexts;
    private readonly SemaphoreSlim _writeGate = writeGate;
    private readonly Func<SofiaDbContext, DbSet<T>> _set = set;
    private readonly Func<T, string> _id = id;
    private readonly ILogger? _log = log;

    public IEnumerable<T> FindAll() => Query(q => q);

    public IEnumerable<T> Find(Expression<Func<T, bool>> predicate) => Query(q => q.Where(predicate));

    public T? FindOne(Expression<Func<T, bool>> predicate) => Query(q => q.Where(predicate)).FirstOrDefault();

    public T? FindById(string id)
    {
        using var ctx = _contexts.CreateDbContext();
        var entity = _set(ctx).AsNoTracking().FirstOrDefault(e => EF.Property<string>(e, "Id") == id);
        return entity;
    }

    /// <summary>Counts every row.</summary>
    public int Count()
    {
        using var ctx = _contexts.CreateDbContext();
        return _set(ctx).Count();
    }

    public int Count(Expression<Func<T, bool>> predicate)
    {
        using var ctx = _contexts.CreateDbContext();
        return _set(ctx).Count(predicate);
    }

    public bool Exists(Expression<Func<T, bool>> predicate)
    {
        using var ctx = _contexts.CreateDbContext();
        return _set(ctx).Any(predicate);
    }

    /// <summary>Largest value of <paramref name="selector"/>; 0 when the table is empty (LiteDB semantics).</summary>
    public long Max(Expression<Func<T, long>> selector)
    {
        using var ctx = _contexts.CreateDbContext();
        var query = _set(ctx).Select(selector);
        return query.Any() ? query.Max() : 0;
    }

    public bool Insert(T entity) => Write(() =>
    {
        using var ctx = _contexts.CreateDbContext();
        _set(ctx).Add(entity);
        ctx.SaveChanges();
        return true;
    });

    public int InsertBulk(IEnumerable<T> entities) => Write(() =>
    {
        using var ctx = _contexts.CreateDbContext();
        _set(ctx).AddRange(entities);
        return ctx.SaveChanges();
    });

    /// <summary>Replaces an existing row; returns false (and writes nothing) when the id is gone.</summary>
    public bool Update(T entity) => Write(() =>
    {
        using var ctx = _contexts.CreateDbContext();
        if (!Exists(ctx, _id(entity))) return false;
        _set(ctx).Update(entity);
        ctx.SaveChanges();
        return true;
    });

    /// <summary>Inserts the row or replaces it, matching LiteDB's <c>Upsert</c>.</summary>
    public bool Upsert(T entity) => Write(() =>
    {
        using var ctx = _contexts.CreateDbContext();
        if (Exists(ctx, _id(entity))) _set(ctx).Update(entity);
        else _set(ctx).Add(entity);
        ctx.SaveChanges();
        return true;
    });

    public bool Delete(string id) => DeleteMany(e => EF.Property<string>(e, "Id") == id) > 0;

    public int DeleteMany(Expression<Func<T, bool>> predicate) => Write(() =>
    {
        using var ctx = _contexts.CreateDbContext();
        return _set(ctx).Where(predicate).ExecuteDelete();
    });

    private List<T> Query(Func<IQueryable<T>, IQueryable<T>> shape)
    {
        using var ctx = _contexts.CreateDbContext();
        return shape(_set(ctx).AsNoTracking()).ToList();
    }

    private bool Exists(SofiaDbContext ctx, string id) =>
        _set(ctx).AsNoTracking().Any(e => EF.Property<string>(e, "Id") == id);

    /// <summary>Runs a whole write (existence check + save) behind the single-writer gate.</summary>
    private TResult Write<TResult>(Func<TResult> action)
    {
        _writeGate.Wait();
        try
        {
            // SQLite allows one writer: retry the (rare) busy/locked error instead of failing the agent turn.
            return SqliteRetry.Execute(action, _log);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
