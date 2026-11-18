// Single-file dependency audit: reads every csproj PackageReference and the UI package.json, then reports
// installed vs latest-stable from nuget.org / registry.npmjs.org. Versions listed in `blocked` are reported
// as HELD (a deliberate decision, documented in .github/dependabot.yml) instead of UPGRADE.
//
// npm versions are resolved from node_modules/<pkg>/package.json, because package.json only declares a
// range (^26.6.0) whose floor is not what is actually installed.
// Run with: dotnet run .sofia/scripts/DepAudit.cs [--repo <path>]
using System.Text.Json;
using System.Text.RegularExpressions;

var repo = args.Length > 1 && args[0] == "--repo" ? args[1] : Directory.GetCurrentDirectory();
var uiDir = Path.Combine(repo, "src", "Sofia.UI");

// Dependencies we intentionally stay below. `Pattern` is a case-insensitive package-name prefix and
// `BlockFrom` is the first version we refuse to move to (inclusive), so KaTeX's 0.x scheme works.
var blocked = new (string Pattern, string BlockFrom, string Why)[]
{
    ("microsoft.aspnetcore", "11.0.0", ".NET 11 is STS with the same support end date as the net10.0 LTS we target"),
    ("@microsoft/signalr", "11.0.0", "must stay aligned with the server's ASP.NET Core major"),
    ("typescript", "7.0.0", "vue-tsc needs the TypeScript <= 6 compiler API; TS 7 needs language-tools v4 (alpha)"),
    ("katex", "0.19.0", "marked-katex-extension peers on katex >=0.16 <0.19; bump both together"),
    ("litedb", "6.0.0", "v6 exists only as prerelease"),
};

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("sofia-dep-audit/1.0");

Console.WriteLine($"=== NuGet ({repo}) ===");
foreach (var csproj in Directory.EnumerateFiles(repo, "*.csproj", SearchOption.AllDirectories))
{
    var xml = await File.ReadAllTextAsync(csproj);
    // Matches: <PackageReference Include="Id" Version="1.2.3" />
    const string pattern = "PackageReference\\s+Include=\"([^\"]+)\"\\s+Version=\"([^\"]+)\"";
    foreach (Match m in Regex.Matches(xml, pattern))
        await ReportAsync(m.Groups[1].Value, m.Groups[2].Value, nuget: true);
}

Console.WriteLine("\n=== npm ===");
var pkgPath = Path.Combine(uiDir, "package.json");
if (File.Exists(pkgPath))
{
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pkgPath));
    foreach (var section in new[] { "dependencies", "devDependencies" })
    {
        if (!doc.RootElement.TryGetProperty(section, out var deps)) continue;
        foreach (var dep in deps.EnumerateObject())
            await ReportAsync(dep.Name, InstalledNpmVersion(dep.Name), nuget: false);
    }
}

string InstalledNpmVersion(string name)
{
    var installedPath = Path.Combine(uiDir, "node_modules", name, "package.json");
    if (!File.Exists(installedPath)) return "(not installed)";
    using var doc = JsonDocument.Parse(File.ReadAllText(installedPath));
    return doc.RootElement.GetProperty("version").GetString() ?? "(unknown)";
}

async Task ReportAsync(string id, string current, bool nuget)
{
    try
    {
        string latest;
        if (nuget)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json"));
            latest = doc.RootElement.GetProperty("versions").EnumerateArray()
                .Select(v => v.GetString()!)
                .LastOrDefault(v => !v.Contains('-')) ?? "(none)";
        }
        else
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://registry.npmjs.org/{Uri.EscapeDataString(id)}"));
            latest = doc.RootElement.GetProperty("dist-tags").GetProperty("latest").GetString()!;
        }

        var verdict = current == latest ? "current" : Verdict(id, latest);
        Console.WriteLine($"{id,-42} {current,-14} {latest,-14} {verdict}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{id,-42} {current,-14} lookup failed: {ex.Message}");
    }
}

// "HELD" when the newer version is one we deliberately avoid, otherwise "UPGRADE".
string Verdict(string id, string latest)
{
    var lower = id.ToLowerInvariant();
    var hit = blocked.FirstOrDefault(b =>
        lower.StartsWith(b.Pattern, StringComparison.Ordinal) && Core(latest) >= Core(b.BlockFrom));
    if (hit.Why is not null) return $"HELD ({hit.Why})";
    return "UPGRADE";
}

// Compare version cores, ignoring any prerelease suffix (nuget/npm both put it after '-').
static Version Core(string version) =>
    Version.TryParse(version.Split('-')[0].Split('+')[0], out var v) ? v : new Version(0, 0);
