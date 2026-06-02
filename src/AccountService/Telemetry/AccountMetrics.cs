using System.Diagnostics.Metrics;

namespace AccountService.Telemetry;

/// <summary>
/// Custom metrics for the Account Service. Exposes a counter of transactions
/// applied, tagged by type.
/// </summary>
public sealed class AccountMetrics
{
    public const string MeterName = "EventLedger.AccountService";

    private readonly Counter<long> _transactionsApplied;

    public AccountMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _transactionsApplied = meter.CreateCounter<long>(
            "transactions_applied_total",
            unit: "{transactions}",
            description: "Total number of transactions applied to accounts.");
    }

    public void TransactionApplied(string type) =>
        _transactionsApplied.Add(1, new KeyValuePair<string, object?>("type", type));
}
