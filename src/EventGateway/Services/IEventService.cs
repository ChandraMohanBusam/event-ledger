using EventGateway.Contracts;

namespace EventGateway.Services;

/// <summary>Outcome of submitting an event.</summary>
public enum SubmitStatus
{
    /// <summary>A new event was accepted, forwarded, and stored.</summary>
    Created,

    /// <summary>The eventId already existed; the original is returned unchanged.</summary>
    Duplicate,

    /// <summary>The Account Service could not be reached; nothing was stored.</summary>
    AccountServiceUnavailable,

    /// <summary>The Account Service rejected the transaction (unexpected after Gateway validation).</summary>
    AccountRejected
}

public sealed record SubmitResult(
    SubmitStatus Status,
    EventResponse? Event,
    string? Error = null);

/// <summary>
/// Orchestrates event submission and reads the Gateway's own event store.
/// The read methods never call the Account Service, which is what lets the GET
/// endpoints keep working during an Account Service outage.
/// </summary>
public interface IEventService
{
    Task<SubmitResult> SubmitEventAsync(
        SubmitEventRequest request,
        CancellationToken cancellationToken = default);

    Task<EventResponse?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventResponse>> GetEventsByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}
