using Microsoft.AspNetCore.Builder;
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
/// Metrics register the service's custom meters alongside ASP.NET Core and
/// HttpClient request metrics. Both signals export to the console so they are
/// visible when running locally without any external collector.
/// </summary>
public static class TelemetryExtensions
{
    public static WebApplicationBuilder AddLedgerTelemetry(
        this WebApplicationBuilder builder,
        string serviceName,
        params string[] meterNames)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddConsoleExporter())
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
