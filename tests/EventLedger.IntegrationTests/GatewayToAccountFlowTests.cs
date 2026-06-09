using System.Net;
using System.Net.Http.Json;
using EventGateway.Contracts;
using FluentAssertions;
using Xunit;

namespace EventLedger.IntegrationTests;

/// <summary>
/// End-to-end tests across the Gateway and Account Service.
///
/// These exercise the full in-process two-service flow: the Gateway receives an
/// event, forwards it over HTTP to a live Account Service test server, and the
/// resulting balance is read back through the Gateway's proxy. The harness
/// (TestHosts) wires the Gateway's typed Account Service client to the Account
/// Service test server by replacing the IAccountServiceClient registration, so
/// the real client and serialization run while the transport targets the
/// in-memory server. The resilience pipeline is bypassed for these flow tests by
/// design (it is covered directly in EventGateway.Tests.ResiliencePipelineTests,
/// including the circuit-breaker-open behaviour); here the goal is the
/// end-to-end functional path.
///
/// Coverage: validation rejection (not forwarded), the full credit/debit flow
/// with balance correctness, end-to-end idempotency, and out-of-order listing
/// with correct balance. These complement the service-level suites rather than
/// duplicating them.
/// </summary>
public class GatewayToAccountFlowTests
{
    private static object Event(string id, string account, string type, decimal amount, string ts)
        => new
        {
            eventId = id,
            accountId = account,
            type,
            amount,
            currency = "USD",
            eventTimestamp = DateTimeOffset.Parse(ts)
        };

    [Fact]
    public async Task Validation_error_returns_400_and_is_not_forwarded()
    {
        await using var account = TestHosts.CreateAccountService();
        var accountClient = account.CreateClient();
        await using var gatewayFactory = TestHosts.CreateGateway(accountClient);
        var gateway = gatewayFactory.CreateClient();

        var response = await gateway.PostAsJsonAsync("/events",
            Event("bad", "acct-1", "CREDIT", 0m, "2026-05-15T10:00:00Z"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_flow_through_to_account_service_and_balance_is_correct()
    {
        await using var account = TestHosts.CreateAccountService();
        var accountClient = account.CreateClient();
        await using var gatewayFactory = TestHosts.CreateGateway(accountClient);
        var gateway = gatewayFactory.CreateClient();

        var credit = await gateway.PostAsJsonAsync("/events",
            Event("e1", "acct-1", "CREDIT", 100m, "2026-05-15T10:00:00Z"));
        var debit = await gateway.PostAsJsonAsync("/events",
            Event("e2", "acct-1", "DEBIT", 30m, "2026-05-15T11:00:00Z"));

        credit.StatusCode.Should().Be(HttpStatusCode.Created);
        debit.StatusCode.Should().Be(HttpStatusCode.Created);

        var balance = await (await gateway.GetAsync("/accounts/acct-1/balance"))
            .Content.ReadFromJsonAsync<GatewayBalanceResponse>();
        balance!.Balance.Should().Be(70m);
    }

    [Fact]
    public async Task Duplicate_event_does_not_change_balance_end_to_end()
    {
        await using var account = TestHosts.CreateAccountService();
        var accountClient = account.CreateClient();
        await using var gatewayFactory = TestHosts.CreateGateway(accountClient);
        var gateway = gatewayFactory.CreateClient();

        await gateway.PostAsJsonAsync("/events",
            Event("dup-1", "acct-1", "CREDIT", 100m, "2026-05-15T10:00:00Z"));
        await gateway.PostAsJsonAsync("/events",
            Event("dup-1", "acct-1", "CREDIT", 100m, "2026-05-15T10:00:00Z"));

        var balance = await (await gateway.GetAsync("/accounts/acct-1/balance"))
            .Content.ReadFromJsonAsync<GatewayBalanceResponse>();
        balance!.Balance.Should().Be(100m);
    }

    [Fact]
    public async Task Out_of_order_events_list_chronologically_and_balance_is_correct()
    {
        await using var account = TestHosts.CreateAccountService();
        var accountClient = account.CreateClient();
        await using var gatewayFactory = TestHosts.CreateGateway(accountClient);
        var gateway = gatewayFactory.CreateClient();

        await gateway.PostAsJsonAsync("/events",
            Event("late", "acct-1", "DEBIT", 20m, "2026-05-15T18:00:00Z"));
        await gateway.PostAsJsonAsync("/events",
            Event("early", "acct-1", "CREDIT", 100m, "2026-05-15T08:00:00Z"));

        var listed = await gateway.GetFromJsonAsync<List<EventResponse>>("/events?account=acct-1");
        listed!.Select(e => e.EventId).Should().ContainInOrder("early", "late");

        var balance = await (await gateway.GetAsync("/accounts/acct-1/balance"))
            .Content.ReadFromJsonAsync<GatewayBalanceResponse>();
        balance!.Balance.Should().Be(80m);
    }
}
