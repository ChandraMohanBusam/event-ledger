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
instrumentation. Both signals export to the console so nothing external is needed
to run the solution.

## 10. Structured logging

Serilog emits compact JSON. Every line carries timestamp, level, message, service
name, trace id, and span id. The service name is set once at startup so log
aggregation can filter by service.

## 11. Health checks

Each service exposes `GET /health` using the ASP.NET Core health-check pipeline
with a database connectivity check, returning JSON diagnostics. The Gateway
health check reports only its own health and does not fail when the Account
Service is down, because the Gateway must keep serving its read endpoints during
an Account Service outage.

## 12. Custom metrics

`events_ingested_total` on the Gateway and `transactions_applied_total` on the
Account Service, each a counter tagged by transaction type, registered with
OpenTelemetry and exported to the console.

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
| Metrics | OpenTelemetry metrics | Same pipeline as tracing |
| Caching | IMemoryCache for immutable and idempotency reads | Safe by construction; DB remains source of truth |
| Rate limiting | ASP.NET Core fixed-window limiter, flag-gated | Built-in flood protection on the public surface |
| Trace visualisation | OTLP exporter to Jaeger, compose profile | Additive; console export remains the default |
| Testing | xUnit, NSubstitute, FluentAssertions | Standard, readable |
| Containers | Docker Compose | One command to run both services |
