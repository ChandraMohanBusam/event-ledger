using System.Diagnostics;
using EventLedger.Shared.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace EventLedger.Shared.Logging;

/// <summary>
/// Configures Serilog to emit structured JSON logs that always carry the
/// service name, and (once an Activity exists) the current trace id and span id.
/// Both services call this the same way, so their log shape is identical and a
/// single trace id ties their log lines together.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Wires Serilog into the host with a compact JSON sink on stdout. Logs
    /// include timestamp, level, message, service name, trace id, and span id.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="serviceName">Logical service name, for example "event-gateway".</param>
    public static WebApplicationBuilder AddLedgerLogging(
        this WebApplicationBuilder builder,
        string serviceName)
    {
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
