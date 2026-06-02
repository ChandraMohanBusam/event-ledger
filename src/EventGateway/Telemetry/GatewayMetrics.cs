using System.Diagnostics.Metrics;

namespace EventGateway.Telemetry;

/// <summary>
/// Custom metrics for the Gateway. Exposes a counter of events ingested,
/// tagged by transaction type, so ingest volume is visible per type without
/// scraping logs.
/// </summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "EventLedger.EventGateway";

    private readonly Counter<long> _eventsIngested;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _eventsIngested = meter.CreateCounter<long>(
            "events_ingested_total",
            unit: "{events}",
            description: "Total number of events successfully ingested by the gateway.");
    }

    public void EventIngested(string type) =>
        _eventsIngested.Add(1, new KeyValuePair<string, object?>("type", type));
}
