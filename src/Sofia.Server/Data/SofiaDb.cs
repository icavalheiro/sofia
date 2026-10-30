using LiteDB;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Data;

/// <summary>LiteDB store. One shared instance; LiteDB serializes access internally.</summary>
public sealed class SofiaDb : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly string? _compactMarker;
    private readonly ILogger<SofiaDb>? _log;
    private long _seq;
    private readonly object _seqGate = new();

    public SofiaDb(SofiaOptions options, ILogger<SofiaDb>? log = null)
        : this(new ConnectionString { Filename = options.DbPath, Connection = ConnectionType.Direct }, log)
    {
    }

    public SofiaDb(ConnectionString connection, ILogger<SofiaDb>? log = null)
    {
        _log = log;
        _compactMarker = string.IsNullOrEmpty(connection.Filename) || connection.Filename == ":memory:" || connection.Filename == ":temp:"
            ? null
            : connection.Filename + ".compact";
        RebuildIfFlagged(_compactMarker, connection);
        var mapper = new BsonMapper { SerializeNullValues = false };
        mapper.TrimWhitespace = false;
        mapper.EmptyStringToNull = false;
        _db = new LiteDatabase(connection, mapper);
        _db.UtcDate = true;

        Workspaces = _db.GetCollection<Workspace>("workspaces");
        Sessions = _db.GetCollection<Session>("sessions");
        Messages = _db.GetCollection<Message>("messages");
        Providers = _db.GetCollection<ProviderConfig>("providers");
        McpServers = _db.GetCollection<McpServerConfig>("mcp");
        Snapshots = _db.GetCollection<FileSnapshot>("snapshots");
        Attachments = _db.GetCollection<Attachment>("attachments");
        Tokens = _db.GetCollection<AccessToken>("tokens");
        Config = _db.GetCollection<ServerConfig>("config");
        Knowledge = _db.GetCollection<KnowledgeNote>("knowledge");
        Catalog = _db.GetCollection<CatalogCache>("catalog");

        Memory = _db.GetCollection<MemoryEntry>("memory");

        Memory.EnsureIndex(x => x.WorkspaceId);
        Knowledge.EnsureIndex(x => x.WorkspaceId);
        Knowledge.EnsureIndex(x => x.Agent);
        Sessions.EnsureIndex(x => x.WorkspaceId);
        Sessions.EnsureIndex(x => x.ParentId);
        Messages.EnsureIndex(x => x.SessionId);
        Messages.EnsureIndex(x => x.Seq);
        Snapshots.EnsureIndex(x => x.SessionId);

        _seq = Messages.Count() == 0 ? 0 : Messages.Max(x => x.Seq);
    }

    public ILiteCollection<Workspace> Workspaces { get; }
    public ILiteCollection<Session> Sessions { get; }
    public ILiteCollection<Message> Messages { get; }
    public ILiteCollection<ProviderConfig> Providers { get; }
    public ILiteCollection<McpServerConfig> McpServers { get; }
    public ILiteCollection<FileSnapshot> Snapshots { get; }
    public ILiteCollection<Attachment> Attachments { get; }
    public ILiteCollection<AccessToken> Tokens { get; }
    public ILiteCollection<ServerConfig> Config { get; }
    public ILiteCollection<KnowledgeNote> Knowledge { get; }
    public ILiteCollection<CatalogCache> Catalog { get; }
    public ILiteCollection<MemoryEntry> Memory { get; }

    /// <summary>Global monotonic message sequence (order inside a session follows it).</summary>
    public long NextSeq()
    {
        lock (_seqGate) return ++_seq;
    }

    public ServerConfig GetConfig() => Config.FindById("config") ?? new ServerConfig();

    public void SaveConfig(ServerConfig config) => Config.Upsert(config);

    public List<Message> SessionMessages(string sessionId) =>
        Messages.Find(m => m.SessionId == sessionId).OrderBy(m => m.Seq).ToList();

    /// <summary>Removes every `memory` entry of a workspace; call it when the workspace is deleted.</summary>
    public int DeleteWorkspaceMemory(string workspaceId) => Memory.DeleteMany(m => m.WorkspaceId == workspaceId);

    public void DeleteSession(string sessionId)
    {
        foreach (var child in Sessions.Find(s => s.ParentId == sessionId).ToList())
            DeleteSession(child.Id);
        var attachmentIds = Messages.Find(m => m.SessionId == sessionId)
            .SelectMany(m => m.Attachments ?? [])
            .Select(a => a.Id)
            .Distinct()
            .ToList();
        Messages.DeleteMany(m => m.SessionId == sessionId);
        foreach (var id in attachmentIds)
        {
            try
            {
                // Keep the attachment if another session's message still references it. LiteDB only translates
                // Any/All when the lambda body compares the lambda *parameter* itself (not a member), so the
                // projection into Ids has to come from Select: `Attachments.Any(a => a.Id == id)` throws
                // "Any/All requires simple parameter on left side" at query build time (LiteDB 5.0.21, #2810).
                if (Messages.Exists(m => m.Attachments != null && m.Attachments.Select(a => a.Id).Any(x => x == id))) continue;
                var attachment = Attachments.FindById(id);
                if (attachment is null) continue;
                try { if (File.Exists(attachment.StoragePath)) File.Delete(attachment.StoragePath); } catch (IOException) { }
                Attachments.Delete(id);
            }
            catch (Exception ex)
            {
                // A leaked attachment file is harmless; a session left behind with its messages already gone is not.
                _log?.LogWarning(ex, "Could not clean up attachment {AttachmentId}", id);
            }
        }
        Snapshots.DeleteMany(s => s.SessionId == sessionId);
        Knowledge.DeleteMany(k => k.Scope == KnowledgeScope.Chat && k.SessionId == sessionId);
        Sessions.Delete(sessionId);
    }

    /// <summary>
    /// LiteDB reuses freed pages but never shrinks the file. Rebuild closes and reopens the engine, which breaks every
    /// concurrent reader/writer ("engine instance already disposed"), so it is never run online: this only flags the
    /// file and the next startup rebuilds it before anything else touches it.
    /// </summary>
    public void Compact()
    {
        try
        {
            _db.Checkpoint();
            if (_compactMarker is not null) File.WriteAllText(_compactMarker, "");
        }
        catch (Exception) { /* best effort: a failed shrink must not fail the delete */ }
    }

    private static void RebuildIfFlagged(string? marker, ConnectionString connection)
    {
        if (marker is null || !File.Exists(marker)) return;
        try
        {
            using (var db = new LiteDatabase(connection)) db.Rebuild();
        }
        catch (Exception) { /* keep the data as is */ }
        try { File.Delete(marker); } catch (IOException) { }
    }

    public void Dispose() => _db.Dispose();
}
