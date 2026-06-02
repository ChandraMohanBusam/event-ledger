using EventGateway.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EventLedger.IntegrationTests;

/// <summary>
/// Builds in-memory hosts for both services with isolated databases per test
/// (unique shared-cache names), and replaces the Gateway's typed Account Service
/// client with one backed by an HttpClient that targets the Account Service's
/// in-memory test server, so the full Gateway to Account flow runs in-process.
///
/// We replace the IAccountServiceClient registration directly rather than trying
/// to swap the inner handler of the named, resilience-wrapped HttpClient: the
/// real AccountServiceClient is reused, but its HttpClient is the one the test
/// supplies (the Account Service test-server client, or a custom handler client).
/// </summary>
internal static class TestHosts
{
    public static WebApplicationFactory<AccountService.ApiMarker> CreateAccountService()
    {
        var dbName = $"acct-{Guid.NewGuid():N}";
        return new WebApplicationFactory<AccountService.ApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:AccountDb"] =
                            $"Data Source={dbName};Mode=Memory;Cache=Shared"
                    });
                });
            });
    }

    /// <summary>
    /// Creates a Gateway whose Account Service client uses the supplied HttpClient.
    /// Pass the Account Service test-server client for a real end-to-end flow, or
    /// a client over a custom handler to observe outgoing requests.
    /// </summary>
    public static WebApplicationFactory<EventGateway.ApiMarker> CreateGateway(HttpClient accountHttpClient)
    {
        var dbName = $"gw-{Guid.NewGuid():N}";
        return new WebApplicationFactory<EventGateway.ApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:EventLedgerDb"] =
                            $"Data Source={dbName};Mode=Memory;Cache=Shared"
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Replace the typed client registration with one that uses the
                    // test-supplied HttpClient. This bypasses the named-client
                    // resilience pipeline for the in-process test, which is what we
                    // want: the resilience pipeline itself is covered by its own
                    // unit test, and here we are verifying the end-to-end flow.
                    services.RemoveAll<IAccountServiceClient>();
                    services.AddSingleton<IAccountServiceClient>(sp =>
                        new AccountServiceClient(
                            accountHttpClient,
                            sp.GetRequiredService<ILogger<AccountServiceClient>>()));
                });
            });
    }
}
