using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EventLedger.Shared.Telemetry;

/// <summary>
/// Configures OpenTelemetry tracing and metrics identically for both services.
///
/// Tracing uses ASP.NET Core and HttpClient instrumentation with W3C Trace
/// Context. A trace id is created at the Gateway when a request arrives, and the
/// HttpClient instrumentation propagates the traceparent header to the Account
/// Service automatically, so one trace id spans both services. The Serilog
/// enricher writes that trace id into every log line, correlating logs and
/// traces.
///
/// The two observability signals are aimed at different backends, which is the
/// reason both Jaeger and Prometheus appear in the stack:
///
///   Traces  -> always to the console, plus OTLP (Jaeger) when an OTLP endpoint
///              is configured. Tracing answers "what happened to this one
///              request as it crossed both services".
///   Metrics -> always to the console, plus a Prometheus scraping endpoint at
///              /metrics. Metrics answer "what is the aggregate behaviour over
///              time" (rates, totals, percentiles) and are what dashboards and
///              alerts are built on.
///
/// Jaeger does not store arbitrary custom metrics and trace data is typically
/// sampled, so it cannot give accurate aggregate counts. Prometheus keeps every
/// metric data point in a time-series store. The two tools are complementary,
/// not interchangeable: a spike seen in Prometheus is then investigated for a
/// specific failing request in Jaeger.
/// </summary>
public static class TelemetryExtensions
{
    public static WebApplicationBuilder AddLedgerTelemetry(
        this WebApplicationBuilder builder,
        string serviceName,
        params string[] meterNames)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter();

                if (hasOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                foreach (var meterName in meterNames)
                {
                    metrics.AddMeter(meterName);
                }

                // Console keeps metrics visible locally with no external tool.
                // The Prometheus exporter registers the in-process collector that
                // the /metrics endpoint (mapped via MapLedgerMetricsEndpoint)
                // serves in Prometheus text format for scraping.
                metrics
                    .AddConsoleExporter()
                    .AddPrometheusExporter();
            });

        return builder;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint at /metrics. Both services expose
    /// the same low-cardinality counters (tagged only by transaction type), so a
    /// Prometheus server can scrape them and Grafana can chart them.
    ///
    /// In production this endpoint would be bound to an internal port or placed
    /// behind the same auth as the rest of the surface rather than exposed
    /// publicly; for the exercise it is left open so a reviewer can curl it.
    /// </summary>
    public static WebApplication MapLedgerMetricsEndpoint(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();
        return app;
    }
}
