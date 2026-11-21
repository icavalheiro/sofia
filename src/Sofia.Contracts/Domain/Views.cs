namespace Sofia.Contracts.Domain;

// Wire DTOs that are not entities but are part of the contract both hosts return verbatim.

/// <summary>A workspace as the sidebar consumes it, with its conversations when the endpoint lists it.</summary>
public sealed class WorkspaceView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "folder";
    public string Icon { get; set; } = "folder";
    public string ModelId { get; set; } = "";
    public bool Expanded { get; set; }
    public int Order { get; set; }
    public List<Session>? Conversations { get; set; }

    public static WorkspaceView From(Workspace w, List<Session>? sessions) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Path = w.Path,
        Kind = w.Kind,
        Icon = w.Icon,
        ModelId = w.ModelId,
        Expanded = w.Expanded,
        Order = w.Order,
        Conversations = sessions,
    };
}

/// <summary>One file changed by a conversation (or by the working tree), as the changes panel lists it.</summary>
public sealed record FileDiff(string File, string Patch, int Additions, int Deletions, string Status);

/// <summary>Totals shown in the stats panel.</summary>
public sealed record StatsView(int Sessions, int Prompts, int Steps, StatsTokens Tokens, double Cost, int ActiveDays, int Streak);

public sealed record StatsTokens(long Input, long Output);

/// <summary>A domain event as it arrives on a client transport.</summary>
public sealed record SofiaEventDto(string Type, string? SessionId, string? WorkspaceId, System.Text.Json.JsonElement Data);
