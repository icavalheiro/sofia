using LiteDB;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Data;

/// <summary>LiteDB store. One shared instance; LiteDB serializes access internally.</summary>
public sealed class SofiaDb : IDisposable
{
    private readonly LiteDatabase _db;
    private long _seq;
    private readonly object _seqGate = new();

    public SofiaDb(SofiaOptions options) : this(new ConnectionString { Filename = options.DbPath, Connection = ConnectionType.Direct })
    {
    }

    public SofiaDb(ConnectionString connection)
    {
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
        Messages.DeleteMany(m => m.SessionId == sessionId);
        Snapshots.DeleteMany(s => s.SessionId == sessionId);
        Knowledge.DeleteMany(k => k.Scope == KnowledgeScope.Chat && k.SessionId == sessionId);
        Sessions.Delete(sessionId);
    }

    public void Dispose() => _db.Dispose();
}
