using Sofia.Server.Browser;

namespace Sofia.Server.Tests;

/// <summary>Detection, resolution, launch arguments and the instance.json reconciliation rules — all without a browser.</summary>
public class BrowserCatalogTests
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Candidates = new()
    {
        ["chrome"] = [@"C:\chrome\chrome.exe", @"C:\portable\chrome.exe"],
        ["edge"] = [@"C:\edge\msedge.exe"],
        ["firefox"] = ["/usr/bin/firefox"],
    };

    private static Func<string, bool> Existing(params string[] paths) =>
        path => paths.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Detects_only_the_files_that_exist_in_catalog_order()
    {
        var installed = BrowserCatalog.Detect(Candidates, _ => null, Existing(@"C:\portable\chrome.exe", "/usr/bin/firefox"), _ => "1.2.3");

        Assert.Equal(new[] { "chrome", "firefox" }, installed.Select(engine => engine.Id));
        Assert.Equal(@"C:\portable\chrome.exe", installed[0].Path);
        Assert.Equal("chromium", installed[0].Kind);
        Assert.Equal("1.2.3", installed[0].Version);
        Assert.Equal("firefox", installed[1].Kind);
    }

    [Fact]
    public void An_env_override_wins_over_detection()
    {
        var installed = BrowserCatalog.Detect(
            Candidates,
            id => id == "chrome" ? @"D:\tools\chrome.exe" : null,
            Existing(@"C:\chrome\chrome.exe", @"D:\tools\chrome.exe"),
            _ => null);

        var chrome = installed.Single(engine => engine.Id == "chrome");
        Assert.Equal(@"D:\tools\chrome.exe", chrome.Path);
    }

    [Fact]
    public void An_env_override_pointing_at_nothing_is_ignored()
    {
        var installed = BrowserCatalog.Detect(
            Candidates,
            id => id == "chrome" ? @"D:\missing\chrome.exe" : null,
            Existing(@"C:\chrome\chrome.exe"),
            _ => null);

        Assert.DoesNotContain(installed, engine => engine.Id == "chrome");
    }

    [Fact]
    public void Auto_prefers_the_chromium_family_and_falls_back_to_firefox()
    {
        var withChrome = BrowserCatalog.Detect(Candidates, _ => null, Existing(@"C:\chrome\chrome.exe", "/usr/bin/firefox"), _ => null);
        Assert.Equal("chrome", BrowserCatalog.Resolve("auto", withChrome).Id);
        Assert.Equal("chrome", BrowserCatalog.Resolve(null, withChrome).Id);

        var firefoxOnly = BrowserCatalog.Detect(Candidates, _ => null, Existing("/usr/bin/firefox"), _ => null);
        Assert.Equal("firefox", BrowserCatalog.Resolve("auto", firefoxOnly).Id);
    }

    [Fact]
    public void A_specific_engine_resolves_by_id_and_accepts_aliases()
    {
        var installed = BrowserCatalog.Detect(Candidates, _ => null, Existing(@"C:\edge\msedge.exe", "/usr/bin/firefox"), _ => null);

        Assert.Equal("edge", BrowserCatalog.Resolve("edge", installed).Id);
        Assert.Equal("edge", BrowserCatalog.Resolve("msedge", installed).Id);
        Assert.Equal("firefox", BrowserCatalog.Resolve(" moz-firefox ", installed).Id);
    }

    [Fact]
    public void An_unknown_engine_throws_naming_the_known_ones()
    {
        var installed = BrowserCatalog.Detect(Candidates, _ => null, Existing(@"C:\chrome\chrome.exe"), _ => null);

        var unknown = Assert.Throws<InvalidOperationException>(() => BrowserCatalog.Resolve("netscape", installed));
        Assert.Contains("Unknown browser 'netscape'", unknown.Message);
        Assert.Contains("chrome", unknown.Message);
    }

    [Fact]
    public void A_known_but_missing_engine_throws_with_what_is_installed()
    {
        var installed = BrowserCatalog.Detect(Candidates, _ => null, Existing(@"C:\chrome\chrome.exe"), _ => null);

        var missing = Assert.Throws<InvalidOperationException>(() => BrowserCatalog.Resolve("edge", installed));
        Assert.Contains("'edge' is not installed", missing.Message);
        Assert.Contains("installed browsers: chrome", missing.Message);
        Assert.Contains("SOFIA_BROWSER_EDGE", missing.Message);
    }

    [Fact]
    public void Nothing_installed_reports_an_actionable_message()
    {
        var empty = BrowserCatalog.Detect(Candidates, _ => null, _ => false, _ => null);

        Assert.Empty(empty);
        var error = Assert.Throws<InvalidOperationException>(() => BrowserCatalog.Resolve("auto", empty));
        Assert.Contains("No supported browser found", error.Message);
        Assert.Contains("SOFIA_BROWSER_CHROME", error.Message);
    }

    [Fact]
    public void Launch_arguments_use_the_private_profile_and_loopback_port()
    {
        var windowed = BrowserLauncher.ChromiumArguments(@"C:\profiles\ses_1\chrome", 9333, headless: false);

        Assert.Contains(@"--user-data-dir=C:\profiles\ses_1\chrome", windowed);
        Assert.Contains("--remote-debugging-port=9333", windowed);
        Assert.DoesNotContain("--headless=new", windowed);
        // No Playwright automation flags: the window must behave like a normal browser.
        Assert.DoesNotContain(windowed, argument => argument.Contains("enable-automation") || argument.Contains("disable-extensions"));

        var headless = BrowserLauncher.ChromiumArguments("/home/u/.sofia/browsers/ses_1/chrome", 9444, headless: true);
        Assert.Contains("--user-data-dir=/home/u/.sofia/browsers/ses_1/chrome", headless);
        Assert.Contains("--remote-debugging-port=9444", headless);
        Assert.Contains("--headless=new", headless);
    }

    [Fact]
    public void Headless_is_forced_by_the_environment_variable()
    {
        var previous = Environment.GetEnvironmentVariable("SOFIA_BROWSER_HEADLESS");
        try
        {
            Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", "1");
            Assert.True(BrowserLauncher.IsHeadlessDefault());
            Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", "0");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.False(BrowserLauncher.IsHeadlessDefault());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOFIA_BROWSER_HEADLESS", previous);
        }
    }

    [Fact]
    public void Instance_json_round_trips_and_is_deleted()
    {
        var profileDir = Directory.CreateTempSubdirectory("sofia-instance").FullName;
        var instance = new BrowserInstance
        {
            Engine = "chrome",
            Executable = @"C:\chrome\chrome.exe",
            Pid = 4242,
            Port = 9222,
            SessionId = "ses_abc",
            StartedAt = DateTimeOffset.UtcNow,
        };

        BrowserInstanceStore.Write(profileDir, instance);
        var read = BrowserInstanceStore.Read(profileDir);
        Assert.NotNull(read);
        Assert.Equal("chrome", read!.Engine);
        Assert.Equal("ses_abc", read.SessionId);
        Assert.Equal(9222, read.Port);
        Assert.Equal(4242, read.Pid);

        Assert.Single(BrowserInstanceStore.Enumerate(profileDir));

        BrowserInstanceStore.Delete(profileDir);
        Assert.Null(BrowserInstanceStore.Read(profileDir));
        Assert.Empty(BrowserInstanceStore.Enumerate(profileDir));
        Directory.Delete(profileDir, recursive: true);
    }

    [Fact]
    public void Reconcile_attaches_kills_and_prunes_by_rule()
    {
        var live = new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 100, Port = 9001, SessionId = "ses_live" };
        var orphan = new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 200, Port = 9002, SessionId = "ses_gone" };
        var recycledPid = new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 300, Port = 9003, SessionId = "ses_gone" };
        var dead = new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 400, Port = 9004, SessionId = "ses_live" };

        var decisions = BrowserInstanceStore.Decide(
            [live, orphan, recycledPid, dead],
            ["ses_live"],
            isListening: port => port is 9001 or 9002 or 9003,
            executableOfPid: pid => pid switch
            {
                100 or 200 => @"C:\chrome\chrome.exe",
                // pid 300 now belongs to an unrelated program: never kill it.
                300 => @"C:\other\app.exe",
                _ => null,
            });

        Assert.Equal(BrowserInstanceAction.Attach, decisions.Single(d => d.Instance == live).Action);
        Assert.Equal(BrowserInstanceAction.Kill, decisions.Single(d => d.Instance == orphan).Action);
        Assert.Equal(BrowserInstanceAction.Prune, decisions.Single(d => d.Instance == recycledPid).Action);
        Assert.Equal(BrowserInstanceAction.Prune, decisions.Single(d => d.Instance == dead).Action);
    }

    [Fact]
    public void Reconcile_on_disk_forgets_the_records_it_acted_on()
    {
        var browsersRoot = Directory.CreateTempSubdirectory("sofia-browsers").FullName;
        var liveDir = Path.Combine(browsersRoot, "ses_live", "chrome");
        var orphanDir = Path.Combine(browsersRoot, "ses_gone", "chrome");
        BrowserInstanceStore.Write(liveDir, new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 100, Port = 9001, SessionId = "ses_live" });
        BrowserInstanceStore.Write(orphanDir, new BrowserInstance { Engine = "chrome", Executable = @"C:\chrome\chrome.exe", Pid = 200, Port = 9002, SessionId = "ses_gone" });

        var result = BrowserInstanceStore.Reconcile(
            browsersRoot,
            ["ses_live"],
            isListening: port => port is 9001 or 9002,
            executableOfPid: pid => pid == 100 ? @"C:\chrome\chrome.exe" : null);

        Assert.Single(result.Attach);
        Assert.Empty(result.Killed);
        Assert.Single(result.Pruned);
        // The attachable window keeps its record; the pruned one is forgotten but its profile stays.
        Assert.NotNull(BrowserInstanceStore.Read(liveDir));
        Assert.Null(BrowserInstanceStore.Read(orphanDir));
        Assert.True(Directory.Exists(orphanDir));
        Directory.Delete(browsersRoot, recursive: true);
    }
}
