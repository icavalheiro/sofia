using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Search;

/// <summary>One matched message inside a session result.</summary>
public sealed record SessionSearchHit(string MessageId, long Seq, string Role, DateTime CreatedAt, string Snippet, int[][] Highlights);

/// <summary>One session's best matches (up to 3 snippets), ranked by the best hit's score.</summary>
public sealed record SessionSearchResult(string SessionId, string WorkspaceId, string Title, DateTime UpdatedAt, bool Archived, double Score, List<SessionSearchHit> Hits);

/// <summary>
/// In-memory inverted index over user/assistant message text with BM25 ranking.
/// Rebuilt from LiteDB on startup and kept current by <see cref="SearchIndexHandler"/>;
/// session metadata (title, archived, parent) is resolved at query time so it never goes stale.
/// </summary>
public interface ISessionSearch
{
    Task RebuildAsync(SofiaDb db, CancellationToken ct = default);
    void Upsert(Message message);
    void Remove(string messageId);
    void RemoveSession(string sessionId);
    IReadOnlyList<SessionSearchResult> Search(SofiaDb db, string query, string? workspaceId, bool includeArchived, bool includeSubagents, int limit);
}

public sealed class SessionSearchIndex : ISessionSearch
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, Doc> _docs = new(StringComparer.Ordinal);
    // term -> (messageId -> term frequency)
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _docLength = new(StringComparer.Ordinal);
    private int _docCount;
    private double _avgDocLength = 1.0;

    /// <summary>Longer text is never indexed whole: the head of the message carries the signal.</summary>
    public const int MaxIndexedChars = 32 * 1024;

    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int SnippetRadius = 80;
    private const int SnippetWidth = 160;

    public Task RebuildAsync(SofiaDb db, CancellationToken ct = default)
    {
        var messages = db.Messages.FindAll().ToList();
        _lock.EnterWriteLock();
        try
        {
            _docs.Clear();
            _postings.Clear();
            _docLength.Clear();
            _docCount = 0;
            _avgDocLength = 1.0;
        }
        finally { _lock.ExitWriteLock(); }

        foreach (var m in messages)
        {
            ct.ThrowIfCancellationRequested();
            Upsert(m);
        }
        return Task.CompletedTask;
    }

    public void Upsert(Message message)
    {
        if (message is null || message.Role is not ("user" or "assistant")) return;
        var text = message.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxIndexedChars) text = text[..MaxIndexedChars];

        var tokens = Tokenize(text).ToList();
        if (tokens.Count == 0) return;

        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;

        _lock.EnterWriteLock();
        try
        {
            // Replace any existing entry for this message id.
            if (_docs.Remove(message.Id))
            {
                _docLength.Remove(message.Id);
                foreach (var posting in _postings.Values) posting.Remove(message.Id);
            }

            _docs[message.Id] = new Doc
            {
                MessageId = message.Id,
                SessionId = message.SessionId,
                Role = message.Role,
                Seq = message.Seq,
                CreatedAt = message.CreatedAt,
                Text = text,
            };
            _docLength[message.Id] = tokens.Count;

            foreach (var (term, count) in tf)
            {
                if (!_postings.TryGetValue(term, out var posting))
                {
                    posting = new Dictionary<string, int>(StringComparer.Ordinal);
                    _postings[term] = posting;
                }
                posting[message.Id] = count;
            }

            RefreshStats();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Remove(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        _lock.EnterWriteLock();
        try
        {
            if (!_docs.Remove(messageId)) return;
            _docLength.Remove(messageId);
            foreach (var posting in _postings.Values) posting.Remove(messageId);
            // Drop empty postings to keep the index tidy.
            var empty = _postings.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var key in empty) _postings.Remove(key);
            RefreshStats();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        List<string> ids;
        _lock.EnterReadLock();
        try { ids = _docs.Values.Where(d => d.SessionId == sessionId).Select(d => d.MessageId).ToList(); }
        finally { _lock.ExitReadLock(); }
        foreach (var id in ids) Remove(id);
    }

    public IReadOnlyList<SessionSearchResult> Search(SofiaDb db, string query, string? workspaceId, bool includeArchived, bool includeSubagents, int limit)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0) return [];

        // Quoted phrases are matched verbatim (post-hoc); the rest becomes terms.
        var phrases = new List<string>();
        foreach (Match m in Regex.Matches(query, "\"([^\"]+)\""))
            if (!string.IsNullOrWhiteSpace(m.Groups[1].Value)) phrases.Add(m.Groups[1].Value.Trim());
        var withoutQuotes = Regex.Replace(query, "\"[^\"]*\"", " ");
        var terms = Tokenize(withoutQuotes).Distinct().ToList();
        if (terms.Count == 0 && phrases.Count == 0) return [];

        _lock.EnterReadLock();
        try
        {
            // Candidate message ids: union of the exact postings plus (for the last term) every posting whose
            // term starts with it — one pass over the index instead of one per candidate.
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in terms.Distinct(StringComparer.Ordinal))
                if (_postings.TryGetValue(t, out var exact))
                    foreach (var id in exact.Keys) candidates.Add(id);
            if (terms.Count > 0)
            {
                var prefix = terms[^1];
                foreach (var kv in _postings)
                    if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                        foreach (var id in kv.Value.Keys) candidates.Add(id);
            }
            if (terms.Count == 0) // phrase-only query: every doc is a candidate.
                foreach (var d in _docs.Values) candidates.Add(d.MessageId);

            var n = Math.Max(1, _docCount);
            var avgLen = _avgDocLength;
            var scored = new List<(Doc Doc, double Score)>();
            foreach (var id in candidates)
            {
                if (!_docs.TryGetValue(id, out var doc)) continue;

                var session = db.Sessions.FindById(doc.SessionId);
                if (session is null) continue;
                if (workspaceId is not null && session.WorkspaceId != workspaceId) continue;
                if (!includeArchived && session.Archived) continue;
                if (!includeSubagents && session.ParentId is not null) continue;

                // Phrase filter: the folded text must contain every phrase.
                var folded = Fold(doc.Text);
                if (phrases.Any(ph => !folded.Contains(Fold(ph), StringComparison.Ordinal))) continue;

                var dl = _docLength.TryGetValue(id, out var l) ? l : 1;
                var score = ScoreDoc(id, terms, n, avgLen, dl);
                if (score <= 0 && terms.Count > 0) continue;

                scored.Add((doc, score));
            }

            // Group by session: best hit first, up to three snippets per session.
            var results = new List<SessionSearchResult>();
            foreach (var group in scored.GroupBy(g => g.Doc.SessionId))
            {
                var session = db.Sessions.FindById(group.Key);
                if (session is null) continue;
                // Ties on score fall back to the newest hit first, so ordering never depends on hash order.
                var ordered = group.OrderByDescending(g => g.Score).ThenByDescending(g => g.Doc.Seq).ToList();
                var hits = ordered.Take(3).Select(h =>
                {
                    var (snippet, highlights) = SnippetWithHighlights(h.Doc.Text, terms, phrases);
                    return new SessionSearchHit(h.Doc.MessageId, h.Doc.Seq, h.Doc.Role, h.Doc.CreatedAt, snippet, highlights);
                }).ToList();
                results.Add(new SessionSearchResult(session.Id, session.WorkspaceId, session.Title, session.UpdatedAt, session.Archived, ordered[0].Score, hits));
            }

            // Ties on score fall back to the most recently updated session, then id, so ordering is stable.
            return results.OrderByDescending(r => r.Score)
                          .ThenByDescending(r => r.UpdatedAt)
                          .ThenBy(r => r.SessionId, StringComparer.Ordinal)
                          .Take(limit > 0 ? limit : int.MaxValue).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    private double ScoreDoc(string id, List<string> terms, int n, double avgLen, int dl)
    {
        if (terms.Count == 0) return 1.0; // phrase-only: constant score, order falls back to the group sort.

        var score = 0.0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < terms.Count; i++)
        {
            var t = terms[i];
            if (i == terms.Count - 1)
            {
                // Last term expands by prefix: "sess" matches "session", "sessions", "sessor".
                foreach (var (term, posting) in _postings)
                {
                    if (!term.StartsWith(t, StringComparison.Ordinal) || seen.Contains(term)) continue;
                    seen.Add(term);
                    score += TermScore(posting, id, n, avgLen, dl);
                }
            }
            else
            {
                if (seen.Contains(t)) continue;
                seen.Add(t);
                if (_postings.TryGetValue(t, out var posting)) score += TermScore(posting, id, n, avgLen, dl);
            }
        }
        return score;
    }

    private static double TermScore(Dictionary<string, int> posting, string docId, int n, double avgLen, int dl)
    {
        if (!posting.TryGetValue(docId, out var tf)) return 0.0;
        var df = posting.Count;
        var idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);
        var denom = tf + K1 * (1.0 - B + B * dl / avgLen);
        return idf * (tf * (K1 + 1.0)) / denom;
    }

    private void RefreshStats()
    {
        _docCount = _docs.Count;
        var total = 0;
        foreach (var l in _docLength.Values) total += l;
        _avgDocLength = _docCount == 0 ? 1.0 : Math.Max(1.0, (double)total / _docCount);
    }

    /// <summary>
    /// A ~160-char window around the first hit, with [start, length] highlight ranges relative to the snippet.
    /// Ranges are merged so they never overlap. Terms match case-insensitively and fold diacritics; the last
    /// query term also highlights its prefix expansions ("sess" → "session").
    /// </summary>
    internal static (string Snippet, int[][] Highlights) SnippetWithHighlights(string text, List<string> terms, List<string> phrases)
    {
        var first = -1;
        foreach (var t in terms.Concat(phrases))
        {
            var idx = FindFolded(text, t);
            if (idx >= 0 && (first < 0 || idx < first)) first = idx;
        }
        if (first < 0 && terms.Count > 0)
        {
            // The last term may only have matched by prefix: find the first word that starts with it.
            var m = new Regex(Regex.Escape(terms[^1]) + @"\w*", RegexOptions.IgnoreCase).Match(text);
            if (m.Success) first = m.Index;
        }
        if (first < 0) first = 0;

        var start = Math.Max(0, first - SnippetRadius);
        var end = Math.Min(text.Length, start + SnippetWidth);
        if (end - start < SnippetWidth && start > 0) start = Math.Max(0, end - SnippetWidth);
        var snippet = text.Substring(start, end - start);

        var highlights = new List<int[]>();
        // Plain occurrences of every term and phrase (diacritic/case folded).
        foreach (var needle in terms.Concat(phrases).Distinct(StringComparer.Ordinal))
            foreach (var range in FindFoldedRanges(snippet, needle)) highlights.Add(range);
        // Prefix expansions of the last term.
        if (terms.Count > 0)
        {
            var pattern = new Regex(Regex.Escape(terms[^1]) + @"\w*", RegexOptions.IgnoreCase);
            foreach (Match m in pattern.Matches(snippet)) highlights.Add([m.Index, m.Length]);
        }

        return (snippet, Merge(highlights));
    }

    private static int[][] Merge(List<int[]> ranges)
    {
        var sorted = ranges.OrderBy(r => r[0]).ThenByDescending(r => r[1]).ToList();
        var merged = new List<int[]>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r[0] <= merged[^1][0] + merged[^1][1])
                merged[^1] = [merged[^1][0], Math.Max(merged[^1][0] + merged[^1][1], r[0] + r[1]) - merged[^1][0]];
            else merged.Add(r);
        }
        return [.. merged];
    }

    /// <summary>All folded (case/diacritic-insensitive) occurrences of `needle` in `text`, as [start, length] pairs.</summary>
    internal static IEnumerable<int[]> FindFoldedRanges(string text, string needle)
    {
        var foldedNeedle = Fold(needle);
        if (foldedNeedle.Length == 0 || text.Length == 0) yield break;
        var (foldedText, map) = FoldMap(text);
        for (var idx = 0; idx + foldedNeedle.Length <= foldedText.Length; idx++)
        {
            if (!foldedText.AsSpan(idx, foldedNeedle.Length).SequenceEqual(foldedNeedle)) continue;
            var s = map[idx];
            var e = map[idx + foldedNeedle.Length - 1] + 1;
            yield return [s, e - s];
            idx += foldedNeedle.Length - 1; // resume after this occurrence
        }
    }

    /// <summary>Index of `needle` in `text` with diacritic and case folding; -1 when absent.</summary>
    internal static int FindFolded(string text, string needle)
    {
        foreach (var r in FindFoldedRanges(text, needle)) return r[0];
        return -1;
    }

    /// <summary>
    /// Folds `text` (lowercase, diacritics removed) and maps each folded character back to the original index of
    /// its base character, so folded match positions can be turned into original [start, length] ranges.
    /// </summary>
    internal static (string Folded, int[] Map) FoldMap(string text)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            foreach (var c in char.ToString(text[i]).Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.ToLowerInvariant(c));
                map.Add(i);
            }
        }
        return (sb.ToString(), [.. map]);
    }

    /// <summary>Lowercase + strip diacritics (café → cafe).</summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits folded text on non-letter/digit runs. CJK has no word separators, so runs of ideographs are
    /// additionally indexed as character bigrams ("搜索" → 搜索) to keep them searchable.
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var folded = Fold(text);
        var tokens = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < folded.Length; i++)
        {
            var c = folded[i];
            if (!char.IsLetterOrDigit(c) || (sb.Length > 0 && IsIdeographic(c) != IsIdeographic(sb[^1])))
            {
                // A script switch (latin/CJK boundary) ends the current run, so "abc搜索def"
                // indexes as abc / bigrams of 搜索 / def instead of one mixed token.
                Flush();
                if (!char.IsLetterOrDigit(c)) continue;
            }
            sb.Append(c);
        }
        Flush();

        void Flush()
        {
            if (sb.Length == 0) return;
            var token = sb.ToString();
            sb.Clear();
            if (token.Length >= 2 && token.All(IsIdeographic))
                for (var i = 0; i + 1 < token.Length; i++) tokens.Add(token.Substring(i, 2));
            else tokens.Add(token);
        }

        foreach (var t in tokens) yield return t;
    }

    private static bool IsIdeographic(char c) =>
        c >= '\u4e00' && c <= '\u9fff' || // CJK unified ideographs
        c >= '\u3400' && c <= '\u4dbf' || // extension A
        c >= '\u3040' && c <= '\u30ff';   // hiragana / katakana
}

internal sealed class Doc
{
    public string MessageId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Role { get; set; } = "";
    public long Seq { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Rebuilds the index from LiteDB once per startup (background, never blocks boot).</summary>
public sealed class SearchIndexRebuildService(ISessionSearch search, SofiaDb db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await search.RebuildAsync(db, stoppingToken); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch { /* best effort: the event handler keeps the index current afterwards */ }
    }
}
