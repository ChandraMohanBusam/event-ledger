using AccountService.Contracts;
using AccountService.Services;
using AccountService.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AccountService.Endpoints;

/// <summary>
/// Maps the Account Service endpoints. Minimal APIs are used rather than MVC
/// controllers: lighter and a better fit for a small, focused service.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts").WithTags("Accounts");

        group.MapPost("/{accountId}/transactions", ApplyTransaction)
            .WithName("ApplyTransaction")
            .WithSummary("Apply a transaction to an account (idempotent by transactionId).");

        group.MapGet("/{accountId}/balance", GetBalance)
            .WithName("GetBalance")
            .WithSummary("Get the current balance for an account.");

        group.MapGet("/{accountId}", GetAccount)
            .WithName("GetAccount")
            .WithSummary("Get account details and recent transactions.");

        return app;
    }

    private static async Task<IResult> ApplyTransaction(
        string accountId,
        ApplyTransactionRequest request,
        ITransactionService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var errors = TransactionValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.ApplyTransactionAsync(accountId, request, cancellationToken);

        return result.Status switch
        {
            ApplyStatus.Created => Results.Created(
                $"/accounts/{accountId}/transactions/{result.Transaction!.TransactionId}",
                result.Transaction),

            ApplyStatus.Duplicate => Results.Ok(result.Transaction),

            ApplyStatus.CurrencyConflict => Results.Problem(
                title: "Currency conflict",
                detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),

            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<Results<Ok<BalanceResponse>, NotFound<string>>> GetBalance(
        string accountId,
        ITransactionService service,
        CancellationToken cancellationToken)
    {
        var balance = await service.GetBalanceAsync(accountId, cancellationToken);
        return balance is null
            ? TypedResults.NotFound($"Account {accountId} not found.")
            : TypedResults.Ok(balance);
    }

    private static async Task<Results<Ok<AccountDetailsResponse>, NotFound<string>>> GetAccount(
        string accountId,
        ITransactionService service,
        CancellationToken cancellationToken)
    {
        var details = await service.GetAccountAsync(accountId, cancellationToken: cancellationToken);
        return details is null
            ? TypedResults.NotFound($"Account {accountId} not found.")
            : TypedResults.Ok(details);
    }
}
