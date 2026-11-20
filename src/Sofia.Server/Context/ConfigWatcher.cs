using Sofia.Server.Data;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Context;

/// <summary>Payload of the <c>config.changed</c> event: one config root stopped changing (debounced).</summary>
public sealed record ConfigChanged(string Scope, string Directory);

/// <summary>One watched folder: where to watch, how deep, and what the event reports back.</summary>
public sealed record ConfigRoot(string Path, bool Recursive, string Scope, string Directory)
{
    public string Key => $"{(Recursive ? 'r' : 'f')}|{PathUtil.Normalize(Path)}";
}

/// <summary>
/// The Markdown Sofia reads as configuration. Commands, prompts, skills and agents live in the <see cref="Folders"/>
/// folders of the harness home and of the workspace's own config folders (<see cref="ProjectFolders"/>:
/// <c>.sofia</c> and the cross-tool <c>.agents</c>); instructions are the root-level files (<c>AGENTS.md</c>,
/// <c>CLAUDE.md</c>, <c>SYSTEM.md</c>, <c>APPEND_SYSTEM.md</c>). Other harnesses' folders are never watched or read.
/// Both the read services and <see cref="ConfigWatcher"/> treat this as the source of truth: adding a folder here makes
/// it read and watched as well.
/// </summary>
public static class ConfigPaths
{
    /// <summary>Folders holding command / prompt / skill / agent Markdown, in the harness home and in each project folder.</summary>
    public static readonly string[] Folders = ["commands", "prompts", "skills", "agents"];

    /// <summary>Sofia's own workspace config folder; it wins over the shared one on a name clash.</summary>
    public const string ProjectFolder = ".sofia";

    /// <summary>The shared workspace config folder (the `agentskills` `.agents/skills` convention) Sofia also reads.</summary>
    public const string SharedProjectFolder = ".agents";

    /// <summary>Workspace config folders in override order: shared first, Sofia's own last so it wins.</summary>
    public static readonly string[] ProjectFolders = [SharedProjectFolder, ProjectFolder];

    private static readonly HashSet<string> InstructionFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "AGENTS.md", "CLAUDE.md", "SYSTEM.md", "APPEND_SYSTEM.md",
    };

    /// <summary>True when a path relative to the harness home or to a workspace config folder is config Sofia reads.</summary>
    public static bool IsConfig(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        var separator = relativePath.IndexOfAny(['/', '\\']);
        return separator < 0 ? InstructionFiles.Contains(relativePath) : Folders.Contains(relativePath[..separator]);
    }

    /// <summary>The workspace's own roots for the given config folders, in override order (the last root wins).</summary>
    public static IEnumerable<string> ProjectRoots(string directory, params string[] folders) =>
        ProjectFolders.SelectMany(f => folders.Select(folder => Path.Combine(directory, f, folder)));

    /// <summary>True when an entry of a workspace root is config or changes which config the workspace has.</summary>
    public static bool IsWorkspaceRootEntry(string name) => IsProjectFolder(name) || InstructionFiles.Contains(name);

    public static bool IsProjectFolder(string name) =>
        ProjectFolders.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The folders to watch: the harness home recursively (filtered by <see cref="IsConfig"/>, so the LiteDB file and
    /// attachments never count) plus every workspace folder — its root for <c>AGENTS.md</c> / <c>CLAUDE.md</c> and for a
    /// config folder appearing or vanishing, and each existing config folder recursively.
    /// </summary>
    public static List<ConfigRoot> WatchRoots(string home, IEnumerable<string> workspacePaths)
    {
        var roots = new List<ConfigRoot> { new(home, true, "global", home) };
        foreach (var path in workspacePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathUtil.Normalize)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(new ConfigRoot(path, false, "project", path));
            foreach (var folder in ProjectFolders)
            {
                var project = Path.Combine(path, folder);
                if (Directory.Exists(project)) roots.Add(new ConfigRoot(project, true, "project", path));
            }
        }
        return roots;
    }
}

/// <summary>
/// Watches the config folders Sofia reads (see <see cref="ConfigPaths"/>) — the harness home and every workspace's
/// <c>.sofia</c> / <c>.agents</c> folders, <c>AGENTS.md</c> and <c>CLAUDE.md</c> — and publishes a debounced
/// <c>config.changed</c> event so the UI drops the command, agent and skill lists it already fetched the moment a file is
/// added or edited. Sofia itself re-reads those files on every message; this only removes the staleness of what was
/// already on screen.
/// </summary>
public sealed class ConfigWatcher(SofiaDb db, SofiaOptions options, IEventBus bus, ILogger<ConfigWatcher> log) : BackgroundService
{
    /// <summary>Editors and git write a file as several events; one save must become one notification.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Safety net for missed or failed watchers (a folder recreated after its watcher died).</summary>
    private static readonly TimeSpan ResyncEvery = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _debounces = [];
    private int _syncScheduled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = bus.Subscribe(stoppingToken);
        using var stop = stoppingToken.Register(StopAll);
        Sync();
        _ = ResyncLoop(stoppingToken);
        try
        {
            await foreach (var e in reader.ReadAllAsync(stoppingToken))
                if (e.Type is EventTypes.WorkspaceUpdated or EventTypes.WorkspaceDeleted) Sync();
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ResyncLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ResyncEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Sync();
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Reconciles the watchers with the harness home and the workspaces that exist now.</summary>
    private void Sync()
    {
        List<ConfigRoot> roots;
        try
        {
            var paths = db.Workspaces.FindAll()
                .Where(w => w.Kind == "folder" && !string.IsNullOrWhiteSpace(w.Path))
                .Select(w => w.Path);
            roots = ConfigPaths.WatchRoots(options.DataDir, paths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Config watcher could not list the workspaces");
            return;
        }

        lock (_gate)
        {
            var wanted = roots.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _watchers.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) Drop(key);
            foreach (var root in roots)
            {
                if (_watchers.ContainsKey(root.Key)) continue;
                var watcher = TryCreate(root);
                if (watcher is not null) _watchers[root.Key] = watcher;
            }
        }
    }

    private FileSystemWatcher? TryCreate(ConfigRoot root)
    {
        if (!Directory.Exists(root.Path)) return null;
        try
        {
            var watcher = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = root.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            void Handle(string fullPath) => OnEntry(root, Path.GetRelativePath(root.Path, fullPath));
            watcher.Changed += (_, e) => Handle(e.FullPath);
            watcher.Created += (_, e) => Handle(e.FullPath);
            watcher.Deleted += (_, e) => Handle(e.FullPath);
            watcher.Renamed += (_, e) => Handle(e.FullPath);
            // A dropped watcher (folder deleted, buffer overflow) is recreated by the next sync.
            watcher.Error += (_, e) =>
            {
                log.LogDebug(e.GetException(), "Config watcher on {Root} stopped working", root.Path);
                lock (_gate) Drop(root.Key);
                ScheduleSync();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Config watcher could not watch {Root}", root.Path);
            return null;
        }
    }

    private void OnEntry(ConfigRoot root, string relative)
    {
        var interesting = root.Recursive ? ConfigPaths.IsConfig(relative) : ConfigPaths.IsWorkspaceRootEntry(relative);
        if (!interesting) return;
        // A config folder appearing or vanishing changes which roots must be watched at all.
        if (!root.Recursive && ConfigPaths.IsProjectFolder(relative)) ScheduleSync();
        Notify(root);
    }

    /// <summary>Collapses the burst of events one save produces into a single notification per root.</summary>
    private void Notify(ConfigRoot root)
    {
        var key = $"{root.Scope}|{PathUtil.Normalize(root.Directory)}";
        var source = new CancellationTokenSource();
        lock (_gate)
        {
            if (_debounces.TryGetValue(key, out var previous)) previous.Cancel();
            _debounces[key] = source;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, source.Token);
                bus.Publish(EventTypes.ConfigChanged, new ConfigChanged(root.Scope, root.Directory));
                log.LogDebug("Config changed in {Scope} {Directory}", root.Scope, root.Directory);
            }
            catch (OperationCanceledException)
            {
                // A newer change is already scheduled to notify.
            }
            finally
            {
                lock (_gate)
                {
                    if (_debounces.TryGetValue(key, out var current) && ReferenceEquals(current, source)) _debounces.Remove(key);
                }
                source.Dispose();
            }
        }, CancellationToken.None);
    }

    private void ScheduleSync()
    {
        if (Interlocked.Exchange(ref _syncScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce);
            }
            finally
            {
                Interlocked.Exchange(ref _syncScheduled, 0);
            }
            Sync();
        });
    }

    private void StopAll()
    {
        lock (_gate)
        {
            foreach (var key in _watchers.Keys.ToList()) Drop(key);
            foreach (var source in _debounces.Values) source.Cancel();
            _debounces.Clear();
        }
    }

    /// <summary>Removes and disposes the watcher stored under <paramref name="key"/>; caller holds <c>_gate</c>.</summary>
    private void Drop(string key)
    {
        if (!_watchers.TryGetValue(key, out var watcher)) return;
        _watchers.Remove(key);
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing to do: the folder is already gone.
        }
    }
}
