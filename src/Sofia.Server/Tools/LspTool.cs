using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Sofia.Server.Infrastructure;
using Sofia.Server.Lsp;

namespace Sofia.Server.Tools;

/// <summary>Position conversion between the tool API (1-based lines/columns) and LSP (0-based, UTF-16).</summary>
internal static class LspPositions
{
    /// <summary>Tool 1-based line/column → LSP 0-based position.</summary>
    public static JsonObject ToLsp(int line, int column) => new()
    {
        ["line"] = Math.Max(0, line - 1),
        ["character"] = Math.Max(0, column - 1),
    };

    /// <summary>Reads 1-based line/column from tool args (column accepts col/character aliases); false when missing or out of range.</summary>
    public static bool TryParse(JsonObject args, out int line, out int column)
    {
        line = IntOf(args, "line") ?? 0;
        foreach (var key in new[] { "column", "col", "character" })
        {
            var value = IntOf(args, key);
            if (value is null) continue;
            column = value.Value;
            return line >= 1 && column >= 1;
        }
        column = 0;
        return false;

        static int? IntOf(JsonObject args, string key)
        {
            if (args[key] is not JsonValue v) return null;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            return null;
        }
    }

    /// <summary>LSP position → tool 1-based "line:column".</summary>
    public static string FromLsp(JsonObject pos) =>
        $"{LongOf(pos["line"]) + 1}:{LongOf(pos["character"]) + 1}";

    private static long LongOf(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    /// <summary>LSP location (uri + range.start) → "path:line:column".</summary>
    public static string Location(JsonObject loc, string root)
    {
        var uri = loc["uri"]?.ToString() ?? "";
        var path = LspManager.PathFromUri(uri);
        return $"{PathUtil.Relative(root, path)}:{FromLsp(loc["range"]?["start"] is JsonObject s ? s : new JsonObject())}";
    }

    /// <summary>LSP hover content (string | { value } | MarkedString[]) → plain text.</summary>
    public static string HoverText(JsonNode? content)
    {
        if (content is null) return "";
        switch (content)
        {
            case JsonValue v:
                return v.ToString();
            case JsonObject o when o["value"] is not null:
                return o["value"]!.ToString();
            case JsonArray arr:
                return string.Join("\n", arr.Select(HoverText));
            default:
                return content.ToJsonString();
        }
    }
}

/// <summary>Read-only LSP queries against the workspace's language servers.</summary>
public sealed class LspTool : ITool
{
    public string Name => "lsp";
    public string Description =>
        "Query language servers for code intelligence: diagnostics (errors/warnings), definition, references, hover, document_symbols and workspace_symbols. " +
        "Positions are 1-based line/column. The language server starts on first use; if it is still loading, the result says so — retry once.";
    public ToolKind Kind => ToolKind.Read;

    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("The query to run.", "diagnostics", "definition", "references", "hover", "document_symbols", "workspace_symbols"), true),
        ("path", Schema.String("File path, relative to the working directory or absolute (required for all actions except workspace_symbols)."), false),
        ("line", Schema.Integer("1-based line of the symbol to query (definition, references, hover)."), false),
        ("column", Schema.Integer("1-based column of the symbol to query (definition, references, hover)."), false),
        ("query", Schema.String("Symbol name or substring for workspace_symbols."), false));

    public string Summarize(JsonObject args) => $"{Args.Optional(args, "action")} {Args.Optional(args, "path") ?? Args.Optional(args, "query") ?? ""}".Trim();

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        if (ctx.Lsp is null) return ToolResult.Fail("LSP is only available in folder workspaces.");
        var action = Args.Required(args, "action").ToLowerInvariant();
        var root = ctx.Cwd;

        switch (action)
        {
            case "diagnostics":
                return await DiagnosticsAsync(ctx, args, ct);
            case "definition":
            case "references":
            case "hover":
            case "document_symbols":
                return await DocumentActionAsync(ctx, action, args, ct);
            case "workspace_symbols":
                return await WorkspaceSymbolsAsync(ctx, args, ct);
            default:
                return ToolResult.Fail($"Unknown action '{action}'. Use diagnostics | definition | references | hover | document_symbols | workspace_symbols.");
        }
    }

    private async Task<ToolResult> DiagnosticsAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        if (!File.Exists(path)) return ToolResult.Fail($"File not found: {path}");

        var output = await ctx.Lsp!.DiagnosticsForFileAsync(ctx.Cwd, path, ct);
        if (output is null)
            return ToolResult.Ok("No diagnostics reported yet for that file (the language server may still be starting, or the file has no errors).");
        return ToolResult.Ok(output);
    }

    private async Task<ToolResult> DocumentActionAsync(ToolContext ctx, string action, JsonObject args, CancellationToken ct)
    {
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        if (!File.Exists(path)) return ToolResult.Fail($"File not found: {path}");
        var def = Lsp.LspServerCatalog.ForPath(path);
        if (def is null) return ToolResult.Fail($"No language server for '{Path.GetExtension(path)}' files.");

        // Sync the current file content first, so answers reflect disk state even after shell-made edits.
        var client = await ctx.Lsp!.GetClientAsync(ctx.Cwd, def.Id, ct);
        if (client is null) return ToolResult.Fail($"The '{def.Id}' language server is not available.");
        Lsp.LspManager.SyncFile(client, def, path, Text.NormalizeNewlines(await File.ReadAllTextAsync(path, ct)));

        var uri = Lsp.LspManager.PathToUri(path);
        var textDocument = new JsonObject { ["uri"] = uri };
        try
        {
            switch (action)
            {
                case "definition":
                    if (!Position(args, out var line, out var column)) return ToolResult.Fail("definition needs 'line' and 'column' (1-based).");
                    // LSP puts the position at the top level of the params (sibling of textDocument).
                    var result = await client.RequestAsync("textDocument/definition", new JsonObject
                    {
                        ["textDocument"] = textDocument,
                        ["position"] = LspPositions.ToLsp(line, column),
                    }, 30_000, ct);
                    return ToolResult.Ok(FormatLocations(result, ctx.Cwd) ?? "No definition found.");

                case "references":
                    if (!Position(args, out line, out column)) return ToolResult.Fail("references needs 'line' and 'column' (1-based).");
                    var refs = await client.RequestAsync("textDocument/references", new JsonObject
                    {
                        ["textDocument"] = textDocument,
                        ["position"] = LspPositions.ToLsp(line, column),
                        ["context"] = new JsonObject { ["includeDeclaration"] = true },
                    }, 30_000, ct);
                    return ToolResult.Ok(FormatLocations(refs, ctx.Cwd) ?? "No references found.");

                case "hover":
                    if (!Position(args, out line, out column)) return ToolResult.Fail("hover needs 'line' and 'column' (1-based).");
                    var hover = await client.RequestAsync("textDocument/hover", new JsonObject
                    {
                        ["textDocument"] = textDocument,
                        ["position"] = LspPositions.ToLsp(line, column),
                    }, 30_000, ct);
                    var content = LspPositions.HoverText(hover?["contents"]);
                    return ToolResult.Ok(string.IsNullOrWhiteSpace(content) ? "No hover information." : content);

                case "document_symbols":
                    var symbols = await client.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = textDocument }, 30_000, ct);
                    return ToolResult.Ok(FormatSymbols(symbols as JsonArray) is { } s && s.Length > 0 ? s : "No symbols found.");

                default:
                    return ToolResult.Fail($"Unknown action '{action}'.");
            }
        }
        catch (TimeoutException)
        {
            return ToolResult.Ok("The language server did not answer in time — it may still be loading the project. Try again shortly.");
        }
        catch (Lsp.LspRequestException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> WorkspaceSymbolsAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var query = Args.Optional(args, "query", "name") ?? "";
        // Search through every enabled server that has a live (or startable) client for the workspace.
        var sb = new StringBuilder();
        foreach (var def in Lsp.LspServerCatalog.All.Where(d => Lsp.LspServerCatalog.IsEnabled(d, ctx.Config.Lsp)))
        {
            ct.ThrowIfCancellationRequested();
            var client = await ctx.Lsp!.GetClientAsync(ctx.Cwd, def.Id, ct);
            if (client is null) continue;
            try
            {
                var result = await client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, 30_000, ct);
                var lines = FormatLocations(result, ctx.Cwd);
                if (lines is not null)
                    sb.Append(sb.Length > 0 ? "\n" : "").Append(lines);
            }
            catch (TimeoutException)
            {
                // That server is still loading; the others may still have matches.
            }
            catch (Lsp.LspRequestException)
            {
                // Some servers do not support workspace symbols; skip them.
            }
        }
        return ToolResult.Ok(sb.Length > 0 ? sb.ToString() : $"No symbols matching '{query}' found.");
    }

    private static bool Position(JsonObject args, out int line, out int column) => LspPositions.TryParse(args, out line, out column);

    /// <summary>LSP location(s) (null | single | array) → relative path:line:column lines, capped.</summary>
    internal static string? FormatLocations(JsonNode? result, string root)
    {
        if (result is null) return null;
        // Never re-parent the server's nodes into a new JsonArray: enumerate instead.
        IEnumerable<JsonNode?> list = result is JsonArray ja
            ? (IEnumerable<JsonNode?>)ja
            : new JsonNode?[] { result };
        var lines = new List<string>();
        foreach (var item in list)
        {
            if (item is null) continue;
            // A location can be nested in a DocumentLink[] for definition.
            IEnumerable<JsonNode?> locs = item is JsonObject o && o["targetUri"] is not null
                ? System.Linq.Enumerable.Repeat<JsonNode?>(o, 1)
                : item as JsonArray ?? System.Linq.Enumerable.Repeat<JsonNode?>(item, 1);
            foreach (var l in locs)
                if (l is JsonObject lo) lines.Add(LspPositions.Location(lo, root));
        }
        if (lines.Count == 0) return null;
        var shown = string.Join('\n', lines.Distinct().Take(100));
        return lines.Count > 100 ? shown + $"\n… {lines.Count - 100} more locations" : shown;
    }

    /// <summary>Symbols (hierarchical or flat) → indented name — kind path:line.</summary>
    private static string? FormatSymbols(JsonArray? symbols)
    {
        if (symbols is null || symbols.Count == 0) return null;
        var lines = new List<string>();
        foreach (var s in symbols.OfType<JsonObject>())
            AppendSymbol(s, 0, lines);
        var shown = string.Join('\n', lines.Take(200));
        return lines.Count > 200 ? shown + $"\n… {lines.Count - 200} more symbols" : shown;
    }

    private static void AppendSymbol(JsonObject s, int depth, List<string> lines)
    {
        var name = s["name"]?.ToString() ?? "?";
        var kind = SymbolKindName(s);
        var range = s["range"] ?? s["location"]?["range"];
        var pos = range is JsonObject r ? LspPositions.FromLsp(r["start"] as JsonObject ?? new JsonObject()) : "";
        lines.Add(new string(' ', depth * 2) + $"{name} — {kind} {pos}".TrimEnd());
        foreach (var child in s["children"]?.AsArray() ?? [])
            if (child is JsonObject c) AppendSymbol(c, depth + 1, lines);
    }

    private static string SymbolKindName(JsonObject s)
    {
        var kind = s["kind"] is JsonValue v && v.TryGetValue<int>(out var k) ? k : -1;
        return kind switch
        {
            1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property",
            8 => "field", 9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable",
            14 => "constant", 15 => "string", 16 => "number", 17 => "boolean", 18 => "array", 19 => "object",
            20 => "key", 21 => "null", 22 => "enum member", 23 => "struct", 24 => "event", 25 => "operator",
            26 => "type parameter", _ => kind.ToString(),
        };
    }
}

/// <summary>Semantic rename through the language server; applies text edits to every affected file.</summary>
public sealed class LspRenameTool : ITool
{
    public string Name => "lsp_rename";
    public string Description =>
        "Rename a symbol across the whole workspace using the language server (semantic, so it catches usages grep would miss). " +
        "Applies the returned text edits to all affected files; resource operations (create/rename/delete) are rejected. Use 1-based line/column on the symbol.";
    public ToolKind Kind => ToolKind.Write;

    public JsonObject Parameters => Schema.Object(
        ("path", Schema.String("File path containing the symbol, relative to the working directory or absolute."), true),
        ("line", Schema.Integer("1-based line of the symbol."), true),
        ("column", Schema.Integer("1-based column of the symbol."), true),
        ("newName", Schema.String("The new name for the symbol."), true));

    public string Summarize(JsonObject args) => $"{Args.Optional(args, "path")} → {Args.Optional(args, "newName")}";

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        if (ctx.Lsp is null) return ToolResult.Fail("LSP is only available in folder workspaces.");
        var path = PathUtil.Resolve(ctx.Cwd, Args.Required(args, "path", "file_path", "filePath"));
        if (!File.Exists(path)) return ToolResult.Fail($"File not found: {path}");
        var def = Lsp.LspServerCatalog.ForPath(path);
        if (def is null) return ToolResult.Fail($"No language server for '{Path.GetExtension(path)}' files.");
        var newName = Args.Raw(args, "newName", "new_name", "name") ?? throw new ToolArgumentException("Missing required argument 'newName'.");
        if (!Position(args, out var line, out var column)) return ToolResult.Fail("'line' and 'column' (1-based) are required.");

        var client = await ctx.Lsp.GetClientAsync(ctx.Cwd, def.Id, ct);
        if (client is null) return ToolResult.Fail($"The '{def.Id}' language server is not available.");
        Lsp.LspManager.SyncFile(client, def, path, Text.NormalizeNewlines(await File.ReadAllTextAsync(path, ct)));

        JsonNode? edit;
        try
        {
            edit = await client.RequestAsync("textDocument/rename", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Lsp.LspManager.PathToUri(path) },
                ["position"] = LspPositions.ToLsp(line, column),
                ["newName"] = newName,
            }, 30_000, ct);
        }
        catch (TimeoutException)
        {
            return ToolResult.Ok("The language server did not answer in time — it may still be loading the project. Try again shortly.");
        }
        if (edit is null)
            return ToolResult.Fail("The language server returned no rename edit (is the cursor on a renameable symbol?).");

        // Collect text edits; refuse resource operations (create/rename/delete).
        var changes = new ConcurrentDictionary<string, List<JsonNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in WorkspaceEditOperations(edit))
        {
            if (op is not JsonObject o) continue;
            if (o["kind"] is JsonValue k && !string.IsNullOrEmpty(k.ToString()))
                return ToolResult.Fail($"The rename includes a resource operation ({k}) which is not supported; apply it manually.");
            var uri = o["textDocument"]?["uri"]?.ToString() ?? "";
            if (uri.Length == 0) continue;
            foreach (var change in o["edits"]?.AsArray() ?? [])
                if (change is not null) changes.GetOrAdd(uri, _ => []).Add(change);
        }
        if (changes.Count == 0)
            return ToolResult.Fail("The rename produced no text edits.");

        // Apply per file: capture for revert before writing, then apply the edits.
        var touched = new List<string>();
        foreach (var (uri, edits) in changes)
        {
            ct.ThrowIfCancellationRequested();
            var file = Lsp.LspManager.PathFromUri(uri);
            if (!file.StartsWith(ctx.Cwd, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail($"The rename touches a file outside the workspace ({PathUtil.Relative(ctx.Cwd, file)}); refused.");
            if (!File.Exists(file))
                return ToolResult.Fail($"The rename requires creating {PathUtil.Relative(ctx.Cwd, file)}; resource operations are not supported.");

            var original = Text.NormalizeNewlines(await File.ReadAllTextAsync(file, ct));
            var updated = ApplyEdits(original, edits);
            if (updated is null)
                return ToolResult.Fail($"Could not apply the rename to {PathUtil.Relative(ctx.Cwd, file)} (an edit range did not match; the file was left unchanged).");
            ctx.Changes.Capture(ctx.Session.Id, ctx.Message, file);
            await File.WriteAllTextAsync(file, updated, ct);
            touched.Add(PathUtil.Relative(ctx.Cwd, file));
        }

        // Keep the servers in sync with what we just wrote.
        foreach (var rel in touched)
        {
            var file = PathUtil.Resolve(ctx.Cwd, rel);
            try
            {
                Lsp.LspManager.SyncFile(client, def, file, Text.NormalizeNewlines(await File.ReadAllTextAsync(file, ct)));
            }
            catch
            {
                // Syncing is best-effort; the next query re-syncs.
            }
        }

        return ToolResult.Ok($"Renamed to '{newName}' in {touched.Count} file{(touched.Count == 1 ? "" : "s")}: {string.Join(", ", touched.Take(20))}{(touched.Count > 20 ? $" … (+{touched.Count - 20} more)" : "")}");
    }

    private static bool Position(JsonObject args, out int line, out int column) => LspPositions.TryParse(args, out line, out column);

    /// <summary>Flattens a WorkspaceEdit into its operations (changes map or documentChanges array).</summary>
    private static IEnumerable<JsonNode> WorkspaceEditOperations(JsonNode? edit)
    {
        if (edit is not JsonObject) yield break;
        if (edit["documentChanges"] is JsonArray arr)
        {
            foreach (var op in arr)
                if (op is not null) yield return op;
            yield break;
        }
        if (edit["changes"] is JsonObject map)
        {
            foreach (var (uri, edits) in map)
                yield return new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri },
                    // The edits array belongs to the server's response; clone it before re-parenting.
                    ["edits"] = edits?.DeepClone(),
                };
        }
    }

    /// <summary>Applies LSP text edits to a full document (offsets are UTF-16 code units = .NET chars); null when an edit does not fit.</summary>
    internal static string? ApplyEdits(string original, IEnumerable<JsonNode?> edits)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var e in edits)
        {
            if (e is not JsonObject o || o["range"] is not JsonObject r) return null;
            var start = OffsetOf(original, r["start"]);
            var end = OffsetOf(original, r["end"]);
            if (start < 0 || end < 0 || start > end) return null;
            ranges.Add((start, end));
        }

        // Apply right-to-left so earlier offsets stay valid.
        var ordered = edits.Zip(ranges).OrderByDescending(p => p.Second.Start).ToList();
        var result = original;
        foreach (var (e, (start, end)) in ordered)
            result = result[..start] + ((e as JsonObject)?["newText"]?.ToString() ?? "") + result[end..];
        return result;
    }

    /// <summary>UTF-16 (line, character) → absolute offset into the text, or -1 when out of bounds.</summary>
    private static int OffsetOf(string text, JsonNode? pos)
    {
        if (pos is not JsonObject p) return -1;
        var line = LongOf(p["line"]);
        var character = LongOf(p["character"]);

        // Find the start of the requested line.
        var offset = 0;
        for (var i = 0; i < line; i++)
        {
            var nl = text.IndexOf('\n', offset);
            if (nl < 0) return -1;
            offset = nl + 1;
            // CRLF: the \r belongs to the previous line, so the next line starts after it.
            if (nl >= 1 && text[nl - 1] == '\r') offset++;
        }

        var end = Math.Min(text.Length, offset + (int)character);
        // A position must not land inside a CRLF pair.
        if (end > offset && text[end] == '\n' && text[end - 1] == '\r') end--;
        return end;
    }

    private static long LongOf(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }
}
