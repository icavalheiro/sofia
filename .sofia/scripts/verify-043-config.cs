// Spec 043 verification against a throwaway server (SOFIA_HOME=<temp>/sofia-043, http://127.0.0.1:5299):
// config PATCH validation surfaced verbatim, a valid PATCH applied, the per-session `language` field, and the shape of
// every view the settings/panels read. Refuses to run unless the server reports the temp data dir.
using System.Text;
using System.Text.Json;

var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:5299";
using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

var config = await Get("/api/config");
var dataDir = config.RootElement.GetProperty("dataDir").GetString() ?? "";
Console.WriteLine($"dataDir = {dataDir}");
if (!dataDir.Contains("sofia-043", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("REFUSING: not the throwaway data dir.");
    return 1;
}

// 1. Invalid value: the server's own message must come back (the app shows it inline).
var clamped = await Send(HttpMethod.Patch, "/api/config", """{"compactThreshold":5}""");
Console.WriteLine($"PATCH compactThreshold=5 -> {clamped.Status} (server clamps; compactThreshold={JsonDocument.Parse(clamped.Body).RootElement.GetProperty("compactThreshold").GetDouble()})");
var invalid = await Send(HttpMethod.Patch, "/api/config", """{"defaultBrowser":"netscape"}""");
Console.WriteLine($"PATCH compactThreshold=5 -> {invalid.Status} {Trim(invalid.Body)}");

// 2. Valid change applied and read back.
var valid = await Send(HttpMethod.Patch, "/api/config", """{"compactThreshold":0.8,"autoCompact":true,"defaultBrowser":"auto"}""");
Console.WriteLine($"PATCH compactThreshold=0.8 -> {valid.Status}");
var after = await Get("/api/config");
Console.WriteLine($"config.autoCompact={after.RootElement.GetProperty("autoCompact").GetBoolean()} compactThreshold={after.RootElement.GetProperty("compactThreshold").GetDouble()}");

// 3. Per-session language (PATCH /api/sessions/{id} { language }) — the header language menu writes this.
var workspacePath = "\"" + JsonEncodedText.Encode(Path.GetTempPath()) + "\"";
var workspace = await Send(HttpMethod.Post, "/api/workspaces",
    $$"""{"kind":"folder","path":{{workspacePath}},"name":"spec-043-045742","icon":"code"}""");
var workspaceJson = JsonDocument.Parse(workspace.Body);
var workspaceId = workspaceJson.RootElement.GetProperty("id").GetString();
var session = await Send(HttpMethod.Post, $"/api/workspaces/{workspaceId}/sessions", """{"title":"locale check"}""");
var sessionId = JsonDocument.Parse(session.Body).RootElement.GetProperty("id").GetString();
var patched = await Send(HttpMethod.Patch, $"/api/sessions/{sessionId}", """{"language":"ja"}""");
var reloaded = await Get($"/api/sessions/{sessionId}");
Console.WriteLine($"PATCH session language=ja -> {patched.Status}, session.language={reloaded.RootElement.GetProperty("language").GetString()}");
await Send(HttpMethod.Patch, $"/api/sessions/{sessionId}", """{"language":"pt"}""");
var portuguese = await Get($"/api/sessions/{sessionId}");
Console.WriteLine($"session.language after pt = {portuguese.RootElement.GetProperty("language").GetString()}");

// Delete the workspace so the temp store stays clean.
var deleted = await Send(HttpMethod.Delete, $"/api/workspaces/{workspaceId}", null);
Console.WriteLine($"DELETE workspace -> {deleted.Status}");

// 4. Shapes the settings tabs and the right panel parse.
foreach (var path in new[] { "/api/models", "/api/providers", "/api/providers/presets", "/api/skills", "/api/mcp", "/api/lsp", "/api/browsers", "/api/processes" })
{
    var response = await client.GetAsync(path);
    var body = await response.Content.ReadAsStringAsync();
    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
    var kind = document.RootElement.ValueKind.ToString();
    var count = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : -1;
    Console.WriteLine($"GET {path} -> {(int)response.StatusCode} {kind} count={count}");
}

return 0;

async Task<JsonDocument> Get(string path) =>
    JsonDocument.Parse(await client.GetStringAsync(path));

async Task<(int Status, string Body)> Send(HttpMethod method, string path, string? body)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
    using var response = await client.SendAsync(request);
    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
}

static string Trim(string text) => text.Length <= 220 ? text.Trim() : text[..220].Trim() + "…";
