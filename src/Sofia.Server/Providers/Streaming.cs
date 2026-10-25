using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Sofia.Server.Providers;

/// <summary>Reads a Server-Sent Events stream as (event, data) pairs.</summary>
internal static class Sse
{
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                if (data.Length > 0) yield return (eventName, data.ToString());
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (eventName, data.ToString());
                eventName = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
    }

    public static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
                if (error.TryGetProperty("message", out var message)) return message.GetString() ?? body;
            }
            if (root.TryGetProperty("message", out var msg)) return msg.GetString() ?? body;
            if (root.TryGetProperty("detail", out var detail)) return detail.ToString();
        }
        catch (JsonException)
        {
            // Plain-text error bodies are returned as-is.
        }
        return string.IsNullOrWhiteSpace(body) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : body.Trim();
    }
}

/// <summary>
/// Splits inline &lt;think&gt;…&lt;/think&gt; blocks out of a content stream, for local
/// models whose chat template emits reasoning as plain text.
/// </summary>
internal sealed class ThinkTagSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private readonly StringBuilder _pending = new();
    private bool _inThink;
    private bool _sawContent;

    public IEnumerable<StreamEvent> Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new List<StreamEvent>();
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _inThink ? Close : Open;
            var idx = text.IndexOf(tag, StringComparison.Ordinal);
            // Only an opening tag at the very start of the answer counts as reasoning.
            if (!_inThink && (_sawContent || (idx > 0 && text[..idx].Trim().Length > 0)))
            {
                Emit(output, text);
                _pending.Clear();
                break;
            }
            if (idx >= 0)
            {
                Emit(output, text[..idx]);
                _pending.Remove(0, idx + tag.Length);
                _inThink = !_inThink;
                if (!_inThink) TrimLeadingNewlines();
                continue;
            }
            var keep = PartialSuffix(text, tag);
            Emit(output, text[..^keep]);
            _pending.Remove(0, text.Length - keep);
            break;
        }
        return output;
    }

    public IEnumerable<StreamEvent> Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        if (rest.Length == 0) return [];
        return [_inThink ? new ReasoningDelta(rest) : new TextDelta(rest)];
    }

    private void Emit(List<StreamEvent> output, string text)
    {
        if (text.Length == 0) return;
        if (_inThink)
        {
            output.Add(new ReasoningDelta(text));
        }
        else
        {
            if (!_sawContent && text.Trim().Length == 0) return;
            _sawContent = true;
            output.Add(new TextDelta(text));
        }
    }

    private void TrimLeadingNewlines()
    {
        while (_pending.Length > 0 && (_pending[0] == '\n' || _pending[0] == '\r')) _pending.Remove(0, 1);
    }

    private static int PartialSuffix(string text, string tag)
    {
        for (var len = Math.Min(tag.Length - 1, text.Length); len > 0; len--)
            if (text.EndsWith(tag[..len], StringComparison.Ordinal)) return len;
        return 0;
    }
}
