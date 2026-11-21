using System.Text.Json;
using Sofia.Client;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// The transcript's mutations over <see cref="ISofiaBackend"/>: permission answers, question forms, revert, branch,
/// retry, queued-message removal and knowledge deletion. Every method returns an error message or null, the same shape
/// the web's harness functions use, so a card can show the failure inline. In fixture mode (no backend) the calls are
/// optimistic no-ops, which is what keeps the screenshot harness server-free.
/// </summary>
public sealed class TranscriptActions
{
    public ISofiaBackend? Backend { get; set; }

    /// <summary>Opens a conversation (subagent link, branch result); wired by the shell.</summary>
    public Func<string, Task>? OpenSession { get; set; }

    public async Task<string?> ResolvePermissionAsync(string sessionId, string requestId, string decision, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.ReplyPermissionAsync(sessionId, requestId, decision, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> AnswerQuestionAsync(string sessionId, string formId, IReadOnlyList<JsonElement?> answers,
        CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.AnswerAsync(sessionId, formId, answers, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> RevertAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.RevertAsync(sessionId, messageId, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> BranchAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            var result = await Backend.BranchAsync(sessionId, messageId, ct);
            var id = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("id", out var value)
                ? value.GetString()
                : null;
            if (id is not null && OpenSession is not null) await OpenSession(id);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> RetryAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.RetryAsync(sessionId, messageId, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> RemoveQueuedAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.RemoveQueuedAsync(sessionId, messageId, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> DeleteKnowledgeAsync(string noteId, CancellationToken ct = default)
    {
        if (Backend is null) return null;
        try
        {
            await Backend.DeleteKnowledgeAsync(noteId, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
