using BuildMind.Application.Abstractions;
using BuildMind.Application.Features.Auth.Abstractions;
using BuildMind.Application.Features.Auth.Services;
using BuildMind.Infrastructure.Persistence.Repositories.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildMind.Infrastructure.Identity;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddM1Authentication(
        this IServiceCollection services,
        JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.TryAddSingleton<JwtOptions>(options);

        services.TryAddSingleton<TimeProvider>(
            TimeProvider.System);

        services.TryAddSingleton<
            IPasswordHashService,
            PasswordHashService>();

        services.TryAddSingleton<
            IAccessTokenIssuer,
            JwtAccessTokenIssuer>();

        services.TryAddScoped<
            IUserRepository,
            UserRepository>();

        services.TryAddScoped<AuthenticationService>();

        return services;
    }
}