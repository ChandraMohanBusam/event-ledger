using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace AccountService.Tests;

/// <summary>
/// Provides a real IMeterFactory for unit tests that need to construct a metrics
/// type directly (metrics depend on IMeterFactory rather than a static Meter).
/// </summary>
public sealed class IMeterFactoryAccessor
{
    public IMeterFactory Factory { get; } =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
}
