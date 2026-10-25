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

    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000 ? $"{(int)elapsed.TotalMilliseconds}ms" : $"{elapsed.TotalSeconds:0.0}s";

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
        ".git", "node_modules", "bin", "obj", "dist", "target", ".vs", ".idea", "__pycache__", ".venv", "venv", ".next", ".nuxt", "coverage",
    };

    public static bool IsBinary(ReadOnlySpan<byte> sample)
    {
        foreach (var b in sample)
            if (b == 0) return true;
        return false;
    }
}
