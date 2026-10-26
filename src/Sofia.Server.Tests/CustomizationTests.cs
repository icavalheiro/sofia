using Sofia.Server.Agent;
using Sofia.Server.Context;
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
    public void IgnoresOtherHarnessFolders()
    {
        Write(Path.Combine(Project, ".claude", "commands", "foreign.md"), "x");
        Write(Path.Combine(Project, ".opencode", "command", "foreign.md"), "x");
        Write(Path.Combine(Project, ".claude", "skills", "foreign", "SKILL.md"), "---\nname: foreign\ndescription: x\n---\nx");
        Write(Path.Combine(Project, ".agents", "skills", "foreign2", "SKILL.md"), "---\nname: foreign2\ndescription: x\n---\nx");
        Write(Path.Combine(Project, ".sofia", "skills", "mine", "SKILL.md"), "---\nname: mine\ndescription: x\n---\nx");

        Assert.DoesNotContain(new CommandService(_options).List(Project), c => c.Name == "foreign");
        Assert.Equal(["mine"], new SkillService(_options).List(Project).Where(s => s.Source == "project").Select(s => s.Name));
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
