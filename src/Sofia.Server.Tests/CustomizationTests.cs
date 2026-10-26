using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Agent;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tests;

public sealed class CustomizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sofia-custom-" + Guid.NewGuid().ToString("N"));
    private readonly SofiaOptions _options;

    public CustomizationTests()
    {
        _options = new SofiaOptions { DataDir = Path.Combine(_root, "data") };
        Directory.CreateDirectory(Path.Combine(_root, "project"));
    }

    private string Project => Path.Combine(_root, "project");

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void LoadsCustomAgentsAndProjectOverridesGlobal()
    {
        Write(Path.Combine(_options.AgentsDir, "reviewer.md"),
            "---\ndescription: Reviews diffs\ntools: Read, Grep, bash\n---\nYou review code.");
        Write(Path.Combine(_options.AgentsDir, "scribe.md"), "---\ndescription: Global scribe\nmode: primary\n---\nWrites docs.");
        Write(Path.Combine(Project, ".sofia", "agents", "scribe.md"), "---\ndescription: Project scribe\nmode: all\n---\nWrites project docs.");
        Write(Path.Combine(Project, ".sofia", "agents", "bad name.md"), "x");
        Write(Path.Combine(Project, ".claude", "agents", "foreign.md"), "---\ndescription: Foreign\n---\nIgnored.");

        var catalog = new AgentCatalog(_options);
        var reviewer = catalog.Find("reviewer", Project)!;
        Assert.Equal("subagent", reviewer.Mode);
        Assert.Equal("global", reviewer.Source);
        Assert.Equal(["read", "grep", Agents.ShellToolToken], reviewer.Tools);
        Assert.False(reviewer.IncludeMcp);
        Assert.Contains("You review code.", reviewer.Addendum);
        Assert.Contains("subagent", reviewer.Addendum);

        var scribe = catalog.Find("scribe", Project)!;
        Assert.Equal(("project", "all"), (scribe.Source, scribe.Mode));
        Assert.Equal("global", catalog.Find("scribe", null)!.Source);

        Assert.Null(catalog.Find("bad name", Project));
        Assert.Null(catalog.Find("foreign", Project));
        Assert.Equal("build", catalog.Get("missing", Project).Id);
        Assert.Contains(catalog.Delegable(Project), a => a.Id == "reviewer");
        Assert.DoesNotContain(catalog.Delegable(Project, readOnlyOnly: true), a => a.Id == "reviewer");
        Assert.DoesNotContain(catalog.Delegable(null), a => a.Id == "build");
    }

    [Fact]
    public void DetectsAgentMentionsInText()
    {
        Write(Path.Combine(_options.AgentsDir, "reviewer.md"), "---\ndescription: Reviews\n---\nReview.");
        var catalog = new AgentCatalog(_options);

        Assert.Equal(["reviewer", "explore"], catalog.Mentions("ask @reviewer, then @Explore.", null).Select(a => a.Id));
        Assert.Empty(catalog.Mentions("mail me at dev@reviewer.dev or see @build and @nobody", null));
        Assert.Single(catalog.Mentions("@reviewer @reviewer", null));
    }

    [Fact]
    public void LoadsNestedCommandsWithFrontmatter()
    {
        Write(Path.Combine(_options.CommandsDir, "git", "pr.md"),
            "---\ndescription: Open a PR\nagent: plan\nmodel: p/m\nsubtask: true\n---\nOpen a PR for $ARGUMENTS");
        Write(Path.Combine(_options.PromptsDir, "old.md"), "Legacy prompt");
        Write(Path.Combine(Project, ".sofia", "commands", "old.md"), "Project override");

        var commands = new CommandService(_options).List(Project);
        var pr = commands.Single(c => c.Name == "git/pr");
        Assert.Equal(("Open a PR", "plan", "p/m", true, "global"), (pr.Description, pr.Agent, pr.Model, pr.Subtask, pr.Source));
        Assert.Equal("project", commands.Single(c => c.Name == "old").Source);
        Assert.Contains(commands, c => c.Name == "compact" && c.Source == "builtin");
    }

    [Fact]
    public void ReadsSofiaAndSharedProjectFoldersAndIgnoresOtherHarnesses()
    {
        Write(Path.Combine(Project, ".claude", "commands", "foreign.md"), "x");
        Write(Path.Combine(Project, ".opencode", "command", "foreign.md"), "x");
        Write(Path.Combine(Project, ".claude", "skills", "foreign", "SKILL.md"), "---\nname: foreign\ndescription: x\n---\nx");
        Write(Path.Combine(Project, ".agents", "skills", "shared", "SKILL.md"), "---\nname: shared\ndescription: x\n---\nx");
        Write(Path.Combine(Project, ".agents", "commands", "only-shared.md"), "Shared only");
        Write(Path.Combine(Project, ".agents", "commands", "shared.md"), "Shared command");
        Write(Path.Combine(Project, ".sofia", "commands", "shared.md"), "Sofia wins");
        Write(Path.Combine(Project, ".sofia", "skills", "mine", "SKILL.md"), "---\nname: mine\ndescription: x\n---\nx");

        var commands = new CommandService(_options).List(Project);
        Assert.DoesNotContain(commands, c => c.Name == "foreign");
        Assert.Contains(commands, c => c.Name == "only-shared");
        Assert.Equal("Sofia wins", commands.Single(c => c.Name == "shared").Template.Trim());

        Assert.Equal(["mine", "shared"], new SkillService(_options).List(Project).Where(s => s.Source == "project").Select(s => s.Name));

        var catalog = new AgentCatalog(_options);
        Write(Path.Combine(Project, ".agents", "agents", "helper.md"), "---\ndescription: Shared helper\n---\nHelps.");
        Assert.Equal(("project", "Shared helper"), (catalog.Find("helper", Project)!.Source, catalog.Find("helper", Project)!.Description));
        Write(Path.Combine(Project, ".sofia", "agents", "helper.md"), "---\ndescription: Sofia helper\n---\nHelps better.");
        Assert.Equal("Sofia helper", catalog.Find("helper", Project)!.Description);
    }

    [Fact]
    public async Task ExpandsShellOutputAndFileReferences()
    {
        Write(Path.Combine(Project, "notes.txt"), "remember this");
        Write(Path.Combine(_root, "secret.txt"), "outside");
        var shell = ProcessRunner.ResolveShell(null);
        const string template = "Output: !`echo sofia-ok` see @notes.txt and @../secret.txt and me@notes.txt";

        var text = await CommandService.ExpandDynamicAsync(template, Project, runShell: true, shell, CancellationToken.None);
        Assert.Contains("Output: sofia-ok", text);
        Assert.Contains("<file path=\"notes.txt\">\nremember this\n</file>", text);
        Assert.DoesNotContain("outside", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<file "));

        var literal = await CommandService.ExpandDynamicAsync(template, Project, runShell: false, shell, CancellationToken.None);
        Assert.Contains("!`echo sofia-ok`", literal);
        Assert.Equal(template, await CommandService.ExpandDynamicAsync(template, null, runShell: true, shell, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* best effort */ }
    }
}

public sealed class ConfigWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sofia-watch-" + Guid.NewGuid().ToString("N"));
    private readonly SofiaOptions _options;
    private readonly string _project;

    public ConfigWatcherTests()
    {
        _options = new SofiaOptions { DataDir = Path.Combine(_root, "data") };
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_options.DataDir, "commands"));
        Directory.CreateDirectory(Path.Combine(_project, ".sofia", "commands"));
        Directory.CreateDirectory(Path.Combine(_project, ".agents", "skills"));
    }

    [Fact]
    public void WatchesOnlyTheFoldersSofiaReads()
    {
        Assert.True(ConfigPaths.IsConfig("commands/git/pr.md"));
        Assert.True(ConfigPaths.IsConfig(@"skills\commit\SKILL.md"));
        Assert.True(ConfigPaths.IsConfig("SYSTEM.md"));
        Assert.False(ConfigPaths.IsConfig("sofia.db"));
        Assert.False(ConfigPaths.IsConfig(Path.Combine("attachments", "att_1.png")));

        Assert.True(ConfigPaths.IsWorkspaceRootEntry(".sofia"));
        Assert.True(ConfigPaths.IsWorkspaceRootEntry(".agents"));
        Assert.True(ConfigPaths.IsWorkspaceRootEntry("AGENTS.md"));
        Assert.True(ConfigPaths.IsWorkspaceRootEntry("CLAUDE.md"));
        Assert.False(ConfigPaths.IsWorkspaceRootEntry("README.md"));
        Assert.False(ConfigPaths.IsWorkspaceRootEntry(".claude"));
        Assert.False(ConfigPaths.IsWorkspaceRootEntry("node_modules"));

        // Home + workspace root + its two config folders (they exist) + the missing workspace root (watcher skipped later).
        // The trailing-separator duplicate collapses.
        var roots = ConfigPaths.WatchRoots(_options.DataDir, [_project, _project + Path.DirectorySeparatorChar, Path.Combine(_root, "gone")]);
        Assert.Equal(5, roots.Count);
        Assert.Equal(roots.Count, roots.Select(r => r.Key).Distinct().Count());
        Assert.Contains(roots, r => r.Recursive && r.Scope == "global" && PathUtil.Same(r.Path, _options.DataDir));
        Assert.Contains(roots, r => !r.Recursive && r.Scope == "project" && PathUtil.Same(r.Path, _project));

        var withProjectConfig = ConfigPaths.WatchRoots(_options.DataDir, [_project]);
        Assert.Contains(withProjectConfig, r => r.Recursive && PathUtil.Same(r.Path, Path.Combine(_project, ".sofia")));
        Assert.Contains(withProjectConfig, r => r.Recursive && PathUtil.Same(r.Path, Path.Combine(_project, ".agents")));
    }

    [Fact]
    public async Task NotifiesWhenACommandFileIsWritten()
    {
        using var db = new SofiaDb(new LiteDB.ConnectionString { Filename = ":memory:" });
        db.Workspaces.Insert(new Workspace { Id = "ws_1", Name = "Project", Path = _project, Kind = "folder" });

        var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        var events = bus.Subscribe(cts.Token);
        var watcher = new ConfigWatcher(db, _options, bus, NullLogger<ConfigWatcher>.Instance);
        await ((IHostedService)watcher).StartAsync(CancellationToken.None);
        try
        {
            // Workspace files that are not config must not wake anything.
            File.WriteAllText(Path.Combine(_project, "notes.txt"), "not config");
            Assert.True(await StaysQuiet(events, TimeSpan.FromMilliseconds(900)), "a plain workspace file notified a config change");

            File.WriteAllText(Path.Combine(_project, ".sofia", "commands", "ship.md"), "---\ndescription: Ship it\n---\nShip.");
            var project = await NextEvent(events, "a workspace command was written");
            Assert.Equal(EventTypes.ConfigChanged, project.Type);
            Assert.Equal("project", project.Data.GetProperty("scope").GetString());
            Assert.True(PathUtil.Same(project.Data.GetProperty("directory").GetString()!, _project));

            File.WriteAllText(Path.Combine(_options.DataDir, "commands", "global.md"), "Global template");
            var home = await NextEvent(events, "a global command was written");
            Assert.Equal("global", home.Data.GetProperty("scope").GetString());

            Directory.CreateDirectory(Path.Combine(_project, ".agents", "skills", "shared"));
            File.WriteAllText(Path.Combine(_project, ".agents", "skills", "shared", "SKILL.md"), "---\nname: shared\ndescription: x\n---\nx");
            var shared = await NextEvent(events, "a skill in the shared .agents folder was written");
            Assert.Equal("project", shared.Data.GetProperty("scope").GetString());
        }
        finally
        {
            await ((IHostedService)watcher).StopAsync(CancellationToken.None);
            cts.Cancel();
        }
    }

    private static async Task<SofiaEvent> NextEvent(ChannelReader<SofiaEvent> events, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            return await events.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"No event published although {what}.");
        }
    }

    private static async Task<bool> StaysQuiet(ChannelReader<SofiaEvent> events, TimeSpan window)
    {
        using var timeout = new CancellationTokenSource(window);
        try
        {
            await events.ReadAsync(timeout.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* best effort */ }
    }
}
