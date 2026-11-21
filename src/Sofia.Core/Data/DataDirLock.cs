using System.Text;
using Microsoft.Extensions.Logging;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Data;

/// <summary>
/// Advisory ownership of one data directory (<c>&lt;dataDir&gt;/sofia.lock</c>). The lock is a file opened with
/// <see cref="FileShare.None"/> that holds the owner's pid and start time, removed when released. A stale file left by
/// a crash is harmless: the file can be opened again, and only a live handle makes <see cref="TryAcquire"/> fail.
/// </summary>
/// <remarks>
/// It is advisory on purpose: two servers on one directory keep working (SQLite serialises their writes and the
/// message counter is allocated in the database). The lock exists so a host that wants to own the directory — the
/// desktop head, which serves its own conversations in-process — can refuse to start next to a server.
/// </remarks>
public sealed class DataDirLock : IDisposable
{
    public const string FileName = "sofia.lock";

    private readonly FileStream _handle;

    private DataDirLock(FileStream handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// Takes the lock, or returns null and reports who holds it. <paramref name="owner"/> is the raw content of the
    /// lock file ("pid=… started=…"), best effort.
    /// </summary>
    public static DataDirLock? TryAcquire(string dataDir, out string? owner)
    {
        owner = null;
        Directory.CreateDirectory(dataDir);
        var path = System.IO.Path.Combine(dataDir, FileName);
        owner = ReadOwner(path);
        try
        {
            // FileShare.Read (not None) so a second host can read the owner out of the file while it is held; a writer
            // still cannot open it, which is what makes the lock exclusive.
            var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                handle.SetLength(0);
                var line = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} started={DateTime.UtcNow:O}{Environment.NewLine}");
                handle.Write(line, 0, line.Length);
                handle.Flush();
                return new DataDirLock(handle, path);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadOwner(string path)
    {
        try
        {
            // The lock file may be held with FileShare.Read by its owner, so this open has to allow sharing too.
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _handle.Dispose();
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file does not keep the directory locked; the next owner opens it again.
        }
    }
}

/// <summary>
/// Tries to own the data directory at startup. A host that must own it (<see cref="SofiaCoreOptions.ExclusiveDataDir"/>,
/// the desktop head) refuses to start next to another process; the ASP.NET server only warns and keeps serving.
/// </summary>
public sealed class DataDirectoryOwnership(SofiaOptions options, SofiaCoreOptions core, ILogger<DataDirectoryOwnership> log) : IHostedService, IDisposable
{
    public DataDirLock? Lock { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Lock = DataDirLock.TryAcquire(options.DataDir, out var owner);
        if (Lock is not null)
        {
            log.LogDebug("Owns the data directory {DataDir}", options.DataDir);
            return Task.CompletedTask;
        }

        if (core.ExclusiveDataDir)
            throw new InvalidOperationException(
                $"Another Sofia process is using the data directory '{options.DataDir}' ({owner ?? "owner unknown"}). " +
                "Close it and start again, or point Sofia at a different directory.");

        log.LogWarning(
            "Another Sofia process is using the data directory {DataDir} ({Owner}). Both keep working (message ordering is " +
            "allocated in the database), but browser windows, processes and the screen-awake lock are shared.",
            options.DataDir, owner ?? "owner unknown");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Lock?.Dispose();
}
