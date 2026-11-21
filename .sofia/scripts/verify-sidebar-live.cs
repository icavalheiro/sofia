// Verification helper for plan-040: exercises the exact read-only calls the Avalonia sidebar makes (workspaces,
// statuses, models, search, worktrees) against an already-running Sofia server. It never mutates the store.
// Run from the repository root:  dotnet run .sofia/scripts/verify-sidebar-live.cs [url]
#:project ../../src/Sofia.Client/Sofia.Client.csproj

using Sofia.Client;

// File-based apps disable reflection-based JSON by default; the client deserializes the documented DTOs.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5199";
await using var backend = new HttpSofiaBackend(new SofiaBackendOptions { BaseUrl = url });

var workspaces = await backend.ListWorkspacesAsync();
Console.WriteLine($"workspaces={workspaces.Count}");
foreach (var workspace in workspaces)
{
    var conversations = workspace.Conversations ?? [];
    Console.WriteLine($"  {workspace.Id} kind={workspace.Kind} icon={workspace.Icon} expanded={workspace.Expanded} " +
                      $"order={workspace.Order} sessions={conversations.Count} " +
                      $"first={(conversations.FirstOrDefault()?.Title ?? "-")}");
    if (conversations.Count == 0) continue;
    var pinnedFirst = conversations
        .OrderByDescending(s => s.Pinned)
        .ThenBy(s => s.Order)
        .ThenByDescending(s => s.UpdatedAt)
        .Select(s => s.Id);
    Console.WriteLine($"  server-order={string.Join(',', conversations.Select(s => s.Id))} " +
                      $"expected-order={string.Join(',', pinnedFirst)}");
}

var statuses = await backend.StatusesAsync();
Console.WriteLine($"statuses={statuses.Count} {string.Join(", ", statuses.Select(kv => $"{kv.Key}:{kv.Value}"))}");

var models = await backend.ModelsAsync();
var modelCount = models.ValueKind == System.Text.Json.JsonValueKind.Array ? models.GetArrayLength() : -1;
Console.WriteLine($"models={modelCount}");

var hits = await backend.SearchAsync("sofia", null, true, true, 5);
if (hits.ValueKind == System.Text.Json.JsonValueKind.Object && hits.TryGetProperty("results", out var results)) hits = results;
var sessions = hits.ValueKind == System.Text.Json.JsonValueKind.Array ? hits.GetArrayLength() : -1;
Console.WriteLine($"search('sofia') sessions={sessions}");
if (sessions > 0)
{
    var first = hits[0];
    var hit = first.GetProperty("hits")[0];
    Console.WriteLine($"  title={first.GetProperty("title").GetString()} " +
                      $"message={hit.GetProperty("messageId").GetString()} " +
                      $"snippet={hit.GetProperty("snippet").GetString()?[..Math.Min(60, hit.GetProperty("snippet").GetString()!.Length)]} " +
                      $"highlights={hit.GetProperty("highlights").GetArrayLength()}");
}

var folder = workspaces.FirstOrDefault(w => w.Kind != "chat" && w.Path.Length > 0)?.Path;
if (folder is not null)
{
    var worktrees = await backend.WorktreesAsync(folder);
    var root = worktrees.ValueKind == System.Text.Json.JsonValueKind.Object &&
               worktrees.TryGetProperty("root", out var r) ? r.GetString() : "";
    var count = worktrees.TryGetProperty("worktrees", out var list) ? list.GetArrayLength() : -1;
    Console.WriteLine($"worktrees({folder}) root={root} entries={count}");
}

Console.WriteLine("OK: sidebar read path answered with the documented shapes");
