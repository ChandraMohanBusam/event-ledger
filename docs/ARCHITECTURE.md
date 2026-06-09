# Event Ledger: Architecture and Design Decisions

This document records the design decisions behind the Event Ledger and the
reasoning for each. It is written to support a live design walkthrough.

## 1. System overview

Two independently deployable ASP.NET Core services communicate over synchronous
REST. There is no shared database and no shared runtime state.

- The Event Gateway owns the event ledger (the immutable record of submitted
  events) and is the only public-facing surface.
- The Account Service owns transactions and balances and is internal.
- The Gateway never reads the Account Service database; it calls the Account
  Service HTTP API.

## 2. Why a shared library does not violate "no shared state"

`EventLedger.Shared` contains only cross-cutting infrastructure setup: structured
JSON logging, the ProblemDetails error contract, the health response writer,
OpenTelemetry wiring, the resilience pipeline configuration, and trace header
constants. It holds no domain types (no Event, no Transaction, no balance math)
and no runtime state (no DbContext, no store).

"No shared state" is a runtime data constraint: each service has its own database
and can run, deploy, and fail independently. Sharing a small platform library for
logging and tracing is normal practice and does not couple the services at
runtime. Each service could move to its own repository by copying that one
library.

## 3. Database: SQLite in-memory

Each service uses its own SQLite in-memory database via EF Core. SQLite was
chosen over the EF Core InMemory provider because the exercise centres on
idempotency, and SQLite enforces real unique constraints and supports real
transactions, whereas the InMemory provider does not. The idempotency guarantee
therefore rests on a genuine database constraint, not on application-level
check-then-insert alone.

A single `SqliteConnection` is opened once and held for the life of the process,
because an in-memory SQLite database exists only while a connection to it is
open. The `DbContext` is registered Scoped per request and uses that shared
connection; the `DbContext` is never shared across requests.

Connection strings come from configuration (appsettings plus environment
variable override), so moving to a file-based SQLite or a server database (SQL
Server, PostgreSQL) is a connection-string change with no code change. The
`Data/Scripts/schema.sql` and `seed.sql` in each service are the schema-of-record
for that move; EF builds the same shape at startup for the in-memory demo, and
the scripts are kept in sync with the model.

### A provider note worth stating

SQLite has no native `decimal` or `DateTimeOffset`. Balance aggregation and event
ordering are therefore performed in memory (client-side) after materialising the
rows, rather than translated to SQL. On a server database these would translate
directly to SQL.

## 4. Idempotency

`eventId` is the idempotency key end to end. The Gateway event store has a unique
key on `eventId`; the Account Service transaction store has a unique key on the
same id (the transaction id equals the originating `eventId`). A repeat
submission returns the original with 200 rather than creating a duplicate, and a
concurrent duplicate is caught at the unique constraint and treated as a
duplicate, not an error. Because both sides key on the same id, every duplicate,
retry, or lost-response scenario converges to a single event and a single
transaction, so a duplicate never alters a balance.

## 5. Write ordering: forward-then-persist (decision)

POST /events validates, checks local idempotency, forwards the transaction to the
Account Service, and persists the event locally only if the forward succeeded. An
event therefore exists in the Gateway only if its transaction was applied, which
keeps the two stores consistent. If the Account Service is unreachable, nothing
is stored and the caller gets a 503; the same `eventId` can be retried safely
because both services are idempotent.

Alternative considered: an outbox (persist first, return 202, drain
asynchronously). It offers higher write availability but changes the POST
semantics, and the requirement is for POST to return 503 when the Account Service
is unavailable. Forward-then-persist is therefore the spec-compliant default; the
outbox is the documented scaling path and could be added behind a flag.

## 6. Out-of-order tolerance

Events carry a producer `eventTimestamp` and may arrive in any order. Listings are
ordered by `eventTimestamp`, not arrival order. Balance is the sum of CREDIT
amounts minus the sum of DEBIT amounts; addition and subtraction are commutative,
so arrival order cannot affect the result.

## 7. Balance: compute-on-read (decision)

The Account Service stores transactions as an immutable log and computes the
balance by aggregating that log on read. This is correct under idempotency and
out-of-order arrival and matches ledger semantics (the log is the source of
truth). A materialised running balance updated in the same transaction as the
insert is a valid performance optimisation and is the natural scaling step, but
compute-on-read is the safer default for correctness and is more than fast enough
at this scale.

## 8. Currency: single currency per account (decision)

A net balance is a single number, so mixing currencies in one balance is
meaningless. Rather than leave this as an unstated assumption, it is enforced: a
transaction whose currency differs from the account's established currency is
rejected with a clear 400. At scale the right design is a separate balance per
currency with no implicit conversion; that is out of scope here and is recorded
as the extension path.

## 9. Distributed tracing

OpenTelemetry with W3C Trace Context. A trace id is created at the Gateway when a
request arrives, and the HttpClient instrumentation propagates the `traceparent`
header to the Account Service automatically, so both services share one trace id.
A Serilog enricher writes the trace id and span id into every log line, so logs
and traces correlate. OpenTelemetry was chosen over hand-rolled header plumbing
precisely because propagation and correlation come from the standard
instrumentation. Traces export to the console in Development for local visibility,
and over OTLP whenever an endpoint is configured, so the same traces reach Jaeger,
the Aspire Dashboard, or a cloud backend without any code change.

## 10. Structured logging

Serilog emits compact JSON. Every line carries timestamp, level, message, service
name, trace id, and span id. The service name is set once at startup so log
aggregation can filter by service. Serilog remains the single application logging
pipeline; the console sink is always present. When an OTLP endpoint is configured,
an OpenTelemetry sink is added to Serilog so the same log records are also
exported over OTLP, which makes logs the third signal (alongside traces and
metrics) in a backend such as the Aspire Dashboard. The trace-id enrichment rides
along, so exported logs stay correlated to their traces. This keeps Serilog and
its formatting and enrichment while gaining OTLP delivery: one log call, console
plus OTLP.

## 11. Health checks

Each service exposes `GET /health` using the ASP.NET Core health-check pipeline
with a database connectivity check, returning JSON diagnostics. The Gateway
health check reports only its own health and does not fail when the Account
Service is down, because the Gateway must keep serving its read endpoints during
an Account Service outage.

## 12. Custom metrics

`events_ingested_total` on the Gateway and `transactions_applied_total` on the
Account Service, each a counter tagged by transaction type, registered with
OpenTelemetry. Each service exposes a Prometheus scraping endpoint at `/metrics`
in every environment (see section 17c), exports metrics to the console in
Development for local visibility, and exports over OTLP whenever an endpoint is
configured (a push to the Aspire Dashboard or a cloud backend, complementing the
Prometheus pull). The console exporters for traces and metrics are limited to
Development because their output is verbose. The single tag is the transaction
`type`, deliberately low-cardinality: every tag value becomes a separate
Prometheus time series, so unbounded or high-cardinality tags (account ids,
request ids) would multiply series and strain the metrics store. Keeping the tag
set small is a conscious production-minded choice.

## 13. Resiliency: Gateway to Account Service

The Gateway calls the Account Service through a typed `HttpClient` wrapped in
Polly's standard resilience handler: total request timeout, retry with
exponential backoff and jitter, per-attempt timeout, and circuit breaker. This
satisfies the "at least one pattern" requirement and goes a little beyond by
combining several in one maintained component, which is the recommended path on
.NET 10 over hand-assembled policies.

Tuned values (in `EventLedger.Shared/Resilience/ResiliencePolicy.cs`): 3 retries;
3s per-attempt timeout; 10s total timeout; circuit breaker at a 50 percent
failure ratio over a 10s sampling window with a minimum throughput of 5 and a 5s
break. The break is deliberately short so the circuit-open behaviour is
demonstrable quickly rather than tuned for production load.

## 14. Graceful degradation

The Gateway's event read endpoints read only from the Gateway's own store and do
not call the Account Service, so they keep working when the Account Service is
down. The write path and the balance proxy depend on the Account Service and
return a clear 503 when it is unreachable. The resilience client translates
transport failures, timeouts, and an open circuit into an "unavailable" result so
these paths degrade rather than throwing a 500.

## 15. Balance proxy on the Gateway

The handout's requirement that balance queries return a clear error when the
Account Service is unavailable applies to the public layer, but the literal
Gateway endpoint table has no balance endpoint. A thin Gateway balance proxy
(`GET /accounts/{accountId}/balance`) resolves this: it forwards to the Account
Service through the same resilient client and returns a clear 503 when the
service is down, so the requirement is satisfiable on the surface clients call.

## 16. Caching

`IMemoryCache` is used for two safe cases: idempotency lookups on the write path,
and the immutable `GET /events/{id}` read. Both are safe because an event is
immutable once written and the database unique constraint remains the source of
truth, so a cache can never cause incorrectness.

Two things are deliberately not cached. Balances are never cached: a financial
balance must not be stale, and the correct scaling step is a materialised running
balance, not a TTL cache. The account event list (`GET /events?account=`) is not
cached locally either: it grows over time and, under multiple Gateway instances,
a local cache could serve a stale list that omits a committed event. The correct
scaling step there is a distributed cache with pub/sub invalidation or a read
replica, chosen deliberately over a naive local cache.

## 17. Security

The public API and the internal call each have a flag-gated authentication
mechanism, defaulting off for the demo: an `X-Api-Key` header on the Gateway
public surface and an `X-Internal-Token` shared secret on the Gateway to Account
Service call, with keys supplied from configuration. The internal token is
attached by a `DelegatingHandler` on the typed client and validated by middleware
on the Account Service. Health endpoints are always exempt so liveness checks
work regardless. The production path is JWT or OAuth2 at the gateway, mTLS or a
service mesh for internal service-to-service trust, and secrets held in a vault
rather than configuration. Full JWT/OAuth was intentionally not built, as it is
out of scope for the exercise and would not change the architecture being
demonstrated.

## 17a. Rate limiting (optional)

The Gateway has a flag-gated fixed-window rate limiter (built into ASP.NET Core,
no extra package) that returns 429 when the per-window request limit is exceeded.
It is applied as a global limiter that exempts health endpoints, and is disabled
by default. For a public-facing financial API this is the expected first line of
flood protection; the limits are configuration-driven so they can be tuned per
environment. At scale a distributed limiter (for example backed by Redis) would
coordinate limits across multiple Gateway instances.

## 17b. Trace visualisation with Jaeger (optional)

The OpenTelemetry tracing pipeline always exports to the console. When an OTLP
endpoint is configured (`OTEL_EXPORTER_OTLP_ENDPOINT`), traces are additionally
exported over OTLP. The docker-compose observability profile starts a Jaeger
all-in-one container; pointing the services at it shows a single trace spanning
the Gateway and the Account Service in the Jaeger UI. This is purely additive: it
changes no application behaviour and adds no dependency to the default run.

## 17c. Metrics with Prometheus and Grafana (optional)

Tracing and metrics are two different pillars of observability, which is why both
Jaeger and Prometheus appear in the stack rather than one tool covering both.

- Tracing (Jaeger) answers "what happened to this one request as it crossed both
  services": the path, the timing of each hop, where it failed. It is oriented
  around individual request journeys and its data is typically sampled.
- Metrics (Prometheus) answer "what is the aggregate behaviour over time":
  totals, rates, error ratios, latency percentiles. This is what dashboards and
  alerts are built on.

Jaeger does not store arbitrary custom metrics, and because trace data is sampled
it cannot give accurate aggregate counts. Prometheus keeps every sample in a
time-series store and provides a query language (PromQL) for aggregation and
alerting. The two are complementary: a spike seen in Prometheus is then
investigated as a specific failing request in Jaeger. Of the four pillars
(metrics, logs, traces, profiles), no single open-source component covers all
well, so each pillar uses a backend suited to its data shape.

Each service exposes `/metrics` in Prometheus text format via the OpenTelemetry
Prometheus exporter, separate from `/health`. The observability profile adds two
containers:

- Prometheus scrapes both `/metrics` endpoints on an interval and stores the
  samples. Its own UI (`:9090`) is intentionally basic, suited to ad hoc PromQL.
- Grafana is the visualisation layer. It is not a store; it queries Prometheus
  for metrics and Jaeger for traces, giving one pane of glass over both pillars.
  Grafana is provisioned from files (data sources and a starter dashboard as
  code), so it comes up wired and reproducible with no manual clicking, the
  infrastructure-as-code pattern rather than a hand-configured instance that does
  not survive a restart.

The same instrumentation underlies all of this. The services emit OpenTelemetry;
the backend is a choice, not a rewrite. Locally that backend is Jaeger plus
Prometheus and Grafana; in production the same OTLP and metrics output would
point at a managed platform (Azure Monitor or Application Insights, CloudWatch,
Datadog) or a self-hosted stack, selected by configuration. "Instrument once,
choose the backend" is the design intent.

Two production notes. The `/metrics` endpoint is open here so a reviewer can curl
it; in production it would be bound to an internal port or placed behind the same
auth as the rest of the surface. And the metric tag set is kept low-cardinality
(transaction `type` only) to bound the Prometheus series count. All three
backends stay behind the `observability` profile, so the default
`docker compose up` runs only the two services.

## 17d. Choosing an observability backend (Aspire Dashboard, self-hosted, cloud)

The services instrument once with OpenTelemetry and the backend is a deployment
choice, not a code change. Three options are demonstrated or documented here, all
consuming the same OTLP and metrics output.

Self-hosted stack (Jaeger plus Prometheus plus Grafana). The `observability`
profile above. This is the shape of a real self-hosted deployment and makes the
division of labour explicit: Prometheus stores metrics, Grafana visualises them,
Jaeger stores and visualises traces. It is the most moving parts and the most
to operate, but nothing is hidden.

.NET Aspire Dashboard (single container). The `aspire` profile starts the
standalone Aspire Dashboard, which receives traces, metrics, and logs over OTLP
and shows all three in one UI with no configuration. It replaces the entire
three-container stack for local development with one container. The trade-off is
that it is a development tool only: telemetry is held in memory, not persisted,
with no alerting or long retention. It is the fastest way to see all signals
while developing, and because it speaks OTLP it needs no change to the service
code, only the OTLP endpoint is pointed at it.

Cloud-managed (production). In production the same instrumentation exports to a
managed platform such as Azure Monitor or Application Insights, AWS CloudWatch,
or Datadog, selected by configuration rather than code. These provide durable
storage, querying, dashboards, and alerting that the dev-time dashboards do not.
For the Microsoft stack the Application Insights exporter wires in like this
(package `Azure.Monitor.OpenTelemetry.Exporter`):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(o =>
        o.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(o =>
        o.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]));
```

The connection string is read from configuration (an environment variable here),
never hard-coded, consistent with the rest of the configuration approach. The
key point across all three options: the application emits OpenTelemetry, and
Jaeger, Prometheus with Grafana, the Aspire Dashboard, and Application Insights
are interchangeable destinations for that same telemetry. Instrument once, choose
the backend.

A note on scope. This project uses the standalone Aspire Dashboard as an OTLP
destination only. It does not adopt the Aspire orchestration model (the AppHost
project), which would replace docker-compose and manage the services' lifecycle
and configuration. That is a larger paradigm change with no benefit for a system
that already runs cleanly under compose; the dashboard delivers the observability
value on its own.

For the practical steps, running the stack, sending sample events, and confirming
this telemetry in Jaeger and Grafana, see [RUNBOOK.md](RUNBOOK.md).

## 18. Minimal APIs

Endpoints use minimal APIs grouped into extension methods rather than MVC
controllers. For small, focused services this avoids controller machinery that
only pays off at larger endpoint counts, it is the modern .NET default, and it
keeps each endpoint readable in one place. Controllers remain a valid choice for
a large API with many endpoints, shared filters, or a team standard that favours
them.

## 19. Testing strategy and a candid note

xUnit with NSubstitute and FluentAssertions. Coverage:

- Idempotency, balance, out-of-order, currency enforcement, validation, and
  unknown-account handling at the service level (Account Service and Gateway).
- The resilience pipeline (retry and unavailable handling) against a failing
  handler.
- An end-to-end validation test through the in-memory hosts.

Four end-to-end tests are skipped with documented reasons. Three exercise the
full Gateway-to-Account flow over HTTP inside `WebApplicationFactory`; reliably
redirecting the Gateway's resilience-wrapped typed client to the Account Service
test server needs more harness work, and the behaviours are already covered at
the service level. One asserts HttpClient `traceparent` injection in isolation,
which is environment-sensitive; propagation itself is implemented via the
OpenTelemetry HttpClient instrumentation and is observable at runtime through the
console exporter and Jaeger. The decision was to keep the suite green and honest
rather than leave brittle tests failing.

## 20. Technology choices, summarised

| Concern | Choice | Reason |
|---|---|---|
| Language | C# | Primary stack |
| Framework | ASP.NET Core, .NET 10 (LTS) | Current LTS, three-year support |
| Database | SQLite in-memory, one per service | Real constraints and transactions; no shared state |
| API style | Minimal APIs | Light, modern default for focused services |
| Resiliency | Polly standard resilience handler | Breaker plus retry plus timeout in one maintained component |
| Tracing | OpenTelemetry, W3C trace context | Standard propagation and log correlation |
| Logging | Serilog JSON | Structured, easy enrichment |
| Metrics | OpenTelemetry metrics, Prometheus `/metrics` endpoint | Same instrumentation pipeline as tracing; standard scrape format |
| Caching | IMemoryCache for immutable and idempotency reads | Safe by construction; DB remains source of truth |
| Rate limiting | ASP.NET Core fixed-window limiter, flag-gated | Built-in flood protection on the public surface |
| Trace visualisation | OTLP exporter to Jaeger, compose profile | Additive; console export remains the default |
| Metrics visualisation | Prometheus scrape plus Grafana, compose profile | Provisioned from files; Grafana queries Prometheus and Jaeger as one pane |
| All-in-one dev telemetry | Standalone .NET Aspire Dashboard, compose profile | One container for traces, metrics, and logs over OTLP; dev-time alternative |
| Testing | xUnit, NSubstitute, FluentAssertions | Standard, readable |
| Containers | Docker Compose | One command to run both services |
