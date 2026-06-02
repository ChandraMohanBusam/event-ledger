using System.Text.Json;

namespace EventGateway.Contracts;

/// <summary>
/// Request body for POST /events. Fields are nullable so validation can tell
/// "missing" from "invalid". Metadata is an optional arbitrary JSON object.
/// </summary>
public sealed record SubmitEventRequest(
    string? EventId,
    string? AccountId,
    string? Type,
    decimal? Amount,
    string? Currency,
    DateTimeOffset? EventTimestamp,
    JsonElement? Metadata);

/// <summary>An event as stored and returned by the Gateway.</summary>
public sealed record EventResponse(
    string EventId,
    string AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp,
    JsonElement? Metadata,
    DateTimeOffset ReceivedAt);

/// <summary>Balance returned by the Gateway's proxy endpoint.</summary>
public sealed record GatewayBalanceResponse(
    string AccountId,
    decimal Balance,
    string Currency);

// --- DTOs exchanged with the Account Service over HTTP ---

/// <summary>Sent to the Account Service to apply a transaction.</summary>
public sealed record ApplyTransactionDto(
    string TransactionId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp);

/// <summary>Returned by the Account Service for an applied transaction.</summary>
public sealed record TransactionDto(
    string TransactionId,
    string AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp,
    DateTimeOffset CreatedAt);

/// <summary>Returned by the Account Service balance endpoint.</summary>
public sealed record BalanceDto(
    string AccountId,
    decimal Balance,
    string Currency);
