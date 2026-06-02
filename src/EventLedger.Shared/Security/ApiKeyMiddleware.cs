using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventLedger.Shared.Security;

/// <summary>
/// Options for the simple header-based API key check. Bound from configuration
/// under "ApiKeyAuth". Disabled by default so the demo and a reviewer's first
/// run are not blocked. In production this gate is where a real JWT or OAuth2
/// scheme would sit; the shared secret here is a stand-in for the exercise.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeyAuth";

    /// <summary>When false (default), the middleware passes every request through.</summary>
    public bool Enabled { get; set; }

    /// <summary>Header carrying the key. Defaults to X-Api-Key.</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>The expected key value. Supplied from configuration, never hardcoded in source.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Rejects requests that do not present the configured API key, when enabled.
/// Health endpoints are always allowed so liveness checks work regardless.
/// </summary>
public static class ApiKeyMiddleware
{
    public static IServiceCollection AddApiKeyAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApiKeyOptions>(configuration.GetSection(ApiKeyOptions.SectionName));
        return services;
    }

    public static IApplicationBuilder UseApiKeyAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value;

        // If disabled or misconfigured, do not register the gate at all.
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            // Always allow health checks through.
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            if (!context.Request.Headers.TryGetValue(options.HeaderName, out var provided)
                || !string.Equals(provided, options.ApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Unauthorized",
                    detail = $"A valid {options.HeaderName} header is required.",
                    status = StatusCodes.Status401Unauthorized
                });
                return;
            }

            await next();
        });

        return app;
    }
}
