namespace EventGateway.Domain;

/// <summary>The kind of money movement carried by an event.</summary>
public enum EventType
{
    Credit,
    Debit
}

/// <summary>
/// An immutable record of a submitted event, stored in the Gateway's own
/// ledger. An event exists here only if its transaction was accepted by the
/// Account Service, which keeps the two stores consistent. EventId is the
/// idempotency key.
/// </summary>
public sealed class Event
{
    /// <summary>Primary key and idempotency key.</summary>
    public required string EventId { get; init; }

    public required string AccountId { get; init; }

    public required EventType Type { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Producer time. Listings are ordered by this, not arrival order.</summary>
    public required DateTimeOffset EventTimestamp { get; init; }

    /// <summary>Optional caller-supplied context, stored as raw JSON.</summary>
    public string? Metadata { get; init; }

    /// <summary>Server time the event was received and stored.</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}
