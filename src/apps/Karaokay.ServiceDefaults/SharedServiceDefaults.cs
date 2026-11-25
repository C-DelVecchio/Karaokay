using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Karaokay.ServiceDefaults;

/// <summary>
/// Shared self-host wiring: health checks today, telemetry/resilience as later slices need them.
/// Deliberately tiny and dependency-free beyond ASP.NET Core so every host can reference it.
/// </summary>
public static class SharedServiceDefaults
{
    /// <summary>Adds the Karaokay service defaults to a host application builder.</summary>
    public static IHostApplicationBuilder AddKaraokayServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks();
        return builder;
    }
}
