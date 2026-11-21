namespace Sofia.Contracts.Domain;

// Persistence markers, deliberately local so the contract assembly stays dependency-free. They are not LiteDB's own
// attributes: the one-shot legacy import configures the LiteDB mapper explicitly (Data/LiteDbMigration.cs) and the
// JSON column store reads [BsonIgnore] to decide what never needs to be stored (Data/SofiaDbContext.cs).

/// <summary>Marks the primary key of an entity.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BsonIdAttribute : Attribute;

/// <summary>Marks a computed or transient property that must never be persisted.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BsonIgnoreAttribute : Attribute;
