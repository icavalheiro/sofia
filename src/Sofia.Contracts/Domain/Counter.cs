namespace Sofia.Contracts.Domain;

/// <summary>
/// A named monotonic counter stored in the database (<c>counters</c> table). It replaces the per-process field the
/// store used to keep, so two hosts writing the same store cannot hand out the same message sequence twice.
/// </summary>
public sealed class Counter
{
    [BsonId] public string Name { get; set; } = "";
    public long Value { get; set; }
}
