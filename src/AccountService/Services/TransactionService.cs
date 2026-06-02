using AccountService.Contracts;
using AccountService.Data;
using AccountService.Domain;
using AccountService.Telemetry;
using AccountService.Validation;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Services;

/// <summary>
/// Applies transactions and derives balances. Balance is computed on read by
/// aggregating the immutable transaction log, which is correct regardless of
/// arrival order because addition is commutative.
/// </summary>
public sealed class TransactionService(
    AccountDbContext db,
    AccountMetrics metrics,
    ILogger<TransactionService> logger)
    : ITransactionService
{
    public async Task<ApplyResult> ApplyTransactionAsync(
        string accountId,
        ApplyTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var transactionId = request.TransactionId!;

        // Idempotency fast path: if the id already exists, return the original.
        var existing = await db.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Duplicate transaction {TransactionId} for account {AccountId}; returning original.",
                transactionId, accountId);
            return new ApplyResult(ApplyStatus.Duplicate, ToResponse(existing));
        }

        // Single currency per account: reject a currency that differs from what
        // the account already uses. A net balance is a single number, so mixing
        // currencies in one balance would be meaningless.
        var accountCurrency = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .Select(t => t.Currency)
            .FirstOrDefaultAsync(cancellationToken);

        if (accountCurrency is not null &&
            !string.Equals(accountCurrency, request.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return new ApplyResult(
                ApplyStatus.CurrencyConflict,
                Transaction: null,
                Error: $"Account {accountId} uses {accountCurrency}; cannot apply a {request.Currency} transaction.");
        }

        TransactionValidator.TryParseType(request.Type, out var type);

        var entity = new Transaction
        {
            TransactionId = transactionId,
            AccountId = accountId,
            Type = type,
            Amount = request.Amount!.Value,
            Currency = request.Currency!.ToUpperInvariant(),
            EventTimestamp = request.EventTimestamp!.Value,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Transactions.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request inserted the same id between our check and
            // save. The unique constraint is the source of truth, so this is a
            // duplicate, not an error. Return the now-existing original.
            db.ChangeTracker.Clear();
            var raced = await db.Transactions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);

            if (raced is not null)
            {
                return new ApplyResult(ApplyStatus.Duplicate, ToResponse(raced));
            }
            throw;
        }

        logger.LogInformation(
            "Applied {Type} {Amount} {Currency} to account {AccountId} as {TransactionId}.",
            entity.Type, entity.Amount, entity.Currency, accountId, transactionId);

        metrics.TransactionApplied(entity.Type.ToString().ToUpperInvariant());

        return new ApplyResult(ApplyStatus.Created, ToResponse(entity));
    }

    public async Task<BalanceResponse?> GetBalanceAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var transactions = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
        {
            return null;
        }

        var (balance, currency) = ComputeBalance(transactions);
        return new BalanceResponse(accountId, balance, currency);
    }

    public async Task<AccountDetailsResponse?> GetAccountAsync(
        string accountId,
        int recentCount = 20,
        CancellationToken cancellationToken = default)
    {
        var transactions = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
        {
            return null;
        }

        var (balance, currency) = ComputeBalance(transactions);

        var recent = transactions
            .OrderByDescending(t => t.EventTimestamp)
            .Take(recentCount)
            .Select(ToResponse)
            .ToList();

        return new AccountDetailsResponse(accountId, balance, currency, recent);
    }

    /// <summary>
    /// Balance = sum of CREDIT amounts minus sum of DEBIT amounts. Computed in
    /// memory with decimal precision rather than in SQL, since SQLite has no
    /// native decimal type.
    /// </summary>
    private static (decimal Balance, string Currency) ComputeBalance(
        IReadOnlyCollection<Transaction> transactions)
    {
        decimal credits = transactions
            .Where(t => t.Type == TransactionType.Credit)
            .Sum(t => t.Amount);

        decimal debits = transactions
            .Where(t => t.Type == TransactionType.Debit)
            .Sum(t => t.Amount);

        // All transactions on an account share one currency (enforced on write).
        var currency = transactions.First().Currency;

        return (credits - debits, currency);
    }

    private static TransactionResponse ToResponse(Transaction t) => new(
        t.TransactionId,
        t.AccountId,
        t.Type.ToString().ToUpperInvariant(),
        t.Amount,
        t.Currency,
        t.EventTimestamp,
        t.CreatedAt);
}
