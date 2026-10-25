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

    private static async Task<List<SearchHit>> SearxngAsync(HttpClient http, Uri baseUri, string query, CancellationToken ct)
    {
        var url = new Uri(baseUri, $"search?q={Uri.EscapeDataString(query)}&format=json");
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var hits = new List<SearchHit>();
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

    [GeneratedRegex("""<a[^>]*class="result__a"[^>]*href="(?<href>[^"]+)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<a[^>]*class="result__snippet"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetRegex();
}
