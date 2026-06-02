namespace AccountService.Domain;

/// <summary>
/// The kind of money movement. CREDIT increases a balance, DEBIT decreases it.
/// </summary>
public enum TransactionType
{
    Credit,
    Debit
}

/// <summary>
/// An immutable record of a single money movement applied to an account.
/// The transaction log is the source of truth; balances are derived from it.
/// TransactionId equals the originating event id, which makes the write path
/// idempotent end to end: the same event can never apply twice.
/// </summary>
public sealed class Transaction
{
    /// <summary>Primary key and idempotency key. Equals the originating event id.</summary>
    public required string TransactionId { get; init; }

    public required string AccountId { get; init; }

    public required TransactionType Type { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Producer time. Used for ordering, may differ from arrival order.</summary>
    public required DateTimeOffset EventTimestamp { get; init; }

    /// <summary>Server time the transaction was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
