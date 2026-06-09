# Event Ledger: run and verify walkthrough

A hands-on guide to running the system, sending sample events, and confirming
the telemetry in Jaeger and Grafana. For architecture and design decisions see
ARCHITECTURE.md; this document is the practical runbook.

## 1. Run the application

Three ways to run: Docker (recommended), Docker with observability, or manual
with the .NET SDK. Choose one below.

### Default run (two services only)

From the repository root:

    docker compose up --build

This starts the Event Gateway (public, port 5000) and the Account Service
(internal, port 5001). No observability backends are started in this mode;
traces and metrics export to the console.

Confirm both services are healthy:

    curl http://localhost:5000/health
    curl http://localhost:5001/health

Each returns a JSON document reporting the service name and a healthy status.

### Full run (with observability backends)

To start the services together with Jaeger, Prometheus, and Grafana, use the
observability profile and point the services at the Jaeger collector:

Windows PowerShell:

    $env:OTEL_EXPORTER_OTLP_ENDPOINT="http://jaeger:4317"; docker compose --profile observability up --build

bash:

    OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317 docker compose --profile observability up --build

This brings up five containers: the two services plus Jaeger, Prometheus, and
Grafana. The user interfaces are:

    Gateway API        http://localhost:5000
    Account API        http://localhost:5001
    Jaeger UI          http://localhost:16686
    Prometheus UI      http://localhost:9090
    Grafana            http://localhost:3000

To stop everything:

    docker compose --profile observability down

### Manual run (local .NET SDK, no Docker)

Requires the .NET 10 SDK installed. From the repository root.

Restore and build the whole solution:

    dotnet restore
    dotnet build

Run the two services. Each needs its own terminal, since dotnet run is a
foreground process:

Terminal 1, Account Service:

    dotnet run --project src/AccountService

Terminal 2, Event Gateway:

    dotnet run --project src/EventGateway

By default the services listen on the ports configured in launchSettings /
appsettings (5000 for the Gateway, 5001 for the Account Service). If you need to
pin them explicitly:

    dotnet run --project src/AccountService --urls http://localhost:5001
    dotnet run --project src/EventGateway --urls http://localhost:5000

In this mode traces and metrics export to the console of each service. There is
no Jaeger, Prometheus, or Grafana unless you start those separately; to send
traces to a locally running Jaeger, set the OTLP endpoint before running:

PowerShell:

    $env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"; dotnet run --project src/EventGateway

bash:

    OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project src/EventGateway

The /metrics endpoint works the same way in this mode (curl http://localhost:5000/metrics),
so you can scrape it with a local Prometheus if you have one.

In VS Code, the included launch configuration "Run Both (Gateway + Account
Service)" starts both services together with one action (F5), which is simpler
than two terminals.

### Run the tests

From the repository root:

    dotnet test

To run a single project's tests:

    dotnet test tests/EventGateway.Tests
    dotnet test tests/AccountService.Tests
    dotnet test tests/EventLedger.IntegrationTests

The suite is expected to pass with several integration tests skipped by design;
the skip reasons are documented in ARCHITECTURE.md (testing section).

## 2. Send sample events

Events are posted to the Gateway, which validates, forwards to the Account
Service, and persists locally only on success. A 201 means the event was
created; a 200 means it was a duplicate (idempotency); a 400 returns a
ProblemDetails body naming the field that failed validation.

### A single credit

bash:

    curl -X POST http://localhost:5000/events \
      -H "Content-Type: application/json" \
      -d '{
        "eventId": "11111111-1111-1111-1111-111111111111",
        "accountId": "acc-001",
        "type": "CREDIT",
        "amount": 100.00,
        "currency": "USD",
        "eventTimestamp": "2026-06-09T12:00:00Z"
      }'

Windows PowerShell:

    $body = @{
      eventId        = [guid]::NewGuid().ToString()
      accountId      = "acc-001"
      type           = "CREDIT"
      amount         = 100.00
      currency       = "USD"
      eventTimestamp = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json
    Invoke-RestMethod -Uri "http://localhost:5000/events" -Method Post -Body $body -ContentType "application/json"

### A debit

Same call with "type": "DEBIT" and a different eventId.

### A small batch (to give the dashboards something to show)

bash:

    for i in $(seq 1 10); do
      curl -s -X POST http://localhost:5000/events \
        -H "Content-Type: application/json" \
        -d "{\"eventId\":\"$(uuidgen)\",\"accountId\":\"acc-001\",\"type\":\"CREDIT\",\"amount\":25,\"currency\":\"USD\",\"eventTimestamp\":\"2026-06-09T12:00:00Z\"}" > /dev/null
      curl -s -X POST http://localhost:5000/events \
        -H "Content-Type: application/json" \
        -d "{\"eventId\":\"$(uuidgen)\",\"accountId\":\"acc-001\",\"type\":\"DEBIT\",\"amount\":10,\"currency\":\"USD\",\"eventTimestamp\":\"2026-06-09T12:00:00Z\"}" > /dev/null
    done

### Read the data back

    curl http://localhost:5000/events/11111111-1111-1111-1111-111111111111
    curl "http://localhost:5000/events?account=acc-001"
    curl http://localhost:5000/accounts/acc-001/balance

The balance call exercises the Gateway-to-Account proxy path and adds more trace
and metric activity. The balance is computed on read as the sum of credits minus
debits.

### Demonstrating idempotency

Send the single credit call twice with the same eventId. The first returns 201,
the second returns 200 with the original event, and the balance does not change.

## 3. Verify metrics

### Raw endpoint (fastest check)

Each service exposes Prometheus text format at /metrics:

    curl http://localhost:5000/metrics
    curl http://localhost:5001/metrics

Look for the two custom counters. Note the exact series names here, since the
Prometheus exporter naming is what Grafana queries must match:

    events_ingested_total       (event-gateway)
    transactions_applied_total  (account-service)

### Prometheus

Open http://localhost:9090. Under Status, then Targets, both event-gateway and
account-service should show state UP, which confirms scraping works. In the
expression bar, run:

    events_ingested_total
    rate(events_ingested_total[1m])
    sum by (type) (rate(events_ingested_total[1m]))

The Prometheus user interface is intentionally minimal and is meant for ad hoc
query debugging. Time range is a separate control, not part of the query. For
readable dashboards and time-range browsing, use Grafana.

### Grafana

Open http://localhost:3000 (anonymous viewer is enabled locally, no sign-in).
Open the Event Ledger Overview dashboard. After sending events you should see:

    Events ingested (total) and Transactions applied (total) climb
    The by-type panels show separate CREDIT and DEBIT lines
    HTTP server request rate reflects the scrape and request traffic

Use the time-range picker (top right) to set the window, and the refresh control
for live updates. The Explore view (compass icon) is the place for ad hoc PromQL
with a proper graph and time picker.

If a panel reads zero while the raw /metrics curl shows data, the metric series
name differs from the dashboard query. Confirm the name in Prometheus and adjust
the panel expression to match.

## 4. Verify tracing

Open http://localhost:16686 (Jaeger). In the Service dropdown choose
event-gateway, then Find Traces. You will see traces for GET /health and
GET /metrics (the health and scrape loops) and for POST /events.

Open a POST /events trace. It should span two services: the Gateway span as the
parent and the outbound call into the Account Service as a child span, sharing
one trace id. This is the distributed trace produced by W3C trace-context
propagation across the service boundary. The /health and /metrics traces touch
only the Gateway and correctly show a single span.

Use the Lookback control and the Min and Max Duration filters to narrow results.

## 5. Notes

The /metrics endpoint is left open here so it can be inspected directly. In
production it would be bound to an internal port or placed behind the same auth
as the rest of the surface. Metric tags are deliberately low-cardinality (the
transaction type only) to keep the Prometheus series count bounded. All three
observability backends stay behind the observability profile, so the default
docker compose up runs only the two services.

Author: Chandra Mohan Busam