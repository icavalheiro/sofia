// Verification helper for plan-040: drives a throwaway Sofia server (temporary data dir) through the exact mutations
// the Avalonia sidebar performs and prints the result of every step. Never touches the user's server or ~/.sofia.
// Run from the repository root:
//   dotnet run .sofia/scripts/verify-sidebar-mutations.cs <url> <git-repo-path>
#:project ../../src/Sofia.Client/Sofia.Client.csproj

using Sofia.Client;
using Sofia.Contracts.Domain;

AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5198";
var repo = args.Length > 1 ? args[1] : throw new ArgumentException("Pass a temporary git repository path.");
await using var backend = new HttpSofiaBackend(new SofiaBackendOptions { BaseUrl = url });

void Check(string step, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {step}{(detail.Length > 0 ? ": " + detail : "")}");
    if (!ok) Environment.ExitCode = 1;
}

var folder = await backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("folder", repo, null, null, null));
Check("create folder workspace", folder.Kind == "folder" && folder.Conversations is { Count: 1 }, folder.Name);

var chat = await backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("chat", null, "Sidebar test group", "chat", null));
Check("create chat group", chat.Kind == "chat" && chat.Icon == "chat", chat.Name);

var patched = await backend.UpdateWorkspaceAsync(folder.Id, new UpdateWorkspaceRequest("sidebar-test", "code", null, false, null));
Check("patch workspace name/icon/expanded", patched.Name == "sidebar-test" && patched.Icon == "code" && !patched.Expanded);

var first = folder.Conversations![0];
var second = await backend.CreateSessionAsync(folder.Id, new CreateSessionRequest("Second conversation"));
Check("create conversation", second.WorkspaceId == folder.Id && second.Title == "Second conversation");

var renamed = await backend.UpdateSessionAsync(second.Id, new UpdateSessionRequest("Renamed by script", null, null, null, null));
Check("rename conversation", renamed.Title == "Renamed by script");

var pinned = await backend.PinSessionAsync(first.Id);
Check("pin conversation", pinned.Pinned);
var archived = await backend.ArchiveSessionAsync(second.Id);
Check("archive conversation", archived.Archived);
var restored = await backend.UnarchiveSessionAsync(second.Id);
Check("restore conversation", !restored.Archived);

await backend.ReorderSessionsAsync(folder.Id, [second.Id, first.Id]);
var tree = await backend.ListWorkspacesAsync();
var sessions = tree.Single(w => w.Id == folder.Id).Conversations!;
Check("session order (pinned first, then manual order)",
    sessions.Select(s => s.Id).SequenceEqual([first.Id, second.Id]),
    string.Join(',', sessions.Select(s => s.Id)));

await backend.ReorderWorkspacesAsync([chat.Id, folder.Id]);
tree = await backend.ListWorkspacesAsync();
Check("workspace order", tree.Take(2).Select(w => w.Id).SequenceEqual([chat.Id, folder.Id]),
    string.Join(',', tree.Select(w => w.Id)));

var before = await backend.WorktreesAsync(repo);
var beforeCount = before.GetProperty("worktrees").GetArrayLength();
var created = await backend.CreateWorktreeAsync(new CreateWorktreeRequest(repo, "sidebar-test"));
var worktreeDir = created.GetProperty("directory").GetString() ?? "";
Check("create worktree", worktreeDir.Length > 0 && Directory.Exists(worktreeDir), worktreeDir);
var during = await backend.WorktreesAsync(repo);
Check("worktree listed", during.GetProperty("worktrees").GetArrayLength() == beforeCount + 1);
await backend.DeleteWorktreeAsync(new DeleteWorktreeRequest(repo, worktreeDir, true));
var after = await backend.WorktreesAsync(repo);
Check("remove worktree", after.GetProperty("worktrees").GetArrayLength() == beforeCount && !Directory.Exists(worktreeDir));

await backend.DeleteSessionAsync(second.Id);
await backend.DeleteWorkspaceAsync(chat.Id);
await backend.DeleteWorkspaceAsync(folder.Id);
tree = await backend.ListWorkspacesAsync();
Check("delete conversation + workspaces", tree.All(w => w.Id != folder.Id && w.Id != chat.Id));

Console.WriteLine(Environment.ExitCode == 0 ? "OK: sidebar mutation path verified" : "FAILED");
