using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace EventLedger.Shared.Resilience;

/// <summary>
/// Central configuration for the resilience pipeline applied to the Gateway's
/// call to the Account Service. Built on the standard resilience handler
/// (Polly v8 underneath), which combines, in order: a total request timeout, a
/// retry with exponential backoff and jitter, a per-attempt timeout, and a
/// circuit breaker.
///
/// Values are tuned so the circuit is demonstrable quickly rather than for a
/// high-throughput production load. The reasoning is documented in
/// docs/ARCHITECTURE.md.
/// </summary>
public static class ResiliencePolicy
{
    public const int MaxRetryAttempts = 3;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(10);

    public const double CircuitFailureRatio = 0.5;
    public static readonly TimeSpan CircuitSamplingDuration = TimeSpan.FromSeconds(10);
    public const int CircuitMinimumThroughput = 5;
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Applies the tuned values to the standard resilience options. Passed to
    /// <c>AddStandardResilienceHandler</c> on the typed Account Service client.
    /// </summary>
    public static void Configure(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = MaxRetryAttempts;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;

        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalRequestTimeout;

        options.CircuitBreaker.FailureRatio = CircuitFailureRatio;
        options.CircuitBreaker.SamplingDuration = CircuitSamplingDuration;
        options.CircuitBreaker.MinimumThroughput = CircuitMinimumThroughput;
        options.CircuitBreaker.BreakDuration = CircuitBreakDuration;
    }
}
