using AccountService.Data;
using AccountService.Endpoints;
using AccountService.Services;
using EventLedger.Shared.Errors;
using EventLedger.Shared.Health;
using EventLedger.Shared.Logging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "account-service";

builder.AddLedgerLogging(ServiceName);
builder.Services.AddLedgerProblemDetails();
builder.Services.AddOpenApi();

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
    db.Database.EnsureCreated();
}

app.UseLedgerExceptionHandling();

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
