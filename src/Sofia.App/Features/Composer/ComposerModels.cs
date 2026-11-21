using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Composer;

/// <summary>Which upward popover the composer holds open; at most one at a time (the web's shared popover lock).</summary>
public enum ComposerMenu
{
    None,
    Agent,
    Model,
    Permission,
    Reasoning,
    Mode,
}

/// <summary>Semantic colour of a chip/option, mapping to the <c>--sofia-*</c> tokens like <c>SofiaPopover.vue</c>.</summary>
public enum ComposerTone
{
    Primary,
    Info,
    Detail,
    Warn,
    Err,
}

/// <summary>A permission or reasoning choice (web <c>PERMISSION_MODES</c> / <c>REASONING_MODES</c>).</summary>
public sealed record ComposerOption(string Value, string Label, string Description, string Icon, ComposerTone? Tone = null);

/// <summary>One row of the model popover (<c>GET /api/models</c>).</summary>
public sealed record ComposerModel(
    string Id,
    string Label,
    string Description,
    string Icon,
    string? Badge = null);

/// <summary>One agent of the catalog (<c>GET /api/agents</c>), primary or subagent.</summary>
public sealed record ComposerAgent(string Id, string Name, string Description, string Mode, string Source)
{
    public bool IsSubagent => Mode != "primary";

    /// <summary>The web shows the first description line, cut at 90 characters.</summary>
    public string ShortDescription
    {
        get
        {
            var line = Description.Split('\n')[0].Trim();
            return line.Length > 90 ? line[..90] : line;
        }
    }
}

/// <summary>One server command of the <c>/</c> palette (<c>GET /api/commands</c>).</summary>
public sealed record ComposerCommand(string Name, string Description, string Source);

/// <summary>A past session attached to the next prompt as a hidden hint (web <c>SessionReference</c>).</summary>
public sealed record ComposerReference(string Id, string Title);

/// <summary>One row of the <c>@</c> mention popup: a section header or a selectable item.</summary>
public sealed class ComposerMentionRow
{
    private ComposerMentionRow(string? header, ComposerAgent? agent, ComposerReference? session, string? file)
    {
        Header = header;
        Agent = agent;
        Session = session;
        File = file;
    }

    public string? Header { get; }

    public ComposerAgent? Agent { get; }

    public ComposerReference? Session { get; }

    public string? File { get; }

    public bool IsHeader => Header is not null;

    public static ComposerMentionRow Section(string label) => new(label, null, null, null);

    public static ComposerMentionRow ForAgent(ComposerAgent agent) => new(null, agent, null, null);

    public static ComposerMentionRow ForSession(ComposerReference session) => new(null, null, session, null);

    public static ComposerMentionRow ForFile(string path) => new(null, null, null, path);

    /// <summary>Text inserted into the composer when the row is picked (sessions attach a reference instead).</summary>
    public string? Insertion => Agent is { } agent ? $"@{agent.Id} " : File is { } file ? $"@{file} " : null;

    public override string ToString() => Header ?? Agent?.Id ?? Session?.Title ?? File ?? "";
}

/// <summary>
/// One attachment in the composer strip. The web keeps a <c>File</c> and an object URL; the app keeps the picked
/// path, uploads on submit (the web's <c>uploadedId ?? upload()</c>) and remembers the server id once it has one so a
/// re-submit never uploads twice.
/// </summary>
public sealed class ComposerAttachment
{
    public string LocalId { get; init; } = "a_" + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "";

    public string Mime { get; set; } = "application/octet-stream";

    public long Bytes { get; set; }

    /// <summary>'image' | 'video' | 'audio' | 'file'.</summary>
    public string Kind { get; set; } = "file";

    public bool IsImage => Kind == "image";

    public string Size => ComposerFormat.Bytes(Bytes);

    /// <summary>Desktop source path (null when the attachment came back from a message, i.e. already uploaded).</summary>
    public string? Path { get; set; }

    /// <summary>Bytes handed over by the browser head (044), where there is no filesystem path.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Image URL for the 24x24 thumbnail (local file or the server URL once uploaded).</summary>
    public string? PreviewUrl { get; set; }

    /// <summary>Id returned by <c>POST /api/attachments</c>; null until the first upload.</summary>
    public string? UploadedId { get; set; }

    /// <summary>Upload failure text; the chip stays in the strip so the user never loses the file (spec risk).</summary>
    public string? Error { get; set; }

    public ComposerAttachment Clone() => new()
    {
        LocalId = LocalId,
        Name = Name,
        Mime = Mime,
        Bytes = Bytes,
        Kind = Kind,
        Path = Path,
        Content = Content,
        PreviewUrl = PreviewUrl,
        UploadedId = UploadedId,
    };

    /// <summary>Stream handed to <c>POST /api/attachments</c>: the picked file on desktop, the in-memory bytes in the browser.</summary>
    public Stream OpenRead() => Content is not null
        ? new MemoryStream(Content, writable: false)
        : Path is not null && File.Exists(Path)
            ? File.OpenRead(Path)
            : throw new FileNotFoundException($"Attachment '{Name}' is no longer available.", Path);

    /// <summary>Rebuilds a chip for a message attachment put back into the composer (web <c>restoreToComposer</c>).</summary>
    public static ComposerAttachment FromMessage(Attachment attachment, string? previewUrl) => new()
    {
        Name = attachment.Name,
        Mime = attachment.Mime,
        Bytes = attachment.Bytes,
        Kind = attachment.Kind,
        UploadedId = attachment.Id,
        PreviewUrl = previewUrl,
    };
}
