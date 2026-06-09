using System.Diagnostics;
using EventLedger.Shared.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace EventLedger.Shared.Logging;

/// <summary>
/// Configures Serilog to emit structured JSON logs that always carry the
/// service name, and (once an Activity exists) the current trace id and span id.
/// Both services call this the same way, so their log shape is identical and a
/// single trace id ties their log lines together.
///
/// Serilog remains the single application logging pipeline. The console sink is
/// always present for local readable output. When an OTLP endpoint is configured
/// (OTEL_EXPORTER_OTLP_ENDPOINT), an OpenTelemetry sink is added so the same log
/// records are also exported over OTLP to a backend such as the Aspire Dashboard
/// (Structured Logs tab) or a cloud platform. This is how logs join traces and
/// metrics as the third signal over OTLP without giving up Serilog or its
/// enrichment: one log call, console plus OTLP.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Wires Serilog into the host with a compact JSON sink on stdout, plus an
    /// OpenTelemetry OTLP sink when an endpoint is configured. Logs include
    /// timestamp, level, message, service name, trace id, and span id.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="serviceName">Logical service name, for example "event-gateway".</param>
    public static WebApplicationBuilder AddLedgerLogging(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Host.UseSerilog((context, loggerConfig) =>
        {
            loggerConfig
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty(TraceConstants.ServiceNameLogProperty, serviceName)
                .Enrich.With(new ActivityTraceEnricher())
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .WriteTo.Console(new CompactJsonFormatter());

            // Export the same log records over OTLP when an endpoint is set, so
            // logs reach the Aspire Dashboard or a cloud backend alongside traces
            // and metrics. The service.name resource attribute lets the backend
            // group logs by service. The Serilog enrichers above (trace id, span
            // id) ride along, so exported logs stay correlated to their traces.
            if (hasOtlp)
            {
                loggerConfig.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName
                    };
                });
            }
        });

        return builder;
    }
}

/// <summary>
/// Enriches each log event with the current Activity's trace id and span id, so
/// every line emitted while handling a request can be correlated to its trace.
/// When no Activity is active (for example at startup) the properties are omitted.
/// </summary>
public sealed class ActivityTraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            TraceConstants.TraceIdLogProperty, activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            TraceConstants.SpanIdLogProperty, activity.SpanId.ToString()));
    }
}
