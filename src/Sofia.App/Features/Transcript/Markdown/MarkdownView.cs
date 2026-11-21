using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sofia.App.Theme;

namespace Sofia.App.Features.Transcript.Markdown;

/// <summary>
/// Avalonia renderer for the markdown block model, ported from <c>MarkdownText.vue</c>: headings 1.25/1.15/1.05em at
/// 650, paragraphs/lists/code/quotes/tables with a collapsed <c>.7em</c> bottom margin, lists indented 1.5em, inline
/// code on surface-2 with the 1px border and 5px radius, code blocks on the canvas colour with horizontal scrolling,
/// the muted 3px blockquote rule, bordered table cells (header on surface-2) and the 1px <c>hr</c>.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Renders a parsed document into controls (no Application required at parse time).</summary>
    public static StackPanel Render(IReadOnlyList<MarkdownBlock> blocks, double fontSize = 15) =>
        Build(blocks, new MarkdownStyle(fontSize, Muted: false));

    private static StackPanel Build(IReadOnlyList<MarkdownBlock> blocks, MarkdownStyle style)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        var pendingBottom = 0d;
        foreach (var block in blocks)
        {
            var (control, top, bottom) = Block(block, style);
            control.Margin = new Thickness(0, Math.Max(0, top - pendingBottom), 0, bottom);
            pendingBottom = bottom;
            panel.Children.Add(control);
        }

        // `.md > :first-child { margin-top: 0 }` / `:last-child { margin-bottom: 0 }` — but the surrounding card
        // supplies the gap, so only the first top margin is dropped (the last block keeps its bottom margin as the
        // card's own spacing, like the web's transcript rows).
        if (panel.Children.Count > 0)
        {
            var first = panel.Children[0];
            var margin = first.Margin;
            first.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
        }
        return panel;
    }

    private static (Control Control, double Top, double Bottom) Block(MarkdownBlock block, MarkdownStyle style)
    {
        var em = style.FontSize;
        switch (block)
        {
            case MarkdownHeading heading:
            {
                var size = em * (heading.Level == 1 ? 1.25 : heading.Level == 2 ? 1.15 : 1.05);
                var text = Text(heading.Runs, style with { FontSize = size, Muted = false });
                text.FontSize = size;
                text.FontWeight = FontWeight.SemiBold;
                text.LineHeight = size * 1.3;
                return (text, size, size * 0.4);
            }

            case MarkdownParagraph paragraph:
            {
                var text = Text(paragraph.Runs, style);
                // No explicit LineHeight here: a fixed line height combined with an inline UI host (the inline code
                // chip) is mis-measured by Avalonia, and the wrapped overflow would overlap the following block.
                return (text, 0, em * 0.7);
            }

            case MarkdownCodeBlock code:
            {
                var pre = new TextBlock
                {
                    Text = code.Text,
                    FontFamily = ThemeLookup.Find("FontFamilyMono") as FontFamily,
                    FontSize = 13,
                    LineHeight = 13 * 1.55,
                    Foreground = ThemeLookup.Brush("BrushTextMuted"),
                    TextWrapping = TextWrapping.NoWrap,
                };
                var scroll = new ScrollViewer
                {
                    Content = pre,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                };
                var border = new Border
                {
                    Background = ThemeLookup.Brush("BrushBg"),
                    BorderBrush = ThemeLookup.Brush("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12.8, 10.4),
                    Child = scroll,
                    ClipToBounds = true,
                };
                return (border, 0, em * 0.7);
            }

            case MarkdownQuote quote:
            {
                var inner = Build(quote.Blocks, style with { Muted = true });
                inner.Margin = new Thickness(0);
                var border = new Border
                {
                    BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(13.5, 1.5, 0, 1.5),
                    Child = inner,
                };
                return (border, 0, em * 0.7);
            }

            case MarkdownList list:
            {
                var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
                var index = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Ordered ? $"{index++}." : "•";
                    var label = new TextBlock
                    {
                        Text = marker,
                        FontSize = em,
                        LineHeight = em * 1.55,
                        Foreground = ThemeLookup.Brush(style.Muted ? "BrushTextMuted" : "BrushText"),
                        VerticalAlignment = VerticalAlignment.Top,
                        TextAlignment = TextAlignment.Right,
                        MinWidth = em * 1.2,
                    };
                    var content = Build(item.Blocks, style);
                    content.Margin = new Thickness(0);
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    label.Margin = new Thickness(0, 0, em * 0.3, 0);
                    Grid.SetColumn(label, 0);
                    row.Children.Add(label);
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);
                    // `li { margin: .15em 0 }` between items.
                    panel.Children.Add(new Border { Child = row, Margin = new Thickness(0, em * 0.15, 0, 0) });
                }
                panel.Margin = new Thickness(em * 1.5, 0, 0, 0);
                return (panel, 0, em * 0.7);
            }

            case MarkdownTable table:
            {
                var columns = Math.Max(1, table.Header.Count);
                var grid = new Grid();
                for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var index = 0;
                AddCells(grid, table.Header, index++, header: true, style);
                foreach (var row in table.Rows)
                {
                    AddCells(grid, row, index++, header: false, style);
                }

                var scroll = new ScrollViewer
                {
                    Content = grid,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                };
                return (scroll, 0, em * 0.7);
            }

            case MarkdownRule:
                return (new Border
                {
                    BorderBrush = ThemeLookup.Brush("BrushBorder"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                }, em, em);

            case MarkdownHtml html:
            {
                var text = new TextBlock
                {
                    Text = html.Text,
                    FontSize = em,
                    LineHeight = em * 1.55,
                    Foreground = ThemeLookup.Brush(style.Muted ? "BrushTextMuted" : "BrushText"),
                    TextWrapping = TextWrapping.Wrap,
                };
                return (text, 0, em * 0.7);
            }

            default:
                return (new TextBlock(), 0, 0);
        }
    }

    private static void AddCells(Grid grid, IReadOnlyList<IReadOnlyList<MarkdownRun>> cells, int rowIndex,
        bool header, MarkdownStyle style)
    {
        if (grid.RowDefinitions.Count <= rowIndex) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var column = 0; column < cells.Count; column++)
        {
            var cell = cells[column];
            var text = Text(cell, style with { Muted = false });
            text.LineHeight = style.FontSize * 1.55;
            if (header) text.FontWeight = FontWeight.SemiBold;
            var border = new Border
            {
                BorderBrush = ThemeLookup.Brush("BrushBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9.6, 4.8),
                Background = header ? ThemeLookup.Brush("BrushSurface2") : Brushes.Transparent,
                Child = text,
            };
            Grid.SetRow(border, rowIndex);
            Grid.SetColumn(border, column);
            grid.Children.Add(border);
        }
    }

    /// <summary>
    /// One inline TextBlock. Code and links are Runs, never <c>InlineUIContainer</c>s: Avalonia mis-measures a wrapped
    /// line that hosts an inline control, so the paragraph's height came out one line short and the overflow overlapped
    /// the next block (captured in the 041 evidence). Code keeps the mono face, the .85em size and the surface-2
    /// background; a link keeps the accent colour and underline and is opened by a character-range hit test.
    /// </summary>
    private static TextBlock Text(IReadOnlyList<MarkdownRun> runs, MarkdownStyle style)
    {
        var text = new TextBlock
        {
            FontSize = style.FontSize,
            Foreground = ThemeLookup.Brush(style.Muted ? "BrushTextMuted" : "BrushText"),
            TextWrapping = TextWrapping.Wrap,
        };
        var inlines = text.Inlines;
        if (inlines is null) return text;

        var offset = 0;
        var links = new List<(int Start, int Length, string Url)>();
        foreach (var run in runs)
        {
            switch (run.Kind)
            {
                case MarkdownRunKind.Code:
                    inlines.Add(new Run
                    {
                        Text = run.Text,
                        FontFamily = ThemeLookup.Find("FontFamilyMono") as FontFamily,
                        FontSize = style.FontSize * 0.85,
                        Background = ThemeLookup.Brush("BrushSurface2"),
                        Foreground = ThemeLookup.Brush("BrushText"),
                    });
                    break;

                case MarkdownRunKind.Link when MarkdownParser.IsNavigable(run.Url):
                    inlines.Add(new Run
                    {
                        Text = run.Text,
                        Foreground = ThemeLookup.Brush("BrushPrimary"),
                        TextDecorations = TextDecorations.Underline,
                    });
                    links.Add((offset, run.Text.Length, run.Url!));
                    break;

                case MarkdownRunKind.Strong:
                    inlines.Add(new Run { Text = run.Text, FontWeight = FontWeight.Bold });
                    break;

                case MarkdownRunKind.Emphasis:
                    inlines.Add(new Run { Text = run.Text, FontStyle = FontStyle.Italic });
                    break;

                default:
                    inlines.Add(new Run { Text = run.Text });
                    break;
            }
            offset += run.Text.Length;
        }

        if (links.Count > 0) AttachLinks(text, links);
        return text;
    }

    /// <summary>
    /// Opens a link when the pointer lands inside its character range. <see cref="TextBlock.TextLayout"/> exposes the
    /// line boxes, so the click maps to a character without an inline control (and only safe URLs ever get here).
    /// </summary>
    private static void AttachLinks(TextBlock block, IReadOnlyList<(int Start, int Length, string Url)> links)
    {
        block.Cursor = new Cursor(StandardCursorType.Hand);
        block.PointerPressed += async (_, e) =>
        {
            var layout = block.TextLayout;
            if (layout is null) return;
            var point = e.GetPosition(block);
            var y = 0d;
            foreach (var line in layout.TextLines)
            {
                if (point.Y >= y && point.Y <= y + line.Height)
                {
                    var hit = line.GetCharacterHitFromDistance(Math.Max(0, point.X - line.Start));
                    foreach (var (start, length, url) in links)
                    {
                        if (hit.FirstCharacterIndex < start || hit.FirstCharacterIndex >= start + length) continue;
                        if (!MarkdownParser.IsNavigable(url)) return;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
                        var top = TopLevel.GetTopLevel(block);
                        if (top is not null) await top.Launcher.LaunchUriAsync(uri);
                        return;
                    }
                    return;
                }
                y += line.Height;
            }
        };
    }

    private readonly record struct MarkdownStyle(double FontSize, bool Muted);
}

/// <summary>
/// The transcript's prose host: keeps the parsed document per source and rebuilds the control tree only when the text
/// actually changed (a streamed message re-renders on coalesced flushes, never per token).
/// </summary>
public sealed class MarkdownProse : StackPanel
{
    private string? _source;

    public MarkdownProse()
    {
        Orientation = Orientation.Vertical;
        Spacing = 0;
    }

    /// <summary>Blocks of the last render, exposed so tests can inspect the parsed model.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; private set; } = [];

    public string? Source => _source;

    public void SetSource(string? source, double fontSize = 15)
    {
        if (_source == source && Blocks.Count > 0) return;
        _source = source;
        Blocks = MarkdownCache.Parse(source);
        Children.Clear();
        var rendered = MarkdownRenderer.Render(Blocks, fontSize);
        foreach (var child in rendered.Children.ToArray())
        {
            rendered.Children.Remove(child);
            Children.Add(child);
        }
    }
}
