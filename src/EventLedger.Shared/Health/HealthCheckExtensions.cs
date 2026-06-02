using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventLedger.Shared.Health;

/// <summary>
/// Produces a JSON health response that includes the overall status, the
/// service name, and each individual check (such as database connectivity).
/// Shared so both services report health in the same shape.
/// </summary>
public static class HealthCheckExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Builds a response writer bound to a service name, for use as
    /// HealthCheckOptions.ResponseWriter.
    /// </summary>
    public static Func<HttpContext, HealthReport, Task> JsonWriter(string serviceName)
    {
        return async (context, report) =>
        {
            context.Response.ContentType = "application/json";

            var payload = new
            {
                status = report.Status.ToString(),
                service = serviceName,
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description
                })
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(payload, JsonOptions));
        };
    }
}
