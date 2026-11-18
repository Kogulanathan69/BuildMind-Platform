using BuildMind.API.Configuration;
using BuildMind.API.Errors;
using BuildMind.API.Health;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        // BE-M4-D02a: database settings, validated at start-up only when Database:Enabled is true.
        // The connection string comes from ConnectionStrings:BuildMind (User Secrets / environment variables).
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidation>();

        // BE-M4-D04 readiness (C11b): MySQL mandatory; Python/RAG instance-optional (never Healthy before D05).
        services.AddSingleton<ReadinessProbeGate>();
        services.AddHealthChecks()
            .AddCheck<MySqlReadinessCheck>(
                "mysql", HealthStatus.Unhealthy, [Readiness.Tag], Readiness.RegistrationTimeout)
            .AddTypeActivatedCheck<InternalServiceReadinessCheck>(
                "python", HealthStatus.Degraded, [Readiness.Tag], Readiness.RegistrationTimeout, "python")
            .AddTypeActivatedCheck<InternalServiceReadinessCheck>(
                "rag", HealthStatus.Degraded, [Readiness.Tag], Readiness.RegistrationTimeout, "rag");

        // BE-M4-D04: do not advertise the server implementation. This only affects Kestrel; a reverse proxy
        // or load balancer in front of the API may add its own headers and must be configured separately.
        services.Configure<KestrelServerOptions>(options => options.AddServerHeader = false);

        return services;
    }

    /// <summary>True only when a registered <c>IExceptionHandler</c> service handled the exception.</summary>
    public static bool ShouldSuppressFrameworkDiagnostics(ExceptionHandlerSuppressDiagnosticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ExceptionHandledBy == ExceptionHandledType.ExceptionHandlerService;
    }
}
