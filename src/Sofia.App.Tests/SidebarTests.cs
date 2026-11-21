using System.Text.Json;
using Avalonia.Headless.XUnit;
using Sofia.App.Features.Sidebar;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Tests;

/// <summary>Sidebar slice: ordering, status rules, the client-side "done" badge, subagent depth and search parsing.</summary>
public class SidebarTests
{
    [AvaloniaFact]
    public void Store_projects_pinned_first_ordering_and_workspace_status()
    {
        var (store, _) = Seeded();

        var sofia = store.Workspaces.Single(w => w.Id == "ws_sofia");
        Assert.Equal("s_1", sofia.Tree[0].Id); // pinned beats recency
        Assert.Equal(["s_1", "s_2", "s_3", "s_4"], sofia.Tree.Select(r => r.Id));
        Assert.Equal(4, sofia.Count);
        Assert.Equal("working", sofia.Status);
        Assert.False(sofia.IsWorking); // expanded: the ring lives on the conversation row

        var site = store.Workspaces.Single(w => w.Id == "ws_site");
        Assert.Equal("permission", site.Status); // precedence over the pinned idle chat
        Assert.Equal(2, site.Count);

        // Folders render before chat groups regardless of the fixture order.
        Assert.Equal(["ws_sofia", "ws_site", "ws_chat"], store.Workspaces.Select(w => w.Id));
        Assert.Equal("question", store.Workspaces.Single(w => w.Id == "ws_chat").Status);
    }

    [AvaloniaFact]
    public void Ending_a_run_while_the_conversation_is_closed_shows_done_until_it_is_opened()
    {
        var (store, harness) = Seeded();
        Assert.Equal("s_1", store.ActiveSessionId);

        harness.Apply(Status("s_2", "working"));
        Assert.Equal("working", store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Single(r => r.Id == "s_2").Status);

        harness.Apply(Status("s_2", "idle"));
        Assert.Equal("done", store.Badges["s_2"]);
        Assert.Equal("done", store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Single(r => r.Id == "s_2").Status);

        // Opening it clears the badge, exactly like useHarness.selectConversation.
        _ = store.OpenSessionAsync("ws_sofia", "s_2");
        Assert.Equal("idle", store.Badges["s_2"]);
        Assert.Equal("idle", store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Single(r => r.Id == "s_2").Status);
    }

    [AvaloniaFact]
    public void Ending_a_run_on_the_open_conversation_does_not_leave_a_done_badge()
    {
        var (store, harness) = Seeded();
        harness.Apply(Status("s_1", "idle"));
        Assert.Equal("idle", store.Badges["s_1"]);
    }

    [AvaloniaFact]
    public void Subagents_nest_under_their_parent_and_only_show_when_running_or_open()
    {
        var workspaces = Fixture.Workspaces();
        var sofia = workspaces.Single(w => w.Id == "ws_sofia");
        sofia.Conversations!.Add(new Session
        {
            Id = "s_sub",
            WorkspaceId = "ws_sofia",
            ParentId = "s_1",
            Title = "Explore the tree",
            Status = "idle",
            UpdatedAt = Fixture.Base,
        });
        var store = new SidebarStore(new HarnessStore());
        store.Seed(workspaces, Fixture.Statuses());

        var tree = store.Workspaces.Single(w => w.Id == "ws_sofia").Tree;
        Assert.DoesNotContain(tree, r => r.Id == "s_sub"); // idle child of an open, non-working parent
        Assert.Equal(4, store.Workspaces.Single(w => w.Id == "ws_sofia").Count);

        var withSub = Fixture.Statuses();
        withSub["s_sub"] = "working";
        store.Seed(workspaces, withSub);
        var working = store.Workspaces.Single(w => w.Id == "ws_sofia").Tree;
        var child = working.Single(r => r.Id == "s_sub");
        Assert.Equal(1, child.Depth);
        Assert.Equal(14, child.IndentPx);
        Assert.Equal(4, store.Workspaces.Single(w => w.Id == "ws_sofia").Count); // subagents never count
    }

    [AvaloniaFact]
    public async Task Local_actions_mutate_the_fixture_tree_like_the_web()
    {
        var (store, _) = Seeded();

        await store.RenameSessionAsync("s_2", "Renamed conversation");
        Assert.Equal("Renamed conversation", store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Single(r => r.Id == "s_2").Title);

        await store.PinSessionAsync("s_3", true);
        Assert.Equal(["s_1", "s_3", "s_2", "s_4"], store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Select(r => r.Id));

        await store.ArchiveSessionAsync("s_2");
        Assert.Equal(["s_1", "s_3", "s_4"], store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Select(r => r.Id));
        Assert.Equal("s_2", store.Workspaces.Single(w => w.Id == "ws_sofia").Archived.Single().Id);

        await store.RestoreSessionAsync("s_2");
        Assert.DoesNotContain(store.Workspaces.Single(w => w.Id == "ws_sofia").Archived, r => r.Id == "s_2");

        await store.ReorderSessionsAsync("ws_sofia", ["s_4", "s_2", "s_3"]);
        Assert.Equal(["s_1", "s_3", "s_4", "s_2"], store.Workspaces.Single(w => w.Id == "ws_sofia").Tree.Select(r => r.Id));

        await store.DeleteSessionAsync("s_4");
        Assert.DoesNotContain(store.Workspaces.Single(w => w.Id == "ws_sofia").Tree, r => r.Id == "s_4");

        await store.RemoveWorkspaceAsync("ws_chat");
        Assert.DoesNotContain(store.Workspaces, w => w.Id == "ws_chat");
    }

    [Fact]
    public void Search_hits_keep_their_highlight_ranges()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new
                {
                    sessionId = "s_1",
                    title = "Avalonia shell",
                    hits = new[]
                    {
                        new { messageId = "m_1", snippet = "the conic sweep", highlights = new[] { new[] { 4, 5 } } },
                    },
                },
            },
        });

        var rows = SidebarStore.ParseHits(json);
        var hit = Assert.Single(rows);
        Assert.Equal("s_1", hit.SessionId);
        Assert.Equal("s_1:m_1", hit.Key);
        Assert.Equal([("the ", false), ("conic", true), (" sweep", false)], hit.Parts().ToList());
    }

    [Fact]
    public void Search_hit_parts_clamp_negative_and_overlapping_ranges()
    {
        var hit = new SearchHitRow
        {
            Snippet = "abcdef",
            Highlights = [(2, 3), (1, 10)],
        };
        Assert.Equal([("ab", false), ("cde", true), ("f", false)], hit.Parts().ToList());
    }

    [Fact]
    public void Worktree_payload_parses_and_paths_compare_case_insensitively()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            root = "/repo",
            worktrees = new[]
            {
                new { directory = "/repo", branch = "main", isMain = true },
                new { directory = "/repo-feature", branch = "feat", isMain = false },
            },
        });
        var (root, entries) = SidebarStore.ParseWorktrees(json);
        Assert.Equal("/repo", root);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsMain);
        Assert.False(entries[1].IsMain);
        Assert.True(WorktreeEntry.SamePath(@"D:\Repo\", "d:/repo"));
    }

    [Fact]
    public void Add_workspace_accepts_drive_and_posix_paths_only()
    {
        Assert.True(SidebarViewModel.IsValidPath(@"D:\repos\sofia"));
        Assert.True(SidebarViewModel.IsValidPath("D:/repos/sofia"));
        Assert.True(SidebarViewModel.IsValidPath("/home/ivan/sofia"));
        Assert.False(SidebarViewModel.IsValidPath("repos/sofia"));
        Assert.False(SidebarViewModel.IsValidPath(""));
    }

    [Fact]
    public void Shortcut_table_carries_the_sidebar_bindings()
    {
        Assert.Equal("Ctrl+K", ShortcutService.BindingOf("search")?.Combo);
        Assert.Equal("Ctrl+O", ShortcutService.BindingOf("workspace.add")?.Combo);
        Assert.Equal("Ctrl+N", ShortcutService.BindingOf("session.new")?.Combo);
        Assert.Equal("Ctrl+Shift+E", ShortcutService.BindingOf("session.archive")?.Combo);
        Assert.Equal("Alt+ArrowUp", ShortcutService.BindingOf("session.prev")?.Combo);
        Assert.Equal("Alt+ArrowDown", ShortcutService.BindingOf("session.next")?.Combo);
        Assert.Null(ShortcutService.BindingOf("nope"));
    }

    private static (SidebarStore Store, HarnessStore Harness) Seeded()
    {
        var harness = new HarnessStore();
        var store = new SidebarStore(harness);
        store.Seed(Fixture.Workspaces(), Fixture.Statuses());
        return (store, harness);
    }

    private static SofiaEventDto Status(string sessionId, string status) => new(
        EventTypes.SessionStatus,
        sessionId,
        "ws_sofia",
        JsonSerializer.SerializeToElement(new { status }));
}
