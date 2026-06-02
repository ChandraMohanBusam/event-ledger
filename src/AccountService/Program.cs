using EventLedger.Shared.Errors;
using EventLedger.Shared.Logging;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "account-service";

// Structured JSON logging with service name, trace id, and span id.
builder.AddLedgerLogging(ServiceName);

// RFC 7807 ProblemDetails for every error response, with trace id attached.
builder.Services.AddLedgerProblemDetails();

// Built-in OpenAPI document generation (.NET 10). Served at /openapi/v1.json.
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseLedgerExceptionHandling();

// OpenAPI document plus a Scalar interactive UI at /scalar/v1.
app.MapOpenApi();
app.MapScalarApiReference();

// Health endpoint. The database connectivity check is added with EF Core in a later commit.
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = ServiceName
}));

app.Run();

// Exposed so the integration test project (WebApplicationFactory<Program>) can reference it.
public partial class Program { }
