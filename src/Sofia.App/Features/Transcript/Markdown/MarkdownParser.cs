using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sofia.App.Features.Transcript.Markdown;

/// <summary>
/// The untrusted-markdown pipeline (web: <c>marked</c> with <c>gfm: true, breaks: true</c> plus KaTeX): GFM tables,
/// strikethrough, autolinks and task lists; soft line breaks become hard breaks; raw HTML is kept as literal text;
/// only absolute <c>http</c>/<c>https</c>/<c>mailto</c> links stay navigable; images collapse to their alt text.
/// Math is left exactly as written (<c>$…$</c>), the same fallback the web uses for invalid math.
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex SchemePattern = new("^([a-z][a-z0-9+.-]*):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>GFM + breaks, matching the web's <c>marked</c> configuration.</summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    /// <summary>Parses markdown into the block model the Avalonia renderer draws.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? source)
    {
        if (string.IsNullOrEmpty(source)) return [];
        var document = Markdig.Markdown.Parse(source, Pipeline);
        return Blocks(document);
    }

    /// <summary>
    /// Only absolute web/mail links may navigate. Unlike the web (which also kept relative references, harmless in a
    /// browser) a desktop has no base URL, so a relative reference is shown as text: no scheme, no navigation.
    /// </summary>
    public static bool IsNavigable(string? url)
    {
        var value = url?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (!SchemePattern.IsMatch(value)) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme.ToLowerInvariant() is "http" or "https" or "mailto";
    }

    private static List<MarkdownBlock> Blocks(ContainerBlock container)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var block in container) Add(block, blocks);
        return blocks;
    }

    private static void Add(Block block, List<MarkdownBlock> into)
    {
        switch (block)
        {
            case HeadingBlock heading:
                into.Add(new MarkdownHeading(Math.Clamp(heading.Level, 1, 6), Runs(heading.Inline)));
                break;

            case ParagraphBlock paragraph:
                into.Add(new MarkdownParagraph(Runs(paragraph.Inline)));
                break;

            case FencedCodeBlock fenced:
                into.Add(new MarkdownCodeBlock(Code(fenced), string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info.Trim()));
                break;

            case CodeBlock code:
                into.Add(new MarkdownCodeBlock(Code(code)));
                break;

            case QuoteBlock quote:
                into.Add(new MarkdownQuote(Blocks(quote)));
                break;

            case ListBlock list:
                into.Add(ParseList(list));
                break;

            case Table table:
                into.Add(ParseTable(table));
                break;

            case ThematicBreakBlock:
                into.Add(new MarkdownRule());
                break;

            case HtmlBlock html:
                into.Add(new MarkdownHtml(Lines(html)));
                break;

            case ContainerBlock nested:
                // Unknown container (link reference definitions, custom extensions): recurse, never emit raw HTML.
                foreach (var child in nested) Add(child, into);
                break;

            case LeafBlock leaf:
                var text = Lines(leaf);
                if (!string.IsNullOrWhiteSpace(text)) into.Add(new MarkdownParagraph([new MarkdownRun(MarkdownRunKind.Text, text)]));
                break;
        }
    }

    private static MarkdownList ParseList(ListBlock list)
    {
        var start = int.TryParse(list.OrderedStart, out var parsed) ? parsed : 1;
        var items = new List<MarkdownListItem>();
        foreach (var child in list)
        {
            if (child is ListItemBlock item) items.Add(new MarkdownListItem(Blocks(item)));
        }
        return new MarkdownList(list.IsOrdered, start, items);
    }

    private static MarkdownTable ParseTable(Table table)
    {
        var header = new List<IReadOnlyList<MarkdownRun>>();
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownRun>>>();
        var first = true;
        foreach (var child in table)
        {
            if (child is not TableRow row) continue;
            var cells = new List<IReadOnlyList<MarkdownRun>>();
            foreach (var cell in row)
            {
                if (cell is TableCell tableCell) cells.Add(Runs(CellInline(tableCell)));
            }
            if (row.IsHeader || first)
            {
                header.AddRange(cells);
                first = false;
                continue;
            }
            rows.Add(cells);
        }
        return new MarkdownTable(header, rows);
    }

    /// <summary>A table cell is a container block; its first paragraph carries the inline spans.</summary>
    private static ContainerInline? CellInline(TableCell cell)
    {
        foreach (var block in cell)
        {
            if (block is ParagraphBlock paragraph) return paragraph.Inline;
        }
        return null;
    }

    private static List<MarkdownRun> Runs(ContainerInline? inline, MarkdownRunKind style = MarkdownRunKind.Text)
    {
        var runs = new List<MarkdownRun>();
        if (inline is null) return runs;
        foreach (var child in inline) AddInline(child, style, runs);
        return runs;
    }

    private static void AddInline(Inline inline, MarkdownRunKind style, List<MarkdownRun> runs)
    {
        switch (inline)
        {
            case LiteralInline literal:
                Append(runs, style, literal.Content.ToString());
                break;

            case EmphasisInline emphasis:
                AddChildren(emphasis, emphasis.DelimiterCount >= 2 ? MarkdownRunKind.Strong : MarkdownRunKind.Emphasis, runs);
                break;

            case CodeInline code:
                Append(runs, MarkdownRunKind.Code, code.Content);
                break;

            case LineBreakInline:
                Append(runs, style, "\n");
                break;

            case TaskList task:
                Append(runs, style, task.Checked ? "[x] " : "[ ] ");
                break;

            case LinkInline link when link.IsImage:
                // Images are never fetched from model output; the alt text is what the web renders too.
                Append(runs, MarkdownRunKind.Image, Text(link));
                break;

            case LinkInline link:
                if (IsNavigable(link.Url))
                {
                    var label = string.IsNullOrEmpty(Text(link)) ? link.Url! : Text(link);
                    Append(runs, MarkdownRunKind.Link, label, link.Url!.Trim());
                }
                else
                {
                    AddChildren(link, style, runs);
                }
                break;

            case AutolinkInline autolink:
                if (IsNavigable(autolink.Url))
                {
                    Append(runs, MarkdownRunKind.Link, autolink.Url, autolink.Url.Trim());
                }
                else
                {
                    Append(runs, style, autolink.Url);
                }
                break;

            case HtmlInline html:
                Append(runs, MarkdownRunKind.Html, html.Tag);
                break;

            case ContainerInline container:
                AddChildren(container, style, runs);
                break;

            default:
                Append(runs, style, inline.ToString() ?? "");
                break;
        }
    }

    private static void AddChildren(ContainerInline container, MarkdownRunKind style, List<MarkdownRun> runs)
    {
        foreach (var child in container) AddInline(child, style, runs);
    }

    private static void Append(List<MarkdownRun> runs, MarkdownRunKind kind, string text, string? url = null)
    {
        if (text.Length == 0) return;
        // Merge adjacent runs of the same kind so a paragraph stays a handful of spans.
        if (url is null && runs.Count > 0 && runs[^1].Kind == kind && runs[^1].Url is null)
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            return;
        }
        runs.Add(new MarkdownRun(kind, text, url));
    }

    /// <summary>Plain text of an inline container (link labels, image alt text).</summary>
    private static string Text(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }
        return text.ToString().Trim();
    }

    private static string Lines(LeafBlock block)
    {
        var lines = block.Lines;
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(lines.Lines[i].Slice.ToString());
        }
        return builder.ToString();
    }

    private static string Code(LeafBlock block)
    {
        var lines = block.Lines;
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(lines.Lines[i].Slice.ToString());
        }
        return builder.ToString();
    }
}

/// <summary>
/// Parse cache: a streaming message re-renders on every coalesced flush, so the same source is never parsed twice
/// (bounded, least-recently-used).
/// </summary>
public static class MarkdownCache
{
    private const int Capacity = 96;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<MarkdownBlock>> Parsed = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Order = new();

    public static int Count
    {
        get
        {
            lock (Gate) return Parsed.Count;
        }
    }

    public static IReadOnlyList<MarkdownBlock> Parse(string? source)
    {
        var key = source ?? "";
        lock (Gate)
        {
            if (Parsed.TryGetValue(key, out var cached))
            {
                Order.Remove(key);
                Order.AddFirst(key);
                return cached;
            }
        }

        var blocks = MarkdownParser.Parse(key);

        lock (Gate)
        {
            if (!Parsed.ContainsKey(key))
            {
                Parsed[key] = blocks;
                Order.AddFirst(key);
                while (Parsed.Count > Capacity && Order.Last is { } last)
                {
                    Order.RemoveLast();
                    Parsed.Remove(last.Value);
                }
            }
            return Parsed.TryGetValue(key, out var stored) ? stored : blocks;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Parsed.Clear();
            Order.Clear();
        }
    }
}
