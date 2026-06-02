using AccountService.Data;
using AccountService.Endpoints;
using AccountService.Services;
using AccountService.Telemetry;
using EventLedger.Shared.Errors;
using EventLedger.Shared.Health;
using EventLedger.Shared.Logging;
using EventLedger.Shared.Security;
using EventLedger.Shared.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "account-service";

builder.AddLedgerLogging(ServiceName);
builder.AddLedgerTelemetry(ServiceName, AccountMetrics.MeterName);
builder.Services.AddLedgerProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<AccountMetrics>();

// Flag-gated internal-token validation (default off): accept calls only from the Gateway.
builder.Services.AddInternalTokenAuth(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("AccountDb")
    ?? "Data Source=AccountService;Mode=Memory;Cache=Shared";

// Keep-alive connection: an in-memory SQLite database exists only while a
// connection to it is open. This single connection is opened once and held for
// the life of the process so the shared in-memory database is not dropped. The
// DbContext below is registered Scoped and opens its own connection to the same
// shared-cache database; the DbContext is never shared across requests.
var keepAliveConnection = new SqliteConnection(connectionString);
keepAliveConnection.Open();
builder.Services.AddSingleton(keepAliveConnection);

builder.Services.AddDbContext<AccountDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<ITransactionService, TransactionService>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AccountDbContext>("database");

var app = builder.Build();

// Build the schema at startup. EF creates the same shape the SQL scripts document.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
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

// Flag-gated internal-token gate (no-op when disabled).
app.UseInternalTokenAuth();

// OpenAPI document and Scalar UI are exposed only in Development. A public
// service should not advertise its full API surface by default in production.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapAccountEndpoints();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthCheckExtensions.JsonWriter(ServiceName)
});

app.Run();

// Exposed so the integration test project (WebApplicationFactory<Program>) can reference it.
public partial class Program { }
