using System.Net;
using EventGateway.Contracts;

namespace EventGateway.Clients;

/// <summary>
/// Result of an HTTP call to the Account Service. Distinguishes "the service
/// was reached and replied" from "the service could not be reached" (connection
/// failure, timeout, or open circuit), which is what the write path and the
/// balance proxy use to decide between a normal response and a 503.
/// </summary>
public sealed record AccountServiceResult<T>(
    bool ServiceReachable,
    HttpStatusCode? StatusCode,
    T? Body)
{
    public bool IsSuccess =>
        ServiceReachable && StatusCode is not null && (int)StatusCode < 400;

    /// <summary>
    /// True when the service could not be reached at all, or replied with a
    /// server error. Both mean the Account Service is effectively unavailable
    /// for this request.
    /// </summary>
    public bool IsUnavailable =>
        !ServiceReachable || (StatusCode is not null && (int)StatusCode >= 500);

    public bool IsNotFound =>
        ServiceReachable && StatusCode == HttpStatusCode.NotFound;

    public static AccountServiceResult<T> Unreachable() => new(false, null, default);
}

/// <summary>
/// Typed client for the Account Service. Registered with IHttpClientFactory and
/// wrapped in the standard resilience pipeline. Behind an interface so the
/// Gateway depends on the contract and tests can substitute it.
/// </summary>
public interface IAccountServiceClient
{
    Task<AccountServiceResult<TransactionDto>> ApplyTransactionAsync(
        string accountId,
        ApplyTransactionDto transaction,
        CancellationToken cancellationToken = default);

    Task<AccountServiceResult<BalanceDto>> GetBalanceAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}
