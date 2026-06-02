using AccountService.Contracts;
using AccountService.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AccountService.Tests;

public class TransactionServiceTests
{
    private static ApplyTransactionRequest Credit(string id, decimal amount, string ts, string currency = "USD")
        => new(id, "CREDIT", amount, currency, DateTimeOffset.Parse(ts));

    private static ApplyTransactionRequest Debit(string id, decimal amount, string ts, string currency = "USD")
        => new(id, "DEBIT", amount, currency, DateTimeOffset.Parse(ts));

    private static TransactionService NewService(TestDatabase tdb)
        => new(tdb.Context, NullLogger<TransactionService>.Instance);

    [Fact]
    public async Task Applying_a_new_transaction_returns_Created()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        var result = await service.ApplyTransactionAsync(
            "acct-1", Credit("evt-1", 100m, "2026-05-15T10:00:00Z"));

        result.Status.Should().Be(ApplyStatus.Created);
        result.Transaction!.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task Duplicate_transactionId_does_not_create_a_second_record_or_change_balance()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);
        var request = Credit("evt-dup", 100m, "2026-05-15T10:00:00Z");

        var first = await service.ApplyTransactionAsync("acct-1", request);
        var second = await service.ApplyTransactionAsync("acct-1", request);

        first.Status.Should().Be(ApplyStatus.Created);
        second.Status.Should().Be(ApplyStatus.Duplicate);

        var verify = tdb.NewContext();
        verify.Transactions.Count().Should().Be(1);

        var balance = await NewService(tdb).GetBalanceAsync("acct-1");
        balance!.Balance.Should().Be(100m);
    }

    [Fact]
    public async Task Balance_is_credits_minus_debits()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        await service.ApplyTransactionAsync("acct-1", Credit("c1", 200m, "2026-05-15T10:00:00Z"));
        await service.ApplyTransactionAsync("acct-1", Debit("d1", 50m, "2026-05-15T11:00:00Z"));
        await service.ApplyTransactionAsync("acct-1", Credit("c2", 25m, "2026-05-15T12:00:00Z"));

        var balance = await service.GetBalanceAsync("acct-1");

        balance!.Balance.Should().Be(175m);
    }

    [Fact]
    public async Task Balance_is_correct_regardless_of_arrival_order()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        // Arrive newest first, then an earlier one: arrival order is reversed.
        await service.ApplyTransactionAsync("acct-1", Debit("d-late", 30m, "2026-05-15T18:00:00Z"));
        await service.ApplyTransactionAsync("acct-1", Credit("c-early", 100m, "2026-05-15T08:00:00Z"));

        var balance = await service.GetBalanceAsync("acct-1");

        balance!.Balance.Should().Be(70m);
    }

    [Fact]
    public async Task Recent_transactions_are_ordered_newest_first_by_event_time()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        await service.ApplyTransactionAsync("acct-1", Credit("c-mid", 10m, "2026-05-15T12:00:00Z"));
        await service.ApplyTransactionAsync("acct-1", Credit("c-old", 10m, "2026-05-15T08:00:00Z"));
        await service.ApplyTransactionAsync("acct-1", Credit("c-new", 10m, "2026-05-15T20:00:00Z"));

        var details = await service.GetAccountAsync("acct-1");

        details!.RecentTransactions.Select(t => t.TransactionId)
            .Should().ContainInOrder("c-new", "c-mid", "c-old");
    }

    [Fact]
    public async Task Mismatched_currency_on_same_account_is_rejected()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        await service.ApplyTransactionAsync("acct-1", Credit("c-usd", 100m, "2026-05-15T10:00:00Z", "USD"));
        var conflict = await service.ApplyTransactionAsync("acct-1", Credit("c-eur", 100m, "2026-05-15T11:00:00Z", "EUR"));

        conflict.Status.Should().Be(ApplyStatus.CurrencyConflict);
        conflict.Error.Should().Contain("USD");
    }

    [Fact]
    public async Task Unknown_account_balance_is_null()
    {
        using var tdb = new TestDatabase();
        var service = NewService(tdb);

        var balance = await service.GetBalanceAsync("no-such-account");

        balance.Should().BeNull();
    }
}
