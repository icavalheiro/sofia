using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sofia.Server.Infrastructure;

/// <summary>Resolved locations and host-level settings (not user-editable at runtime).</summary>
public sealed class SofiaOptions
{
    public string DataDir { get; set; } = "";
    public string UiPath { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];

    public string DbPath => Path.Combine(DataDir, "sofia.db");
    public string AttachmentsDir => Path.Combine(DataDir, "attachments");
    public string SkillsDir => Path.Combine(DataDir, "skills");
    public string PromptsDir => Path.Combine(DataDir, "prompts");
    public string CommandsDir => Path.Combine(DataDir, "commands");
    public string AgentsDir => Path.Combine(DataDir, "agents");
    public string GlobalAgentsFile => Path.Combine(DataDir, "AGENTS.md");

    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("SOFIA_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sofia");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AttachmentsDir);
    }
}

public static class Ids
{
    /// <summary>Time-sortable id: prefix + 11 hex chars of ms timestamp + 8 random hex chars.</summary>
    public static string New(string prefix)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> rnd = stackalloc byte[4];
        RandomNumberGenerator.Fill(rnd);
        return $"{prefix}_{ms:x11}{Convert.ToHexString(rnd).ToLowerInvariant()}";
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }

    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PropertyNameCaseInsensitive = true;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    }

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public static class Text
{
    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB"];
        double value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.0} {units[unit]}";
    }

    public static string ContextLabel(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M ctx" : $"{Math.Round(tokens / 1000.0)}K ctx";

    /// <summary>Keeps the head and tail of an oversized tool output so both ends stay visible.</summary>
    public static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0 || value.Length <= maxChars) return value;
        var keep = Math.Max(256, maxChars / 2);
        var head = value[..keep];
        var tail = value[^keep..];
        return $"{head}\n\n… [{value.Length - head.Length - tail.Length} characters truncated] …\n\n{tail}";
    }

    public static string FirstLine(string value, int max = 120)
    {
        var line = value.Split('\n', 2)[0].Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>
    /// Elapsed time in the largest unit that has started: 873ms, 12.4s, 2.25m, 1.05h.
    /// Always invariant-culture: the string is sent to the UI as-is.
    /// </summary>
    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1)
            return elapsed.TotalMilliseconds < 1000
                ? $"{(int)elapsed.TotalMilliseconds}ms"
                : Fmt(elapsed.TotalSeconds, "0.0") + "s";
        if (elapsed.TotalHours < 1) return Fmt(elapsed.TotalMinutes, "0.00") + "m";
        return Fmt(elapsed.TotalHours, "0.00") + "h";

        static string Fmt(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n");

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Rough token estimate (≈4 chars per token) used before a provider reports usage.</summary>
    public static int EstimateTokens(string? value) => string.IsNullOrEmpty(value) ? 0 : Math.Max(1, value.Length / 4);
}

public static class PathUtil
{
    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Resolves a model-supplied path against the working directory.</summary>
    public static string Resolve(string cwd, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".") return cwd;
        var expanded = path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'))
            : path;
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(cwd, expanded));
    }

    public static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(root)) return path;
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel.Replace('\\', '/');
    }

    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        // VCS / IDE
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        // C# / .NET
        "bin", "obj", "packages", ".nuget",
        // Node / web
        "node_modules", "bower_components", "jspm_packages", ".npm", ".yarn", ".pnpm-store", ".next", ".nuxt", ".svelte-kit", ".angular",
        ".turbo", ".parcel-cache", ".cache", ".output", ".vite", "dist", "build", "out", "coverage", ".nyc_output",
        // Python
        "__pycache__", ".venv", "venv", "env", ".tox", ".nox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".ipynb_checkpoints", "site-packages", ".eggs",
        // PHP / Ruby
        "vendor", ".bundle", ".sass-cache",
        // Rust / Java / Kotlin / Scala / Go-adjacent
        "target", ".gradle", ".m2", ".bloop", ".metals", ".stack-work",
        // Others (Dart, Elixir, Swift, Terraform, Unity, etc.)
        ".dart_tool", ".pub-cache", "_build", "deps", ".build", "DerivedData", "Pods", ".terraform", ".serverless", "Library", "Temp",
    };

    public static bool IsBinary(ReadOnlySpan<byte> sample)
    {
        foreach (var b in sample)
            if (b == 0) return true;
        return false;
    }
}
