using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// Exporters are chosen so each signal reaches the backend that suits it, and so
/// the three OpenTelemetry signals (traces, metrics, logs) all travel over OTLP
/// when an endpoint is set. Logs are exported from Serilog (see LoggingExtensions);
/// traces and metrics are exported here:
///
///   Traces  -> console in Development (local visibility), plus OTLP when an
///              endpoint is configured (Jaeger, or the Aspire Dashboard, or a
///              cloud backend). Tracing answers "what happened to this one
///              request as it crossed both services".
///   Metrics -> a Prometheus scraping endpoint at /metrics in every environment,
///              plus console in Development, plus OTLP when an endpoint is
///              configured. Metrics answer "what is the aggregate behaviour over
///              time" (rates, totals, percentiles).
///
/// The console exporters are deliberately limited to Development because their
/// output is verbose; Prometheus scraping and OTLP export are the paths used
/// outside local development. Jaeger does not store arbitrary custom metrics and
/// trace data is typically sampled, so it cannot give accurate aggregate counts;
/// Prometheus keeps every metric data point. The Aspire Dashboard, by contrast,
/// receives all three signals over OTLP and shows them in one UI. The common
/// thread is that the application instruments once and the backend is a choice:
/// the same OTLP output feeds Jaeger, the Aspire Dashboard, or a cloud platform.
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
        var isDevelopment = builder.Environment.IsDevelopment();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                // Console is local-only noise; keep it to Development.
                if (isDevelopment)
                {
                    tracing.AddConsoleExporter();
                }

                // OTLP carries traces to Jaeger, the Aspire Dashboard, or a cloud
                // backend whenever an endpoint is configured.
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

                // Prometheus scraping is always available: the exporter registers
                // the in-process collector that the /metrics endpoint (mapped via
                // MapLedgerMetricsEndpoint) serves in Prometheus text format.
                metrics.AddPrometheusExporter();

                // Console metrics are verbose; keep them to Development.
                if (isDevelopment)
                {
                    metrics.AddConsoleExporter();
                }

                // OTLP carries metrics to the Aspire Dashboard or a cloud backend
                // (push), complementing the Prometheus scrape (pull).
                if (hasOtlp)
                {
                    metrics.AddOtlpExporter();
                }
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
