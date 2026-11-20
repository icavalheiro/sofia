using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LiteDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Sofia.Server.Data;

/// <summary>
/// EF Core (SQLite) model of the harness store. Scalar fields are real columns so queries push down to SQL; the
/// nested DTO graphs (attachments, tool calls, usage, settings sub-objects) stay JSON columns, which keeps the entity
/// shape and therefore the wire contract identical to the LiteDB era.
/// </summary>
public sealed class SofiaDbContext(DbContextOptions<SofiaDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ProviderConfig> Providers => Set<ProviderConfig>();
    public DbSet<McpServerConfig> McpServers => Set<McpServerConfig>();
    public DbSet<FileSnapshot> Snapshots => Set<FileSnapshot>();
    public DbSet<FileReadRecord> FileReads => Set<FileReadRecord>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AccessToken> Tokens => Set<AccessToken>();
    public DbSet<ServerConfig> Config => Set<ServerConfig>();
    public DbSet<KnowledgeNote> Knowledge => Set<KnowledgeNote>();
    public DbSet<CatalogCache> Catalog => Set<CatalogCache>();
    public DbSet<MemoryEntry> Memory => Set<MemoryEntry>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type: EF stores DateTime as TEXT and reads it back with Kind=Unspecified. The harness
        // always writes UTC (LiteDB's UtcDate did the same), so the Kind is restored on read; otherwise timestamps
        // would drift by the local offset in the UI (`ToLocalTime`).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.IsFolder);
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Usage).HasJsonColumn();
            e.Property(x => x.Context).HasJsonColumn();
            e.Property(x => x.ActiveSkills).HasJsonColumn();
            e.Property(x => x.PromptFacts).HasJsonColumn();
            e.Property(x => x.SubContextFiles).HasJsonColumn();
            e.Property(x => x.AllowedTools).HasJsonColumn();
            // Computed/transient state: recomputed on load, never stored.
            e.Ignore(x => x.SandboxEffective);
            e.Ignore(x => x.Queued);
            e.Ignore(x => x.QueuedMessages);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.UpdatedAt);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Attachments).HasJsonColumn();
            e.Property(x => x.ToolCalls).HasJsonColumn();
            e.Property(x => x.Permission).HasJsonColumn();
            e.Property(x => x.Question).HasJsonColumn();
            e.Property(x => x.QuestionSet).HasJsonColumn();
            e.Property(x => x.Knowledge).HasJsonColumn();
            e.Property(x => x.Compaction).HasJsonColumn();
            e.Property(x => x.Retry).HasJsonColumn();
            e.Property(x => x.Usage).HasJsonColumn();
            e.Property(x => x.ContextNotes).HasJsonColumn();
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.Seq);
        });

        b.Entity<ProviderConfig>(e =>
        {
            e.ToTable("providers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Models).HasJsonColumn();
            e.Property(x => x.KnownContext).HasJsonColumn();
        });

        b.Entity<McpServerConfig>(e =>
        {
            e.ToTable("mcp_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Env).HasJsonColumn();
        });

        b.Entity<ServerConfig>(e =>
        {
            e.ToTable("server_config");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisabledModels).HasJsonColumn();
            e.Property(x => x.SingleClientModels).HasJsonColumn();
            e.Property(x => x.MultiClientModels).HasJsonColumn();
            e.Property(x => x.Lsp).HasJsonColumn();
        });

        b.Entity<CatalogCache>(e =>
        {
            e.ToTable("catalog");
            e.HasKey(x => x.Id);
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(x => x.Id);
        });

        b.Entity<FileSnapshot>(e =>
        {
            e.ToTable("snapshots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<FileReadRecord>(e =>
        {
            e.ToTable("file_reads");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<AccessToken>(e =>
        {
            e.ToTable("tokens");
            e.HasKey(x => x.Id);
        });

        b.Entity<KnowledgeNote>(e =>
        {
            e.ToTable("knowledge");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.Agent);
        });

        b.Entity<MemoryEntry>(e =>
        {
            e.ToTable("memory");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId);
        });
    }
}

/// <summary>
/// Serializer settings for JSON columns: exactly what LiteDB persisted. Properties hidden from the wire
/// (<see cref="JsonIgnoreAttribute"/>, e.g. <c>Attachment.StoragePath</c> or <c>ToolCall.Result</c>) still have to be
/// stored, while computed/transient fields (<see cref="BsonIgnoreAttribute"/>) are skipped.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IncludePersistedProperties } },
        };
        return options;
    }

    private static void IncludePersistedProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        // Properties marked [JsonIgnore] stay in the contract with their accessors dropped; wiring the accessors
        // back makes them serialize without touching the wire contract (which uses its own options).
        foreach (var json in info.Properties)
        {
            if (json.AttributeProvider is not PropertyInfo property) continue;
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.IsDefined(typeof(BsonIgnoreAttribute), true)) continue;
            if (!property.IsDefined(typeof(JsonIgnoreAttribute), true)) continue;
            json.Get = target => property.GetValue(target);
            json.Set = (target, value) => property.SetValue(target, value);
            // [JsonIgnore] is implemented as a "never serialize" predicate; clearing it is what re-enables the write.
            json.ShouldSerialize = null;
        }
    }
}

public static class JsonColumnExtensions
{
    /// <summary>Stores a nested DTO graph as a JSON text column.</summary>
    public static PropertyBuilder<T> HasJsonColumn<T>(this PropertyBuilder<T> property) where T : class?
    {
        property.HasConversion(
            new ValueConverter<T, string>(
                value => JsonSerializer.Serialize(value, JsonStore.Options),
                text => JsonSerializer.Deserialize<T>(text, JsonStore.Options)!),
            new ValueComparer<T>(
                (a, b) => ReferenceEquals(a, b) || (a != null && b != null && JsonSerializer.Serialize(a, JsonStore.Options) == JsonSerializer.Serialize(b, JsonStore.Options)),
                value => value == null ? 0 : JsonSerializer.Serialize(value, JsonStore.Options).GetHashCode(),
                value => value));
        return property;
    }
}

/// <summary>Stores the UTC instant and restores <see cref="DateTimeKind.Utc"/> when the row is read back.</summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    value => value.HasValue ? (value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime()) : value,
    value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
