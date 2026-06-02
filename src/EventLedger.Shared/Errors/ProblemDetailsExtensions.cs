using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventLedger.Shared.Errors;

/// <summary>
/// Configures the RFC 7807 ProblemDetails contract used for every error
/// response across both services. The current trace id is attached to each
/// problem so a client can quote it and an operator can find the matching logs
/// and trace.
/// </summary>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Registers ProblemDetails with a trace id enricher and a global exception
    /// handler that converts unhandled exceptions into a 500 ProblemDetails
    /// rather than leaking a stack trace.
    /// </summary>
    public static IServiceCollection AddLedgerProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var traceId = Activity.Current?.TraceId.ToString()
                              ?? context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["traceId"] = traceId;

                // A stable instance value helps correlate a problem to a request path.
                context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
            };
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }

    /// <summary>
    /// Adds the exception-handling middleware. Call early in the pipeline.
    /// </summary>
    public static WebApplication UseLedgerExceptionHandling(this WebApplication app)
    {
        app.UseExceptionHandler();
        return app;
    }
}

/// <summary>
/// Catches unhandled exceptions and returns a consistent 500 ProblemDetails.
/// Validation and other expected errors are handled at the endpoint and never
/// reach here; this is the safety net for the genuinely unexpected.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception while processing {Path}",
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7807",
            }
        });
    }
}
