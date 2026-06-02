using System.Net.Http.Json;
using EventGateway.Contracts;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EventGateway.Clients;

/// <summary>
/// Calls the Account Service over HTTP. The HttpClient is configured by
/// IHttpClientFactory with the standard resilience pipeline (timeout, retry
/// with backoff, circuit breaker). When the pipeline ultimately fails (the
/// service is down, every attempt timed out, or the circuit is open) the
/// resulting exception is translated into an "unreachable" result so callers
/// can degrade gracefully rather than throwing a 500.
/// </summary>
public sealed class AccountServiceClient(HttpClient httpClient, ILogger<AccountServiceClient> logger)
    : IAccountServiceClient
{
    public async Task<AccountServiceResult<TransactionDto>> ApplyTransactionAsync(
        string accountId,
        ApplyTransactionDto transaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"/accounts/{accountId}/transactions", transaction, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadFromJsonAsync<TransactionDto>(cancellationToken);
                return new AccountServiceResult<TransactionDto>(true, response.StatusCode, body);
            }

            return new AccountServiceResult<TransactionDto>(true, response.StatusCode, default);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            logger.LogWarning(ex,
                "Account Service unreachable while applying transaction {TransactionId} for {AccountId}.",
                transaction.TransactionId, accountId);
            return AccountServiceResult<TransactionDto>.Unreachable();
        }
    }

    public async Task<AccountServiceResult<BalanceDto>> GetBalanceAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"/accounts/{accountId}/balance", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadFromJsonAsync<BalanceDto>(cancellationToken);
                return new AccountServiceResult<BalanceDto>(true, response.StatusCode, body);
            }

            return new AccountServiceResult<BalanceDto>(true, response.StatusCode, default);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            logger.LogWarning(ex,
                "Account Service unreachable while fetching balance for {AccountId}.", accountId);
            return AccountServiceResult<BalanceDto>.Unreachable();
        }
    }

    /// <summary>
    /// Exceptions that mean the Account Service could not be reached: a
    /// transport failure, a timeout from the resilience pipeline, or an open
    /// circuit. Anything else is genuinely unexpected and is allowed to bubble
    /// up to the global exception handler.
    /// </summary>
    private static bool IsUnreachable(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or TimeoutRejectedException
            or BrokenCircuitException;
}
