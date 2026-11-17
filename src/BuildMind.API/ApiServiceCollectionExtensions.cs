using BuildMind.API.Configuration;
using BuildMind.API.Errors;
using BuildMind.API.Security;
using BuildMind.Application.Abstractions;
using BuildMind.Infrastructure.Configuration;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace BuildMind.API;

/// <summary>API-layer shared infrastructure registration (BE-M4-D01c), called by the composition root.</summary>
public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddBuildMindApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // C3/C5: RFC 9457 Problem Details with BuildMind code, type and traceId conventions.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Apply);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // C10: the framework middleware's own exception diagnostics (which include the full exception) are
        // suppressed only when GlobalExceptionHandler handled the exception — it logs safe metadata itself.
        // Framework diagnostics stay on for handler failures and other unhandled paths (no blanket log filter).
        services.Configure<ExceptionHandlerOptions>(options =>
            options.SuppressDiagnosticsCallback = ShouldSuppressFrameworkDiagnostics);

        // Current user derived from the authenticated principal only.
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // C8: typed internal-service destinations, validated at start-up. Disabled services never block start-up.
        services.AddOptions<ServicesOptions>()
            .Bind(configuration.GetSection(ServicesOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServicesOptions>, ServicesOptionsValidation>();

        return services;
    }

    /// <summary>True only when a registered <c>IExceptionHandler</c> service handled the exception.</summary>
    public static bool ShouldSuppressFrameworkDiagnostics(ExceptionHandlerSuppressDiagnosticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ExceptionHandledBy == ExceptionHandledType.ExceptionHandlerService;
    }
}
