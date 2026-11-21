using Sofia.Contracts.Domain;

namespace Sofia.App.Shell;

/// <summary>
/// Deterministic sample data for <c>--fixture</c> and the screenshot harness (no server, no <c>~/.sofia</c> reads). It
/// mirrors the shape the engine returns from <c>/api/workspaces</c> so the shell chrome is exercised exactly as it will
/// be with live data: pinned and working sessions, a permission prompt, a question, a chat group and a subagent.
/// </summary>
public static class Fixture
{
    public static readonly DateTime Base = new(2026, 11, 20, 21, 40, 0, DateTimeKind.Utc);

    public static List<WorkspaceView> Workspaces() =>
    [
        new()
        {
            Id = "ws_sofia",
            Name = "sofia",
            Path = @"D:\repos\opensource\sofia",
            Kind = "folder",
            Icon = "code",
            Expanded = true,
            Order = 0,
            Conversations =
            [
                Session("s_1", "ws_sofia", "Avalonia shell scaffold", minutes: 0, pinned: true),
                Session("s_2", "ws_sofia", "Design tokens + Lucide icon port", minutes: 12),
                Session("s_3", "ws_sofia", "Working border motion parity", minutes: 47),
                Session("s_4", "ws_sofia", "Review the SQLite ordering fix", minutes: 180),
            ],
        },
        new()
        {
            Id = "ws_site",
            Name = "website",
            Path = @"D:\repos\ivans\website",
            Kind = "folder",
            Icon = "globe",
            Expanded = true,
            Order = 1,
            Conversations =
            [
                Session("s_5", "ws_site", "Fix the contact form validation", minutes: 320),
                Session("s_6", "ws_site", "Publish the new pricing page", minutes: 1500, pinned: true),
            ],
        },
        new()
        {
            Id = "ws_chat",
            Name = "Chat",
            Path = "",
            Kind = "chat",
            Icon = "chat",
            Expanded = true,
            Order = 2,
            Conversations =
            [
                Session("s_7", "ws_chat", "What can we build next?", minutes: 2000),
            ],
        },
    ];

    /// <summary>Session id → status, exactly like <c>GET /api/sessions/statuses</c> for non-idle sessions.</summary>
    public static Dictionary<string, string> Statuses() => new(StringComparer.Ordinal)
    {
        ["s_1"] = "working",
        ["s_3"] = "waiting",
        ["s_5"] = "permission",
        ["s_7"] = "question",
    };

    private static Session Session(string id, string workspaceId, string title, int minutes, bool pinned = false) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        Title = title,
        CreatedAt = Base.AddDays(-2).AddMinutes(minutes),
        UpdatedAt = Base.AddMinutes(-minutes),
        ModelId = "unsloth/nvidia-nemotron-3.5-bolt-e4b",
        Agent = "build",
        PermissionMode = "ask",
        ReasoningMode = "medium",
        Status = "idle",
        Pinned = pinned,
        Usage = new Usage { Input = 18_420 + minutes, Output = 3_120 + minutes / 2, CacheRead = 9_600, Cost = 0.0184 },
        LastTurnMs = 8_140,
        LastTurnAt = Base.AddMinutes(-minutes),
    };
}
