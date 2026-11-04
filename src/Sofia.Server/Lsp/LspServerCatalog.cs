using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;

namespace Sofia.Server.Lsp;

/// <summary>One built-in language server definition.</summary>
public sealed record LspServerDef(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    string Command,
    string InstallCommand,
    string InstallHint,
    string RootMarker,
    string LanguageId);

/// <summary>
/// The built-in language servers. Adding a language is a single entry here (plus its extensions);
/// users can override the command per id in <see cref="LspConfig.Servers"/>.
/// </summary>
public static class LspServerCatalog
{
    public static readonly IReadOnlyList<LspServerDef> All =
    [
        new(
            Id: "typescript",
            Name: "TypeScript / JavaScript",
            Extensions: [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
            Command: "typescript-language-server --stdio",
            InstallCommand: "npm i -g typescript-language-server typescript",
            InstallHint: "Requires Node.js and npm on PATH.",
            RootMarker: "package.json",
            LanguageId: "typescript"),
        new(
            Id: "html",
            Name: "HTML",
            Extensions: [".html", ".htm"],
            Command: "vscode-html-language-server --stdio",
            InstallCommand: "npm i -g vscode-langservers-extracted",
            InstallHint: "Requires Node.js and npm on PATH.",
            RootMarker: "package.json",
            LanguageId: "html"),
        new(
            Id: "css",
            Name: "CSS",
            Extensions: [".css", ".scss", ".less"],
            Command: "vscode-css-language-server --stdio",
            InstallCommand: "npm i -g vscode-langservers-extracted",
            InstallHint: "Requires Node.js and npm on PATH.",
            RootMarker: "package.json",
            LanguageId: "css"),
        new(
            Id: "csharp",
            Name: "C#",
            Extensions: [".cs"],
            Command: "csharp-ls",
            InstallCommand: "dotnet tool install -g csharp-ls",
            InstallHint: "Requires the .NET SDK on PATH; works with .sln/.slnx/.csproj roots.",
            RootMarker: "*.sln|*.slnx|*.csproj",
            LanguageId: "csharp"),
    ];

    private static readonly Dictionary<string, LspServerDef> ById =
        All.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public static LspServerDef? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The server that handles a file extension, or null (unknown language or no server for it).</summary>
    public static LspServerDef? ForPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0) return null;
        return All.FirstOrDefault(d => d.Extensions.Contains(ext));
    }

    /// <summary>The language id announced in <c>didOpen</c> for a file.</summary>
    public static string LanguageIdFor(LspServerDef def, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return (def.Id, ext) switch
        {
            ("typescript", ".tsx") => "typescriptreact",
            ("typescript", ".js" or ".jsx" or ".mjs" or ".cjs") when ext is not ".ts" => ext == ".jsx" ? "javascriptreact" : "javascript",
            ("css", ".scss") => "scss",
            ("css", ".less") => "less",
            _ => def.LanguageId,
        };
    }

    /// <summary>
    /// The effective command line for a server: the user override when set, else the built-in.
    /// </summary>
    public static string CommandFor(LspServerDef def, LspConfig? config)
    {
        if (config?.Servers.TryGetValue(def.Id, out var setting) == true && !string.IsNullOrWhiteSpace(setting.Command))
            return setting.Command.Trim();
        return def.Command;
    }

    /// <summary>Whether the language server is enabled (global switch + per-server override).</summary>
    public static bool IsEnabled(LspServerDef def, LspConfig? config)
    {
        if (config is null) return true;
        if (!config.Enabled) return false; // master switch off
        return !config.Servers.TryGetValue(def.Id, out var setting) || setting.Enabled;
    }

    /// <summary>Resolves the first token of a command line to an executable on PATH (PATHEXT-aware on Windows), or null.</summary>
    public static string? ResolveCommand(string commandLine)
    {
        var argv = McpManager.SplitCommandLine(commandLine);
        if (argv.Count == 0) return null;
        var first = argv[0].Trim();
        // Absolute or relative paths are used as-is.
        if (Path.IsPathRooted(first) || first.Contains('/') || first.Contains('\\'))
            return File.Exists(first) ? Path.GetFullPath(first) : null;
        return ProcessRunner.FindOnPath(first);
    }

    /// <summary>True when the workspace root looks like a project for this server (marker file present at the root).</summary>
    public static bool HasRootMarker(LspServerDef def, string root)
    {
        try
        {
            foreach (var pattern in def.RootMarker.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var glob = pattern.StartsWith("*") ? pattern : "*" + pattern;
                if (Directory.EnumerateFiles(root, glob).Any()) return true;
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The install command of every built-in server that is missing on PATH.</summary>
    public static List<(LspServerDef Def, string Command)> Missing(LspConfig? config) =>
        All.Where(d => IsEnabled(d, config) && ResolveCommand(CommandFor(d, config)) is null)
            .Select(d => (d, d.InstallCommand)).ToList();

    /// <summary>The effective command line, for display in the UI and tool output.</summary>
    public static string Display(LspServerDef def, LspConfig? config) => CommandFor(def, config);
}
