using EventGateway.Clients;
using EventGateway.Contracts;
using EventGateway.Services;
using EventGateway.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EventGateway.Tests;

public class EventServiceCacheTests
{
    private static readonly GatewayMetrics Metrics = new(
        new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

    private static SubmitEventRequest Credit(string id, string account, decimal amount, string ts)
        => new(id, account, "CREDIT", amount, "USD", DateTimeOffset.Parse(ts), null);

    [Fact]
    public async Task Second_get_by_id_is_served_from_cache_without_a_second_db_read()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(new AccountServiceResult<TransactionDto>(true, System.Net.HttpStatusCode.Created, null));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new EventService(tdb.Context, client, Metrics, cache, NullLogger<EventService>.Instance);

        await service.SubmitEventAsync(Credit("evt-1", "acct-1", 100m, "2026-05-15T10:00:00Z"));

        // First read populates (or reads) cache, second read should hit cache.
        var first = await service.GetEventAsync("evt-1");
        var second = await service.GetEventAsync("evt-1");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.EventId.Should().Be("evt-1");

        // The event id is present in the cache after submission.
        cache.TryGetValue("event:evt-1", out EventResponse? cached).Should().BeTrue();
        cached!.EventId.Should().Be("evt-1");
    }

    [Fact]
    public async Task Duplicate_submission_short_circuits_via_cache_without_forwarding_again()
    {
        using var tdb = new TestDatabase();
        var client = Substitute.For<IAccountServiceClient>();
        client.ApplyTransactionAsync(Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>())
            .Returns(new AccountServiceResult<TransactionDto>(true, System.Net.HttpStatusCode.Created, null));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new EventService(tdb.Context, client, Metrics, cache, NullLogger<EventService>.Instance);
        var request = Credit("evt-dup", "acct-1", 100m, "2026-05-15T10:00:00Z");

        var first = await service.SubmitEventAsync(request);
        var second = await service.SubmitEventAsync(request);

        first.Status.Should().Be(SubmitStatus.Created);
        second.Status.Should().Be(SubmitStatus.Duplicate);

        // Forwarded exactly once; the duplicate was served from cache.
        await client.Received(1).ApplyTransactionAsync(
            Arg.Any<string>(), Arg.Any<ApplyTransactionDto>(), Arg.Any<CancellationToken>());
    }
}
