using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildMind.Infrastructure;

/// <summary>
/// Infrastructure-layer service registration (adapters implementing Application ports),
/// called once by the API composition root.
/// </summary>
/// <remarks>
/// Owned by M4. Intentionally registers nothing yet: persistence (BE-M4-D02), internal service
/// clients (BE-M4-D05) and storage adapters are added in later approved slices. Configuration is
/// accepted now so later adapters bind settings here rather than in the API project.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }
}
