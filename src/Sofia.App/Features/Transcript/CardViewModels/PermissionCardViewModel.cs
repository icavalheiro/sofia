using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>One button of the permission card: everything but the last choice is an allow (web colour rule).</summary>
public sealed record PermissionChoice(string Label, bool IsDeny);

/// <summary>
/// Permission request (web <c>.perm-card</c>): warn border on warn-soft while pending, a neutral card once resolved.
/// The reply posts <c>POST /api/sessions/{id}/permissions/{requestId}</c> with once/always/reject.
/// </summary>
public sealed class PermissionCardViewModel(PermissionCard card)
{
    public PermissionCard Card { get; } = card;

    public string RequestId => Card.RequestId;

    public string Tool => Card.Tool;

    public string Description => Card.Description;

    public bool Resolved => !string.IsNullOrEmpty(Card.Resolved);

    public bool Allowed => Resolved && !Card.Resolved!.ToLowerInvariant().StartsWith("deny", StringComparison.Ordinal);

    /// <summary>head tone: warn while pending, ok when allowed, err when denied.</summary>
    public string Tone => !Resolved ? "warn" : Allowed ? "ok" : "err";

    public string Title => !Resolved
        ? Localization.T("perm.title")
        : Localization.T(Allowed ? "perm.approved" : "perm.denied");

    public string ResolvedText => Card.Resolved ?? "";

    public IReadOnlyList<PermissionChoice> Choices => Card.Choices
        .Select((choice, index) => new PermissionChoice(choice, index == Card.Choices.Count - 1))
        .ToList();

    /// <summary>Web <c>permissionDecision</c>: label → wire decision.</summary>
    public static string Decision(string choice)
    {
        var normalized = choice.ToLowerInvariant();
        if (normalized.Contains("always") || normalized.Contains("session")) return "always";
        if (normalized.Contains("deny") || normalized.Contains("reject")) return "reject";
        return "once";
    }
}
