# Event Ledger

A two-service event ledger built with ASP.NET Core on .NET 10. Clients submit
financial transaction events to a public Gateway, which forwards each one to an
internal Account Service that maintains transactions and balances. The system is
idempotent, tolerant of out-of-order events, traced end to end, resilient to
Account Service outages, and gracefully degraded so reads keep working when the
Account Service is down.

## Architecture overview

```
                  +-------------------------+
   client  --->   |   Event Gateway (5000)  |   public-facing
                  |   own SQLite database   |
                  +-----------+-------------+
                              |  HTTP + W3C trace context
                              |  Polly resilience pipeline
                              v
                  +-------------------------+
                  |  Account Service (5001) |   internal only
                  |   own SQLite database   |
                  +-------------------------+
```

The two services are independently runnable processes. Each owns its own SQLite
in-memory database. There is no shared database and no shared in-process state.
A small shared library holds only cross-cutting infrastructure (logging, the
error contract, tracing, the resilience pipeline configuration); it contains no
domain types and no runtime state.

- **Event Gateway** accepts events, validates them, enforces idempotency, owns
  the event ledger, and calls the Account Service to apply transactions.
- **Account Service** owns transactions and computes balances. It is only called
  by the Gateway.

For the full set of design decisions and the reasoning behind each, see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## API

### Event Gateway (port 5000)

| Method | Endpoint | Description |
|---|---|---|
| POST | `/events` | Submit an event |
| GET | `/events/{id}` | Get a single event |
| GET | `/events?account={accountId}` | List events for an account, ordered by event timestamp |
| GET | `/accounts/{accountId}/balance` | Balance via a resilient proxy to the Account Service |
| GET | `/health` | Liveness and database check |

### Account Service (port 5001)

| Method | Endpoint | Description |
|---|---|---|
| POST | `/accounts/{accountId}/transactions` | Apply a transaction (idempotent by id) |
| GET | `/accounts/{accountId}/balance` | Current balance |
| GET | `/accounts/{accountId}` | Account details and recent transactions |
| GET | `/health` | Liveness and database check |

### Event payload

```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": { "source": "mainframe-batch", "batchId": "B-9042" }
}
```

`type` must be `CREDIT` or `DEBIT`, `amount` must be greater than zero, and all
fields except `metadata` are required. `eventId` is any unique string and is the
idempotency key; the examples in [docs/RUNBOOK.md](docs/RUNBOOK.md) use GUIDs,
but a value like `evt-001` is equally valid.

## Prerequisites

- .NET 10 SDK (for running locally or running the tests)
- Docker and Docker Compose (for the containerised run)

## Running

### With Docker Compose (recommended)

```bash
docker compose up --build
```

Gateway: `http://localhost:5000`, Account Service: `http://localhost:5001`.

### With the .NET SDK

```bash
dotnet restore
dotnet build

# in two terminals:
dotnet run --project src/AccountService
dotnet run --project src/EventGateway
```

### Interactive API docs

In Development, each service serves an OpenAPI document and a Scalar UI:

- Gateway: `http://localhost:5000/scalar/v1` (document at `/openapi/v1.json`)
- Account Service: `http://localhost:5001/scalar/v1`

OpenAPI and the UI are exposed only in Development; a public service should not
advertise its full API surface by default in production.

For the observability run profiles, sample requests, and step-by-step
verification of metrics and traces, see [docs/RUNBOOK.md](docs/RUNBOOK.md).

## Running the tests

```bash
dotnet test
```

Tests cover idempotency, out-of-order tolerance, balance correctness, input
validation, the resilience pipeline behaviour, and trace propagation, plus
per-service and end-to-end coverage.

## Resiliency pattern (and why)

The Gateway calls the Account Service through a typed `HttpClient` registered
with `IHttpClientFactory` and wrapped in Polly's standard resilience handler
(`Microsoft.Extensions.Http.Resilience`). The single handler combines, in order:

1. a total request timeout,
2. retry with exponential backoff and jitter,
3. a per-attempt timeout, and
4. a circuit breaker.

The requirement asks for at least one of circuit breaker, bulkhead, or timeout
plus retry. The standard handler was chosen because it delivers several of these
in one maintained, well-tested component rather than hand-assembled individual
policies, and it is the recommended path on .NET 10. Retry with backoff and
jitter absorbs transient blips; the per-attempt and total timeouts stop slow
calls from hanging; the circuit breaker stops hammering an Account Service that
is clearly down and fails fast instead. Tuned values and the full rationale are
in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Key properties

- **Idempotency**: a duplicate `eventId` never creates a second event or changes
  a balance. Enforced by a unique key in each SQLite database, so it holds even
  under concurrent duplicate submissions.
- **Out-of-order tolerance**: listings are ordered by `eventTimestamp`, and
  balances are correct regardless of arrival order because a balance is a
  commutative sum of credits minus debits.
- **Distributed tracing**: a trace id is created at the Gateway and propagated to
  the Account Service over HTTP using W3C trace context; both services log it.
- **Graceful degradation**: the Gateway's event read endpoints keep working when
  the Account Service is down; the write path and balance proxy return a clear
  503.
- **Observability**: structured JSON logs with trace id, health checks with a
  database connectivity check, distributed tracing to Jaeger, and custom metrics
  exposed at `/metrics` for Prometheus with Grafana dashboards.

## Optional features

These are off by default so the demo and a reviewer's first run are never
disrupted. Each is enabled by configuration.

### Authentication (flag-gated)

- `X-Api-Key` on the Gateway public surface. Enable with `ApiKeyAuth:Enabled=true`
  and set `ApiKeyAuth:ApiKey`. Health is always open.
- `X-Internal-Token` shared secret on the internal Gateway to Account Service
  call. Enable with `InternalAuth:Enabled=true` and set `InternalAuth:Token` on
  both services.

The production path (JWT or OAuth2 at the gateway, mTLS internally, secrets in a
vault) is described in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Rate limiting (flag-gated)

A fixed-window limiter on the Gateway returns 429 when the per-window request
limit is exceeded. Enable with `RateLimiting:Enabled=true` and tune
`RateLimiting:PermitLimit` and `RateLimiting:WindowSeconds`. Health is never
throttled.

### Trace visualisation with Jaeger

Run the observability profile and point the services at the Jaeger collector:

```bash
# Windows PowerShell
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://jaeger:4317"; docker compose --profile observability up --build

# bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317 docker compose --profile observability up --build
```

The Jaeger UI is at `http://localhost:16686`. Submit an event and you can see a
single trace span the Gateway and the Account Service. Without the endpoint set,
the services export traces to the console only, and Jaeger is not started.

### Metrics with Prometheus and Grafana

Tracing (Jaeger) and metrics (Prometheus) cover two different observability
pillars and are not interchangeable. Jaeger reconstructs the journey of one
request across both services. Prometheus stores aggregate time-series (totals,
rates, percentiles) and is what dashboards and alerts are built on. Jaeger does
not store arbitrary custom metrics and samples its trace data, so it cannot give
accurate aggregate counts; Prometheus keeps every sample. The two are used
together: a spike seen in Prometheus is then investigated as a specific failing
request in Jaeger. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full
explanation.

Each service exposes its metrics in Prometheus text format at `/metrics`
(separate from `/health`). With the services running you can scrape them directly:

```bash
curl http://localhost:5000/metrics   # event-gateway
curl http://localhost:5001/metrics   # account-service
```

The observability profile also starts Prometheus (which scrapes both `/metrics`
endpoints) and Grafana (which charts them). The same command brings up Jaeger,
Prometheus, and Grafana together:

```bash
# Windows PowerShell
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://jaeger:4317"; docker compose --profile observability up --build

# bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317 docker compose --profile observability up --build
```

- Prometheus UI: `http://localhost:9090` (basic, for ad hoc PromQL).
- Grafana: `http://localhost:3000` (anonymous viewer enabled locally). Grafana is
  provisioned from files, so it comes up already wired to Prometheus (metrics)
  and Jaeger (traces) with a starter "Event Ledger Overview" dashboard, no manual
  setup. Submit a few events, then watch `events_ingested_total` and
  `transactions_applied_total` move on the dashboard.

Grafana is a visualisation layer, not a store: it queries Prometheus for metrics
and Jaeger for traces, giving one pane of glass over both pillars. The metric
tags are deliberately low-cardinality (only the transaction `type`), which keeps
the Prometheus series count small. In production the `/metrics` endpoint would be
bound to an internal port or placed behind auth rather than exposed publicly.

For a hands-on walkthrough covering how to run the system, send sample events,
and verify the telemetry in Jaeger and Grafana, see [docs/RUNBOOK.md](docs/RUNBOOK.md).

A single-container alternative to the self-hosted stack is also included: run
`docker compose --profile aspire up --build` to view traces, metrics, and logs
in the .NET Aspire Dashboard. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
(section 17d) for the comparison of observability backends.


```
EventLedger.sln
docker-compose.yml
src/
  EventLedger.Shared/    cross-cutting infra only (logging, tracing, resilience, error contract)
  EventGateway/          public API, port 5000
  AccountService/        internal API, port 5001
observability/           Prometheus scrape config and Grafana provisioning (observability profile)
tests/
  EventGateway.Tests/
  AccountService.Tests/
  EventLedger.IntegrationTests/
docs/
  ARCHITECTURE.md
  RUNBOOK.md
  MINIMAL_APIS.md
```

Author: Chandra Mohan Busam
