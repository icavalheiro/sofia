using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript.CardViewModels;

/// <summary>
/// Compaction notice (web <c>.compact-notice</c>): a system line, not a chat bubble. The "running" text pulses
/// 1.4s ease-in-out (motion contract #15); a finished notice expands to the generated summary.
/// </summary>
public sealed class CompactionCardViewModel(CompactionNotice notice)
{
    public string Status => notice.Status;

    public bool Running => notice.Status == "running";

    public bool Failed => notice.Status == "failed";

    public string Icon => Failed ? "alert" : "book";

    public bool Expanded { get; set; }

    public string Text
    {
        get
        {
            var template = notice.Status switch
            {
                "running" => Localization.T(notice.Auto ? "chat.compact.runningAuto" : "chat.compact.running"),
                "done" => Localization.T("chat.compact.done"),
                "failed" => Localization.T("chat.compact.failed"),
                _ => Localization.T("chat.compact.skipped"),
            };
            return template
                .Replace("{before}", notice.Before.ToString("N0"))
                .Replace("{after}", (notice.After ?? 0).ToString("N0"))
                .Replace("{error}", notice.Error ?? "");
        }
    }

    /// <summary>The expandable body (the generated summary, or the failure reason).</summary>
    public string Body => notice.Status == "failed" ? notice.Error ?? "" : notice.Result ?? "";

    public bool CanExpand => Body.Length > 0;

    public void Toggle() => Expanded = !Expanded;
}
