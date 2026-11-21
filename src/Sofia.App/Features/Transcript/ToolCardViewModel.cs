using Sofia.App.Services;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// One tool invocation card (web <c>.tool</c>): mono tool name, status pill, duration, a foldable body with the
/// summary, the subagent link, the input/output/error blocks (200px cap, 10px uppercase labels), image outputs and the
/// +green/−red diff of <c>edit</c>/<c>write</c>/<c>apply_patch</c>.
/// </summary>
public sealed class ToolCardViewModel
{
    /// <summary>Tools that render their own card in the message (the question prompt): folded by default.</summary>
    private static readonly HashSet<string> SelfCardTools = new(StringComparer.Ordinal) { "question" };

    private string? _diffSource;

    public ToolCardViewModel(ToolCall call)
    {
        Call = call;
        Expanded = !SelfCardTools.Contains(call.Name);
    }

    public ToolCall Call { get; }

    public string Id => Call.Id;

    public string Name => Call.Name;

    public string Status => Call.Status;

    public bool IsRunning => Call.Status == "running";

    public string Summary => Call.Summary;

    public string? Input => Call.Input;

    public string? Output => Call.Output;

    public string? Error => Call.Error;

    public string Duration => Call.Duration;

    public string? ChildSessionId => Call.ChildSessionId;

    public string? ChildModelId => string.IsNullOrEmpty(Call.ChildModelId) ? null : Call.ChildModelId.Split('/').Last();

    public string ChildMode => Call.ChildBackground ? "background" : "blocking";

    public IReadOnlyList<Attachment> Images => Call.Images ?? [];

    public bool HasChild => !string.IsNullOrEmpty(Call.ChildSessionId);

    public bool HasInput => !string.IsNullOrEmpty(Call.Input);

    public bool HasOutput => !string.IsNullOrEmpty(Call.Output);

    public bool HasError => !string.IsNullOrEmpty(Call.Error);

    public bool HasImages => Images.Count > 0;

    /// <summary>Fold state of the whole card (a user toggle flips the tool's default).</summary>
    public bool Expanded { get; set; }

    public bool InputOpen { get; set; }

    public bool OutputOpen { get; set; }

    public string StatusLabel => Localization.T(Status switch
    {
        "success" => "tool.success",
        "running" => "tool.running",
        _ => "tool.fail",
    });

    public string StatusIcon => Status switch
    {
        "success" => "check",
        "running" => "clock",
        _ => "x",
    };

    /// <summary>ok | err | running: the 2px left rule and the pill colours.</summary>
    public string Tone => Status switch
    {
        "success" => "ok",
        "running" => "running",
        _ => "err",
    };

    /// <summary>The Lucide key for the tool family (web <c>toolIcon</c>).</summary>
    public string Icon
    {
        get
        {
            var name = Call.Name;
            if (name.StartsWith("read", StringComparison.Ordinal) || name.StartsWith("write", StringComparison.Ordinal) ||
                name.Contains("file", StringComparison.Ordinal)) return "file";
            if (name.Contains("command", StringComparison.Ordinal) || name.Contains("run", StringComparison.Ordinal) ||
                name.Contains("shell", StringComparison.Ordinal)) return "terminal";
            if (name.Contains("search", StringComparison.Ordinal) || name.Contains("fetch", StringComparison.Ordinal) ||
                name == "browser") return "globe";
            if (name.Contains("edit", StringComparison.Ordinal) || name == "apply_patch") return "code";
            return "box";
        }
    }

    /// <summary>Diff rows when this is a successful file-writing tool whose output carries a hunk (cached per output).</summary>
    public IReadOnlyList<DiffRow>? DiffRows
    {
        get
        {
            if (Status != "success" || !UnifiedDiff.DiffTools.Contains(Call.Name) || string.IsNullOrEmpty(Output)) return null;
            if (_diffSource == Output) return _diffRows;
            _diffSource = Output;
            _diffRows = UnifiedDiff.FromToolOutput(Output);
            return _diffRows;
        }
    }

    private IReadOnlyList<DiffRow>? _diffRows;

    public bool IsDiff => DiffRows is { Count: > 0 };

    public string Toggle() 
    {
        Expanded = !Expanded;
        return Expanded ? "chevronUp" : "chevronDown";
    }

    /// <summary>Keeps the user's fold/open state when a <c>message.updated</c> replaces the tool call.</summary>
    public void CopyStateFrom(ToolCardViewModel previous)
    {
        Expanded = previous.Expanded;
        InputOpen = previous.InputOpen;
        OutputOpen = previous.OutputOpen;
    }
}
