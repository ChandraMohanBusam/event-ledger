using System.ComponentModel;
using EventGateway.Clients;
using EventGateway.Contracts;
using EventGateway.Services;
using EventGateway.Validation;

namespace EventGateway.Endpoints;

/// <summary>
/// Maps the Event Gateway endpoints. The write path and the balance proxy
/// depend on the Account Service; the event read endpoints depend only on the
/// Gateway's own store and keep working when the Account Service is down.
/// </summary>
public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/events").WithTags("Events");

        events.MapPost("/", SubmitEvent)
            .WithName("SubmitEvent")
            .WithSummary("Submit a transaction event.")
            .Produces<EventResponse>(StatusCodes.Status201Created)
            .Produces<EventResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        events.MapGet("/{id}", GetEvent)
            .WithName("GetEvent")
            .WithSummary("Retrieve a single event by id.")
            .Produces<EventResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        events.MapGet("/", GetEventsByAccount)
            .WithName("GetEventsByAccount")
            .WithSummary("List events for an account, ordered by event timestamp.")
            .Produces<IReadOnlyList<EventResponse>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        // Balance proxy. Intentional extension beyond the literal Gateway table:
        // resolves the spec's requirement that balance queries degrade clearly
        // when the Account Service is unavailable.
        var accounts = app.MapGroup("/accounts").WithTags("Balance proxy");
        accounts.MapGet("/{accountId}/balance", GetBalance)
            .WithName("GetBalanceViaGateway")
            .WithSummary("Get an account balance by proxying to the Account Service.")
            .Produces<GatewayBalanceResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> SubmitEvent(
        SubmitEventRequest request,
        IEventService service,
        CancellationToken cancellationToken)
    {
        var errors = EventValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.SubmitEventAsync(request, cancellationToken);

        return result.Status switch
        {
            SubmitStatus.Created => Results.Created(
                $"/events/{result.Event!.EventId}", result.Event),

            SubmitStatus.Duplicate => Results.Ok(result.Event),

            SubmitStatus.AccountServiceUnavailable => Results.Problem(
                title: "Account Service unavailable",
                detail: result.Error,
                statusCode: StatusCodes.Status503ServiceUnavailable),

            SubmitStatus.AccountRejected => Results.Problem(
                title: "Transaction rejected",
                detail: result.Error,
                statusCode: StatusCodes.Status502BadGateway),

            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> GetEvent(
        [Description("The event identifier returned when the event was submitted.")] string id,
        IEventService service,
        CancellationToken cancellationToken)
    {
        var evt = await service.GetEventAsync(id, cancellationToken);
        return evt is null
            ? Results.Problem(
                title: "Event not found",
                detail: $"No event with id {id}.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(evt);
    }

    private static async Task<IResult> GetEventsByAccount(
        [Description("The account identifier to list events for, for example acct-123.")] string? account,
        IEventService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["account"] = ["The 'account' query parameter is required."]
            });
        }

        var events = await service.GetEventsByAccountAsync(account, cancellationToken);
        return Results.Ok(events);
    }

    private static async Task<IResult> GetBalance(
        [Description("The account identifier, for example acct-123.")] string accountId,
        IAccountServiceClient accountClient,
        CancellationToken cancellationToken)
    {
        var result = await accountClient.GetBalanceAsync(accountId, cancellationToken);

        if (result.IsUnavailable)
        {
            return Results.Problem(
                title: "Account Service unavailable",
                detail: "Balance cannot be retrieved because the Account Service is unreachable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (result.IsNotFound)
        {
            return Results.Problem(
                title: "Account not found",
                detail: $"No account with id {accountId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var balance = result.Body!;
        return Results.Ok(new GatewayBalanceResponse(
            balance.AccountId, balance.Balance, balance.Currency));
    }
}
