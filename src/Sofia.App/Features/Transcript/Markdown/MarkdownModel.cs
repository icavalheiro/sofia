namespace Sofia.App.Features.Transcript.Markdown;

/// <summary>How one inline span of a markdown block is rendered.</summary>
public enum MarkdownRunKind
{
    Text,
    Emphasis,
    Strong,
    Code,
    /// <summary>A navigable link: only absolute <c>http</c>, <c>https</c> and <c>mailto</c> URLs get here.</summary>
    Link,
    /// <summary>The alt text of an image (the web never loads model-supplied images).</summary>
    Image,
    /// <summary>Raw HTML surfaced as literal text (never parsed, never injected).</summary>
    Html,
}

/// <summary>One inline span. <see cref="Url"/> is set only for <see cref="MarkdownRunKind.Link"/> runs.</summary>
public sealed record MarkdownRun(MarkdownRunKind Kind, string Text, string? Url = null);

/// <summary>Base of the small markdown block model the renderer consumes (and the unit tests inspect).</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>A fenced or indented code block: rendered on one horizontally scrolling line box.</summary>
public sealed record MarkdownCodeBlock(string Text, string? Language = null) : MarkdownBlock;

public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

public sealed record MarkdownListItem(IReadOnlyList<MarkdownBlock> Blocks);

public sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

public sealed record MarkdownTable(
    IReadOnlyList<IReadOnlyList<MarkdownRun>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownRun>>> Rows) : MarkdownBlock;

public sealed record MarkdownRule : MarkdownBlock;

/// <summary>An HTML block from untrusted output: shown verbatim as text, exactly like the web's escaped renderer.</summary>
public sealed record MarkdownHtml(string Text) : MarkdownBlock;
