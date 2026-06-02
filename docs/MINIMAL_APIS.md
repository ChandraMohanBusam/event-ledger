# Minimal APIs: A Short Guide (for the walkthrough)

This project uses minimal APIs instead of MVC controllers. If your background
is controller-based ASP.NET, here is the mental model, a side-by-side
comparison, and exactly how this codebase is structured, so you can explain and
edit the endpoints confidently.

## The one-sentence mental model

A minimal API endpoint is just a route mapped to a function. Instead of a
controller class with attributed action methods, you call `app.MapGet(...)` or
`app.MapPost(...)` and hand it a function. Dependency injection, model binding,
and results work the same way underneath; there is simply less ceremony around
them.

## Side by side: the same endpoint, both styles

### MVC controller (what you know)

```csharp
[ApiController]
[Route("accounts")]
public class AccountsController : ControllerBase
{
    private readonly ITransactionService _service;

    public AccountsController(ITransactionService service)
    {
        _service = service;
    }

    [HttpGet("{accountId}/balance")]
    public async Task<IActionResult> GetBalance(string accountId)
    {
        var balance = await _service.GetBalanceAsync(accountId);
        return balance is null ? NotFound() : Ok(balance);
    }
}
```

### Minimal API (what this project uses)

```csharp
var group = app.MapGroup("/accounts");

group.MapGet("/{accountId}/balance", async (
    string accountId,
    ITransactionService service) =>
{
    var balance = await service.GetBalanceAsync(accountId);
    return balance is null ? Results.NotFound() : Results.Ok(balance);
});
```

Same route, same DI, same status codes. The controller's constructor injection
becomes a parameter on the function. `IActionResult` / `Ok()` / `NotFound()`
become `IResult` / `Results.Ok()` / `Results.NotFound()`.

## How the pieces map

| MVC concept                | Minimal API equivalent                         |
|----------------------------|------------------------------------------------|
| `[HttpGet("path")]`        | `app.MapGet("path", handler)`                  |
| `[Route]` on controller    | `app.MapGroup("/prefix")`                      |
| Constructor injection      | A parameter on the handler function            |
| `[FromBody] T model`       | A `T model` parameter (inferred from the body) |
| Route value `{id}`         | A parameter named `id`                         |
| `IActionResult`            | `IResult`                                       |
| `Ok(x)`, `NotFound()`      | `Results.Ok(x)`, `Results.NotFound()`          |
| `[ApiController]` validation | Explicit validation call in the handler      |

### Parameter binding rules (how the function knows what is what)

When you write a handler like
`async (string accountId, ApplyTransactionRequest request, ITransactionService service) => ...`,
the framework binds each parameter by these rules, in order:

1. If the name matches a route token (`{accountId}`), it comes from the route.
2. If the type is a registered service (`ITransactionService`), it comes from DI.
3. A complex type with no other source (`ApplyTransactionRequest`) is read from
   the request body as JSON.
4. Simple types not in the route are read from the query string.

This is why the handlers in this project do not need `[FromBody]` or
`[FromServices]` attributes; the framework infers them. You can add the
attributes explicitly if you ever want to be unambiguous.

## How this codebase is organised

Endpoints are not all dumped into `Program.cs`. Each service groups its
endpoints into an extension method, so `Program.cs` stays short and the routes
live in one readable place.

`src/AccountService/Endpoints/AccountEndpoints.cs`:

```csharp
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts").WithTags("Accounts");

        group.MapPost("/{accountId}/transactions", ApplyTransaction);
        group.MapGet("/{accountId}/balance", GetBalance);
        group.MapGet("/{accountId}", GetAccount);

        return app;
    }

    private static async Task<IResult> ApplyTransaction(
        string accountId,
        ApplyTransactionRequest request,
        ITransactionService service,
        CancellationToken cancellationToken)
    {
        var errors = TransactionValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);   // 400 with field errors
        }

        var result = await service.ApplyTransactionAsync(accountId, request, cancellationToken);
        return result.Status switch
        {
            ApplyStatus.Created          => Results.Created(/* location */, result.Transaction),
            ApplyStatus.Duplicate        => Results.Ok(result.Transaction),
            ApplyStatus.CurrencyConflict => Results.Problem(/* 400 */),
            _                            => Results.Problem(/* 500 */)
        };
    }
}
```

And `Program.cs` simply calls `app.MapAccountEndpoints();`. The grouping
extension is the minimal-API equivalent of having one controller per area.

## Adding a new endpoint (so you can do it live)

To add, for example, a "reverse a transaction" endpoint, you add one line to
the group and one handler function:

```csharp
group.MapPost("/{accountId}/reversals", ReverseTransaction);

private static async Task<IResult> ReverseTransaction(
    string accountId,
    ReverseRequest request,
    ITransactionService service)
{
    // ... call the service, return Results.Ok(...) or Results.NotFound()
}
```

No new class, no attributes. That is the whole change.

## Typed results (used in this project for the GET endpoints)

For some endpoints this project uses `TypedResults` instead of `Results`. They
behave identically at runtime, but the return type names every possible status,
which makes the OpenAPI document accurate automatically:

```csharp
private static async Task<Results<Ok<BalanceResponse>, NotFound<string>>> GetBalance(
    string accountId,
    ITransactionService service)
{
    var balance = await service.GetBalanceAsync(accountId);
    return balance is null
        ? TypedResults.NotFound($"Account {accountId} not found.")
        : TypedResults.Ok(balance);
}
```

The return type `Results<Ok<BalanceResponse>, NotFound<string>>` says "this
returns either a 200 with a BalanceResponse or a 404 with a string." That is a
nice touch to point out in the walkthrough: the type is the documentation.

## Why minimal APIs for this project (your walkthrough answer)

Keep it to three points:

1. Small, focused services. Each service has four endpoints. Minimal APIs avoid
   the controller machinery that only pays off at larger endpoint counts.
2. They are the modern .NET default. Since .NET 6 the templates lead with
   minimal APIs, and they are Microsoft's recommended starting point for
   microservices.
3. Readability. The route, validation, service call, and response mapping are
   visible in one short function, with no jumping between a controller, a base
   class, and attributes.

If asked "would you use controllers instead?": yes, for a large API with many
endpoints, shared filters, complex model binding, or a team standard that
favours them. Controllers and minimal APIs are both first-class in ASP.NET Core;
the choice is about fit, not capability.

## Things that are exactly the same as controllers

So you are not caught off guard: dependency injection, the service layer,
EF Core, validation logic, logging, middleware, authentication, and testing all
work identically. Minimal APIs change only how the endpoint itself is declared.
Everything behind the endpoint in this project (the service, the DbContext, the
validator) is plain classes you already know.
