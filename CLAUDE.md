# Event Ledger: Project Context

This file documents the scope, architecture, and key engineering decisions for
the Event Ledger system. It is committed to the repository root as a record of
the design and the reasoning behind it.

## Project

Event Ledger is a two-service system that ingests financial transaction events
and maintains account balances. It is built to demonstrate correctness under
duplicate and out-of-order delivery, distributed tracing, structured
observability, resiliency, and graceful degradation.

## Tech stack

- Language: C# 14
- Framework: ASP.NET Core on .NET 10 (LTS)
- Database: SQLite in-memory, one independent database per service, accessed
  through EF Core 10. A single SqliteConnection is opened once and held open for
  the life of the process, because an in-memory SQLite database exists only
  while a connection to it is open. The DbContext is registered Scoped per
  request and uses that shared open connection.
- Persistence config: connection strings come from configuration
  (appsettings plus environment variable override), never hardcoded. Moving to
  a file-based or server SQL database is a connection-string change with no code
  change.
- API documentation: built-in Microsoft.AspNetCore.OpenApi document generation
  with a Scalar UI on both services.
- HTTP communication: synchronous REST. The Gateway calls the Account Service
  through a typed client registered with IHttpClientFactory.
- Resiliency: Polly via Microsoft.Extensions.Http.Resilience standard handler on
  the Gateway to Account Service call: total timeout, retry with exponential
  backoff and jitter, per-attempt timeout, and circuit breaker.
- Tracing: OpenTelemetry using W3C Trace Context (traceparent header). Trace id
  created at the Gateway, propagated to the Account Service, logged by both.
- Metrics: OpenTelemetry metrics with custom counters, console exporter.
- Logging: Serilog structured JSON with trace id, span id, timestamp, log level,
  and service name.
- Caching: IMemoryCache for idempotency lookups on the write path and for the
  immutable GET /events/{id} read. The database remains the source of truth.
- Authentication: flag-gated and default off for the demo. X-Api-Key on the
  Gateway public surface and X-Internal-Token shared secret on the internal
  Gateway to Account Service call. Keys come from configuration.
- Testing: xUnit with NSubstitute and FluentAssertions, plus
  Microsoft.AspNetCore.Mvc.Testing for integration tests.
- Containers: Docker Compose runs both services.

## Architecture

Two independently runnable microservices with no shared database and no shared
in-process state. A small shared library (EventLedger.Shared) holds only
cross-cutting infrastructure setup (logging, tracing, resilience config, error
contract, trace header constants). It contains no domain types and no runtime
state.

### Event Gateway API (public-facing, port 5000)
- POST /events
- GET /events/{id}
- GET /events?account={accountId}
- GET /accounts/{accountId}/balance (intentional extension: resilient proxy to the Account Service)
- GET /health

### Account Service (internal, port 5001)
- POST /accounts/{accountId}/transactions
- GET /accounts/{accountId}/balance
- GET /accounts/{accountId}
- GET /health

## Key requirements

- Idempotency: a duplicate eventId returns the original event and never creates
  a duplicate record or alters the balance. Enforced by a SQLite unique key on
  the id in both services; the database constraint is the source of truth.
- Out-of-order tolerance: event listings are ordered by eventTimestamp, and
  balances are correct regardless of arrival order because a balance is a
  commutative sum.
- Balance: sum of CREDIT amounts minus sum of DEBIT amounts, computed on read
  from the immutable transaction log.
- Validation: missing required fields, non-positive amounts, and unknown types
  return meaningful errors using the ProblemDetails contract with the trace id.
- Service separation: each service runs as its own process with its own
  database.
- Distributed tracing: trace id generated at the Gateway, propagated over HTTP,
  logged in both services.
- Resiliency: standard resilience pipeline on the Gateway to Account Service
  call.
- Graceful degradation: GET /events endpoints keep working when the Account
  Service is down; the write path and balance proxy return a clear 503.
- Observability: structured JSON logging, health checks with database
  connectivity, and custom metrics.
- Docker Compose to run both services.

## Project structure

```
event-ledger/
  src/
    EventLedger.Shared/   cross-cutting infra only (logging, tracing, resilience, error contract)
    EventGateway/         public API, port 5000
    AccountService/       internal API, port 5001
  tests/
    EventGateway.Tests/
    AccountService.Tests/
  docs/
    ARCHITECTURE.md
  docker-compose.yml
  README.md
  CLAUDE.md
```

Each service also ships Data/Scripts/schema.sql and seed.sql as the
schema-of-record for a future move to a real database. EF Core builds the same
schema at startup for the in-memory demo, so the scripts and the EF model are
kept in sync.

## Development approach

Incremental, feature by feature. Each commit leaves the repository building and
runnable, so the solution is submittable at every step. Required functionality
is built first; deliberate extras (auth stubs, caching, rate limiting, Jaeger
trace visualization) are layered on afterward, each in its own commit. The
commit history is the record of the working process and is intentionally not
squashed.

## Build and run

- Build: `dotnet build`
- Run Gateway: `dotnet run --project src/EventGateway`
- Run Account Service: `dotnet run --project src/AccountService`
- Run tests: `dotnet test`
- Run everything in containers: `docker compose up --build`
- Gateway interactive docs (Scalar): `http://localhost:5000/scalar/v1`
- Account Service interactive docs (Scalar): `http://localhost:5001/scalar/v1`

## Author

Chandra Mohan Busam
