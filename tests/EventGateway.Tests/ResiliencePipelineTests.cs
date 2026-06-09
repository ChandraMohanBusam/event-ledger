using System.Net;
using EventGateway.Clients;
using EventGateway.Contracts;
using EventLedger.Shared.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventGateway.Tests;

/// <summary>
/// Exercises the real standard resilience pipeline wired onto the typed client.
/// Verifies that a persistently failing Account Service is retried (not called
/// just once) and that the exhausted call surfaces as "unavailable" so the
/// Gateway can degrade gracefully rather than throwing.
/// </summary>
public class ResiliencePipelineTests
{
    /// <summary>Counts attempts and always returns 503, to trigger retries.</summary>
    private sealed class CountingFailingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Failing_account_service_is_retried_and_reported_unavailable()
    {
        var handler = new CountingFailingHandler();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddHttpClient<IAccountServiceClient, AccountServiceClient>(client =>
            {
                client.BaseAddress = new Uri("http://account-service.test");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(ResiliencePolicy.Configure);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAccountServiceClient>();

        var result = await client.ApplyTransactionAsync(
            "acct-1",
            new ApplyTransactionDto("evt-1", "CREDIT", 100m, "USD", DateTimeOffset.UtcNow));

        // The pipeline retried: more than the single initial attempt was made.
        handler.Calls.Should().BeGreaterThan(1);

        // A persistent 5xx is treated as the service being unavailable.
        result.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Circuit_breaker_opens_after_repeated_failures_and_short_circuits()
    {
        var handler = new CountingFailingHandler();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddHttpClient<IAccountServiceClient, AccountServiceClient>(client =>
            {
                client.BaseAddress = new Uri("http://account-service.test");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(ResiliencePolicy.Configure);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAccountServiceClient>();

        var transaction = new ApplyTransactionDto("evt-cb", "CREDIT", 100m, "USD", DateTimeOffset.UtcNow);

        // Drive enough failing calls (each one also retried) to satisfy the
        // breaker's minimum throughput within its sampling window several times
        // over. With a 100% failure ratio well above the configured threshold,
        // the circuit opens.
        for (var i = 0; i < (ResiliencePolicy.CircuitMinimumThroughput * 3); i++)
        {
            var result = await client.ApplyTransactionAsync("acct-1", transaction);
            result.IsUnavailable.Should().BeTrue();
        }

        // Record how many times the underlying handler has been hit so far.
        var callsBeforeOpenWindow = handler.Calls;

        // With the circuit open, the next call must short-circuit: it fails fast
        // as unavailable WITHOUT invoking the underlying handler again. This is
        // the distinguishing behaviour of an open breaker versus plain retry.
        var shortCircuited = await client.ApplyTransactionAsync("acct-1", transaction);

        shortCircuited.IsUnavailable.Should().BeTrue();
        handler.Calls.Should().Be(callsBeforeOpenWindow,
            "an open circuit should fail fast without calling the Account Service again");
    }
}
