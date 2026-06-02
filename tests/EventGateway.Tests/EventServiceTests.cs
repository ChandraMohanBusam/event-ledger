using System.Net;
using EventGateway.Clients;
using EventGateway.Contracts;
using EventGateway.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EventGateway.Tests;

public class EventServiceTests
{
    private static SubmitEventRequest Credit(string id, string account, decimal amount, string ts)
        => new(id, account, "CREDIT", amount, "USD", DateTimeOffset.Parse(ts), null);

    private static AccountServiceResult<TransactionDto> AppliedOk(string id) =>
        new(true, HttpStatusCode.Created,
            new TransactionDto(id, "acct-1", "CREDIT", 100m, "USD",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    private static EventService NewService(TestDatabase tdb, IAccountServiceClient client)
        => new(tdb.Context, client, NullLogger<EventService>.Instance);

    [Fact]
    public async Task Successful_submission_forwards_then_stores_and_returns_Created()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync("acct-1", Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(AppliedOk("evt-1"));

        var service = NewService(tdb, client);

        var result = await service.SubmitEventAsync(Credit("evt-1", "acct-1", 100m, "2026-05-15T10:00:00Z"));

        result.Status.Should().Be(SubmitStatus.Created);
        await client.Received(1).ApplyTransactionAsync(
            "acct-1", Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>());
        tdb.NewContext().Events.Count().Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_eventId_returns_original_without_forwarding_again()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(AppliedOk("evt-dup"));
        var service = NewService(tdb, client);
        var request = Credit("evt-dup", "acct-1", 100m, "2026-05-15T10:00:00Z");

        var first = await service.SubmitEventAsync(request);
        var second = await service.SubmitEventAsync(request);

        first.Status.Should().Be(SubmitStatus.Created);
        second.Status.Should().Be(SubmitStatus.Duplicate);

        // Forwarded only once: the duplicate short-circuits before the call.
        await client.Received(1).ApplyTransactionAsync(
            Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>());
        tdb.NewContext().Events.Count().Should().Be(1);
    }

    [Fact]
    public async Task When_account_service_unavailable_nothing_is_stored_and_status_is_unavailable()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(AccountServiceResult<TransactionDto>.Unreachable());
        var service = NewService(tdb, client);

        var result = await service.SubmitEventAsync(Credit("evt-x", "acct-1", 100m, "2026-05-15T10:00:00Z"));

        result.Status.Should().Be(SubmitStatus.AccountServiceUnavailable);
        tdb.NewContext().Events.Count().Should().Be(0);
    }

    [Fact]
    public async Task Reads_work_without_calling_the_account_service()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(AppliedOk("evt-1"));
        var service = NewService(tdb, client);

        await service.SubmitEventAsync(Credit("evt-1", "acct-1", 100m, "2026-05-15T10:00:00Z"));
        client.ClearReceivedCalls();

        var byId = await service.GetEventAsync("evt-1");
        var byAccount = await service.GetEventsByAccountAsync("acct-1");

        byId.Should().NotBeNull();
        byAccount.Should().HaveCount(1);
        // The read path never touches the Account Service: graceful degradation.
        await client.DidNotReceive().ApplyTransactionAsync(
            Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Listing_is_ordered_by_event_timestamp_regardless_of_arrival_order()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AccountServiceResult<TransactionDto>(true, HttpStatusCode.Created, null));
        var service = NewService(tdb, client);

        // Submit newest first, oldest last: arrival order is reversed.
        await service.SubmitEventAsync(Credit("e-late", "acct-1", 10m, "2026-05-15T18:00:00Z"));
        await service.SubmitEventAsync(Credit("e-early", "acct-1", 10m, "2026-05-15T08:00:00Z"));
        await service.SubmitEventAsync(Credit("e-mid", "acct-1", 10m, "2026-05-15T13:00:00Z"));

        var listed = await service.GetEventsByAccountAsync("acct-1");

        listed.Select(e => e.EventId).Should().ContainInOrder("e-early", "e-mid", "e-late");
    }

    [Fact]
    public async Task Unknown_event_returns_null()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        var service = NewService(tdb, client);

        (await service.GetEventAsync("nope")).Should().BeNull();
    }
}
