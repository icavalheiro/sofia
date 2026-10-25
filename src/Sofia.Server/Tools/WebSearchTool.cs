using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

public sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>Searches the web (DuckDuckGo without a key, or a SearXNG instance) and lists titles, URLs and snippets.</summary>
public sealed partial class WebSearchTool(IHttpClientFactory httpFactory) : ITool
{
    private const int DefaultResults = 5;
    private const int MaxResults = 10;

    public string Name => "web_search";
    public string Description =>
        "Search the web and return a ranked list of results (title, URL, snippet). Use it for current information or documentation you do not know; " +
        "follow up with web_fetch to read a promising page.";
    public ToolKind Kind => ToolKind.Network;
    public JsonObject Parameters => Schema.Object(
        ("query", Schema.String("What to search for. Keep it short and specific."), true),
        ("max_results", Schema.Integer($"Number of results to return (1-{MaxResults}, default {DefaultResults})."), false));

    public string Summarize(JsonObject args) => Text.FirstLine(Args.Optional(args, "query", "q") ?? "", 120);

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var query = Args.Required(args, "query", "q").Trim();
        var count = Math.Clamp(Args.Int(args, "max_results") ?? DefaultResults, 1, MaxResults);
        var http = httpFactory.CreateClient("fetch");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        List<SearchHit> hits;
        try
        {
            if (ctx.Config.SearchProvider == "searxng")
            {
                if (!Uri.TryCreate(ctx.Config.SearxngUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
                    return ToolResult.Fail("Web search is set to SearXNG but no valid instance URL is configured in Settings.");
                hits = await SearxngAsync(http, baseUri, query, cts.Token);
            }
            else
            {
                hits = await DuckDuckGoAsync(http, query, cts.Token);
            }
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail($"Search failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail("Search timed out.");
        }

        if (hits.Count == 0) return ToolResult.Ok($"No results for \"{query}\". Try different keywords.");
        return ToolResult.Ok(Format(hits.Take(count).ToList(), ctx.Config.MaxToolOutput));
    }

    internal static string Format(IReadOnlyList<SearchHit> hits, int maxChars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            sb.Append(i + 1).Append(". ").AppendLine(hits[i].Title);
            sb.Append("   ").AppendLine(hits[i].Url);
            if (hits[i].Snippet.Length > 0) sb.Append("   ").AppendLine(hits[i].Snippet);
            sb.AppendLine();
        }
        return Text.Truncate(sb.ToString().TrimEnd(), maxChars);
    }

    private static async Task<List<SearchHit>> DuckDuckGoAsync(HttpClient http, string query, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}", ct);
        response.EnsureSuccessStatusCode();
        return ParseDuckDuckGo(await response.Content.ReadAsStringAsync(ct));
    }

    internal static List<SearchHit> ParseDuckDuckGo(string html)
    {
        var titles = TitleRegex().Matches(html);
        var snippets = SnippetRegex().Matches(html);
        var hits = new List<SearchHit>();
        for (var i = 0; i < titles.Count; i++)
        {
            var url = ResolveRedirect(WebUtility.HtmlDecode(titles[i].Groups["href"].Value));
            if (url is null) continue;
            // A snippet belongs to a result when it sits between this title and the next one.
            var end = i + 1 < titles.Count ? titles[i + 1].Index : html.Length;
            var snippet = snippets.FirstOrDefault(s => s.Index > titles[i].Index && s.Index < end);
            hits.Add(new SearchHit(
                WebFetchTool.HtmlToText(titles[i].Groups["text"].Value),
                url,
                snippet is null ? "" : WebFetchTool.HtmlToText(snippet.Groups["text"].Value)));
        }
        return hits;
    }

    /// <summary>DuckDuckGo wraps result links in a tracking redirect; the real target is the <c>uddg</c> parameter. Ads are dropped.</summary>
    private static string? ResolveRedirect(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)) return uri.ToString();
        if (uri.AbsolutePath != "/l/") return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "uddg") return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    internal static async Task<List<SearchHit>> SearxngAsync(HttpClient http, Uri baseUri, string query, CancellationToken ct)
    {
        // Keep a sub-path such as /searxng/ (new Uri(base, "search") would drop it).
        var search = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/search?q=" + Uri.EscapeDataString(query);
        var (jsonStatus, json) = await GetBodyAsync(http, search + "&format=json", ct);
        if (json.Length > 0)
        {
            try
            {
                return ParseSearxngJson(json);
            }
            catch (JsonException)
            {
                // Instance answers the JSON request with a page instead: fall through to the HTML parser.
            }
        }

        // Most instances leave the json engine disabled (403 on format=json), so read the result page.
        var (htmlStatus, html) = await GetBodyAsync(http, search, ct);
        if (string.IsNullOrEmpty(html))
            throw new HttpRequestException($"the instance answered nothing usable (HTTP {jsonStatus} for format=json, HTTP {htmlStatus} for the result page).");
        return ParseSearxngHtml(html);
    }

    /// <summary>GETs <paramref name="path"/> and returns the status code with its body (empty when the status is not 2xx).</summary>
    private static async Task<(int Status, string Body)> GetBodyAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseContentRead, ct);
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "");
    }

    internal static List<SearchHit> ParseSearxngJson(string body)
    {
        var hits = new List<SearchHit>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return hits;
        foreach (var item in results.EnumerateArray())
        {
            var link = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(link)) continue;
            hits.Add(new SearchHit(
                item.TryGetProperty("title", out var t) ? t.GetString() ?? link : link,
                link,
                item.TryGetProperty("content", out var c) ? Text.FirstLine(c.GetString() ?? "", 400) : ""));
        }
        return hits;
    }

    /// <summary>Reads the SearXNG result page: one <c>&lt;article class="result …"&gt;</c> per hit, title in its <c>&lt;h3&gt;</c> and snippet in <c>p.content</c>.</summary>
    internal static List<SearchHit> ParseSearxngHtml(string html)
    {
        var hits = new List<SearchHit>();
        foreach (Match article in ArticleRegex().Matches(html))
        {
            var title = TitleLinkRegex().Match(article.Groups["body"].Value);
            if (!Uri.TryCreate(WebUtility.HtmlDecode(title.Groups["href"].Value), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")) continue;
            var snippet = SnippetParaRegex().Match(article.Groups["body"].Value);
            hits.Add(new SearchHit(
                Text.FirstLine(WebFetchTool.HtmlToText(title.Groups["text"].Value), 300),
                uri.ToString(),
                snippet.Success ? Text.FirstLine(WebFetchTool.HtmlToText(snippet.Groups["text"].Value), 400) : ""));
        }
        return hits;
    }

    [GeneratedRegex("""<article[^>]*class="[^"]*\bresult\b[^"]*"[^>]*>(?<body>.*?)</article>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("""<h[34][^>]*>\s*<a[^>]*href="(?<href>[^"]+)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex("""<p class="content"[^>]*>(?<text>.*?)</p>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetParaRegex();

    [GeneratedRegex("""<a[^>]*class="result__a"[^>]*href="(?<href>[^"]+)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<a[^>]*class="result__snippet"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetRegex();
}
