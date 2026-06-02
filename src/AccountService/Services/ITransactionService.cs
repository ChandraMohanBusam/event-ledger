using AccountService.Contracts;

namespace AccountService.Services;

/// <summary>Outcome of applying a transaction.</summary>
public enum ApplyStatus
{
    /// <summary>A new transaction was created.</summary>
    Created,

    /// <summary>The transaction id already existed; the original is returned unchanged.</summary>
    Duplicate,

    /// <summary>The currency differs from the account's established currency.</summary>
    CurrencyConflict
}

/// <summary>Result of an apply attempt. Error is set only for CurrencyConflict.</summary>
public sealed record ApplyResult(
    ApplyStatus Status,
    TransactionResponse? Transaction,
    string? Error = null);

/// <summary>
/// Account operations. Behind an interface so endpoints depend on the contract
/// and tests can substitute it.
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Applies a transaction idempotently. A repeat of an existing
    /// transaction id is a no-op that returns the original.
    /// </summary>
    Task<ApplyResult> ApplyTransactionAsync(
        string accountId,
        ApplyTransactionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Current balance, or null if the account has no transactions.</summary>
    Task<BalanceResponse?> GetBalanceAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    /// <summary>Account details with recent transactions, or null if unknown.</summary>
    Task<AccountDetailsResponse?> GetAccountAsync(
        string accountId,
        int recentCount = 20,
        CancellationToken cancellationToken = default);
}
