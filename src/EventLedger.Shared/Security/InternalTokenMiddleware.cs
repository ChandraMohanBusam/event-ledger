using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventLedger.Shared.Security;

/// <summary>
/// Options for the internal service-to-service shared secret. Bound from
/// configuration under "InternalAuth". Disabled by default. This models the
/// trust boundary "the Account Service only accepts calls from the Gateway"
/// without dragging in an identity provider; in production this would be mTLS
/// or a service mesh identity.
/// </summary>
public sealed class InternalTokenOptions
{
    public const string SectionName = "InternalAuth";

    public bool Enabled { get; set; }

    public string HeaderName { get; set; } = "X-Internal-Token";

    public string? Token { get; set; }
}

/// <summary>
/// Outbound: attaches the internal token header to every request the Gateway
/// makes to the Account Service, when enabled. Registered on the typed client.
/// </summary>
public sealed class InternalTokenHandler(IOptions<InternalTokenOptions> options)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (opts.Enabled && !string.IsNullOrWhiteSpace(opts.Token)
            && !request.Headers.Contains(opts.HeaderName))
        {
            request.Headers.Add(opts.HeaderName, opts.Token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Inbound: validates the internal token on the Account Service, when enabled.
/// Health checks are always allowed.
/// </summary>
public static class InternalTokenMiddleware
{
    public static IServiceCollection AddInternalTokenAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InternalTokenOptions>(
            configuration.GetSection(InternalTokenOptions.SectionName));
        return services;
    }

    public static IApplicationBuilder UseInternalTokenAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<InternalTokenOptions>>().Value;

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Token))
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            if (!context.Request.Headers.TryGetValue(options.HeaderName, out var provided)
                || !string.Equals(provided, options.Token, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Unauthorized",
                    detail = "This service accepts internal calls only.",
                    status = StatusCodes.Status401Unauthorized
                });
                return;
            }

            await next();
        });

        return app;
    }
}
