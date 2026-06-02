using System.Text.Json;
using EventGateway.Clients;
using EventGateway.Contracts;
using EventGateway.Data;
using EventGateway.Domain;
using EventGateway.Validation;
using Microsoft.EntityFrameworkCore;

namespace EventGateway.Services;

/// <summary>
/// Submits events and reads the local event store.
///
/// Write ordering (POST /events): check local idempotency, then forward the
/// transaction to the Account Service, and only persist the event locally if
/// the forward succeeded. An event therefore exists in the Gateway only if its
/// transaction was applied, which keeps the two stores consistent. If the
/// Account Service is unreachable, nothing is stored and the caller gets a 503;
/// the same eventId can be retried safely because both services are idempotent.
/// </summary>
public sealed class EventService(
    EventDbContext db,
    IAccountServiceClient accountClient,
    ILogger<EventService> logger) : IEventService
{
    public async Task<SubmitResult> SubmitEventAsync(
        SubmitEventRequest request,
        CancellationToken cancellationToken = default)
    {
        var eventId = request.EventId!;

        // Idempotency: a known eventId was already forwarded and stored, so we
        // return the original without calling the Account Service again.
        var existing = await db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation("Duplicate event {EventId}; returning original.", eventId);
            return new SubmitResult(SubmitStatus.Duplicate, ToResponse(existing));
        }

        EventValidator.TryParseType(request.Type, out var type);

        // Forward to the Account Service first. The transaction id equals the
        // event id, so the Account Service is idempotent on the same key.
        var transaction = new ApplyTransactionDto(
            eventId, request.Type!, request.Amount!.Value,
            request.Currency!, request.EventTimestamp!.Value);

        var callResult = await accountClient.ApplyTransactionAsync(
            request.AccountId!, transaction, cancellationToken);

        if (callResult.IsUnavailable)
        {
            logger.LogWarning(
                "Account Service unavailable; not storing event {EventId}.", eventId);
            return new SubmitResult(
                SubmitStatus.AccountServiceUnavailable,
                Event: null,
                Error: "The Account Service is currently unavailable. Please retry.");
        }

        if (!callResult.IsSuccess)
        {
            return new SubmitResult(
                SubmitStatus.AccountRejected,
                Event: null,
                Error: $"The Account Service rejected the transaction (status {(int?)callResult.StatusCode}).");
        }

        // The transaction was applied. Persist the event locally.
        var entity = new Event
        {
            EventId = eventId,
            AccountId = request.AccountId!,
            Type = type,
            Amount = request.Amount!.Value,
            Currency = request.Currency!.ToUpperInvariant(),
            EventTimestamp = request.EventTimestamp!.Value,
            Metadata = request.Metadata?.GetRawText(),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        db.Events.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent duplicate submission. The transaction was idempotent on
            // the Account Service side, so this is a duplicate, not an error.
            db.ChangeTracker.Clear();
            var raced = await db.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
            if (raced is not null)
            {
                return new SubmitResult(SubmitStatus.Duplicate, ToResponse(raced));
            }
            throw;
        }

        logger.LogInformation(
            "Stored event {EventId} for account {AccountId}.", eventId, entity.AccountId);
        return new SubmitResult(SubmitStatus.Created, ToResponse(entity));
    }

    public async Task<EventResponse?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
        return entity is null ? null : ToResponse(entity);
    }

    public async Task<IReadOnlyList<EventResponse>> GetEventsByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        // Fetch the account's events, then order by event timestamp in memory.
        // SQLite stores DateTimeOffset as text and cannot ORDER BY it in SQL,
        // so the ordering is done client-side (the same approach used for the
        // decimal balance aggregation). Listing by event timestamp, not arrival
        // order, is what makes out-of-order delivery list chronologically.
        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.AccountId == accountId)
            .ToListAsync(cancellationToken);

        return events
            .OrderBy(e => e.EventTimestamp)
            .Select(ToResponse)
            .ToList();
    }

    private static EventResponse ToResponse(Event e) => new(
        e.EventId,
        e.AccountId,
        e.Type.ToString().ToUpperInvariant(),
        e.Amount,
        e.Currency,
        e.EventTimestamp,
        ParseMetadata(e.Metadata),
        e.ReceivedAt);

    private static JsonElement? ParseMetadata(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
