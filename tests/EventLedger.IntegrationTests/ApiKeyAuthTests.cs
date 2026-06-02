using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using EventGateway.Clients;
using Xunit;

namespace EventLedger.IntegrationTests;

/// <summary>
/// Verifies the flag-gated API key on the Gateway public surface: when enabled,
/// requests without the key are rejected and health remains open; with the key,
/// requests are accepted.
/// </summary>
public class ApiKeyAuthTests
{
    private const string TestKey = "test-key-123";

    private static WebApplicationFactory<EventGateway.ApiMarker> CreateGatewayWithApiKey(
        HttpClient accountHttpClient)
    {
        var dbName = $"gw-auth-{Guid.NewGuid():N}";
        return new WebApplicationFactory<EventGateway.ApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:EventLedgerDb"] =
                            $"Data Source={dbName};Mode=Memory;Cache=Shared",
                        ["ApiKeyAuth:Enabled"] = "true",
                        ["ApiKeyAuth:HeaderName"] = "X-Api-Key",
                        ["ApiKeyAuth:ApiKey"] = TestKey
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAccountServiceClient>();
                    services.AddSingleton<IAccountServiceClient>(sp =>
                        new AccountServiceClient(
                            accountHttpClient,
                            sp.GetRequiredService<ILogger<AccountServiceClient>>()));
                });
            });
    }

    [Fact]
    public async Task Health_is_open_even_when_api_key_is_enabled()
    {
        await using var account = TestHosts.CreateAccountService();
        await using var gatewayFactory = CreateGatewayWithApiKey(account.CreateClient());
        var gateway = gatewayFactory.CreateClient();

        var response = await gateway.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Request_without_api_key_is_rejected_with_401()
    {
        await using var account = TestHosts.CreateAccountService();
        await using var gatewayFactory = CreateGatewayWithApiKey(account.CreateClient());
        var gateway = gatewayFactory.CreateClient();

        var response = await gateway.GetAsync("/events?account=acct-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_with_valid_api_key_is_accepted()
    {
        await using var account = TestHosts.CreateAccountService();
        await using var gatewayFactory = CreateGatewayWithApiKey(account.CreateClient());
        var gateway = gatewayFactory.CreateClient();
        gateway.DefaultRequestHeaders.Add("X-Api-Key", TestKey);

        var response = await gateway.GetAsync("/events?account=acct-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
