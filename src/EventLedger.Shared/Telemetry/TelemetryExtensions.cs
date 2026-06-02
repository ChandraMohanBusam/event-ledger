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
/// Both signals always export to the console so they are visible locally with no
/// external collector. When an OTLP endpoint is configured (OTEL_EXPORTER_OTLP_ENDPOINT,
/// for example the Jaeger collector in the docker-compose observability profile),
/// traces are additionally exported over OTLP for visualisation.
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

                metrics.AddConsoleExporter();
            });

        return builder;
    }
}
