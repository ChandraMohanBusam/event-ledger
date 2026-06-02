using EventGateway.Clients;
using EventGateway.Data;
using EventGateway.Endpoints;
using EventGateway.Services;
using EventGateway.Telemetry;
using EventLedger.Shared.Errors;
using EventLedger.Shared.Health;
using EventLedger.Shared.Logging;
using EventLedger.Shared.Resilience;
using EventLedger.Shared.Security;
using EventLedger.Shared.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "event-gateway";

builder.AddLedgerLogging(ServiceName);
builder.AddLedgerTelemetry(ServiceName, GatewayMetrics.MeterName);
builder.Services.AddLedgerProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<GatewayMetrics>();

// In-memory cache for idempotency lookups and immutable event-by-id reads.
// The database unique constraint remains the source of truth; the cache only
// saves a round-trip and can never cause incorrectness.
builder.Services.AddMemoryCache();

// Flag-gated auth (default off). API key on the public surface; internal token
// attached to outbound Account Service calls.
builder.Services.AddApiKeyAuth(builder.Configuration);
builder.Services.AddInternalTokenAuth(builder.Configuration);
builder.Services.AddTransient<InternalTokenHandler>();

var connectionString = builder.Configuration.GetConnectionString("EventLedgerDb")
    ?? "Data Source=EventGateway;Mode=Memory;Cache=Shared";

// Keep-alive connection holds the in-memory database open for the process life.
// The DbContext is Scoped and opens its own connection to the same shared cache.
var keepAliveConnection = new SqliteConnection(connectionString);
keepAliveConnection.Open();
builder.Services.AddSingleton(keepAliveConnection);

builder.Services.AddDbContext<EventDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IEventService, EventService>();

// Typed Account Service client via IHttpClientFactory, wrapped in the standard
// resilience pipeline (timeout, retry with backoff and jitter, circuit breaker).
var accountServiceBaseUrl = builder.Configuration["AccountService:BaseUrl"]
    ?? "http://localhost:5001";

builder.Services
    .AddHttpClient<IAccountServiceClient, AccountServiceClient>(client =>
    {
        client.BaseAddress = new Uri(accountServiceBaseUrl);
    })
    .AddHttpMessageHandler<InternalTokenHandler>()
    .AddStandardResilienceHandler(ResiliencePolicy.Configure);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<EventDbContext>("database");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
    // EnsureCreated builds the schema for the in-memory database. Under a shared
    // in-memory cache (and parallel test hosts) the schema may already exist, in
    // which case the create is a benign no-op rather than a failure.
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
    {
        // SQLite error 1 here means an object (table) already exists: the schema
        // was created by another connection to the same shared in-memory cache.
    }
}

app.UseLedgerExceptionHandling();

// Flag-gated API key gate on the public surface (no-op when disabled).
app.UseApiKeyAuth();

// OpenAPI document and Scalar UI exposed only in Development.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapEventEndpoints();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthCheckExtensions.JsonWriter(ServiceName)
});

app.Run();

// Exposed so the integration test project (WebApplicationFactory<Program>) can reference it.
public partial class Program { }
