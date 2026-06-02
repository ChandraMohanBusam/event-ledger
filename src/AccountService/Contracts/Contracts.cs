namespace AccountService.Contracts;

/// <summary>
/// Request to apply a transaction. Fields are nullable so that "missing" is
/// distinguishable from "present but invalid" during validation.
/// TransactionId equals the originating event id (idempotency key).
/// </summary>
public sealed record ApplyTransactionRequest(
    string? TransactionId,
    string? Type,
    decimal? Amount,
    string? Currency,
    DateTimeOffset? EventTimestamp);

/// <summary>A persisted transaction returned to callers.</summary>
public sealed record TransactionResponse(
    string TransactionId,
    string AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp,
    DateTimeOffset CreatedAt);

/// <summary>Current balance for an account.</summary>
public sealed record BalanceResponse(
    string AccountId,
    decimal Balance,
    string Currency);

/// <summary>Account details with recent transactions, newest first by event time.</summary>
public sealed record AccountDetailsResponse(
    string AccountId,
    decimal Balance,
    string Currency,
    IReadOnlyList<TransactionResponse> RecentTransactions);
