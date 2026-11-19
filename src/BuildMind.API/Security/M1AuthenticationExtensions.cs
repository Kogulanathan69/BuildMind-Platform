using System.Threading.RateLimiting;
using BuildMind.API.Routing;
using BuildMind.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;

namespace BuildMind.API.Security;

public static class M1AuthenticationExtensions
{
    public static bool AddM1AuthenticationApi(
        this WebApplicationBuilder builder)
    {
        var enabled = builder.Configuration
            .GetValue<bool>("Authentication:Enabled");

        if (!enabled)
            return false;

        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Authentication activation is not approved.");
        }

        if (!builder.Configuration.GetValue<bool>(
            "Database:Enabled"))
        {
            throw new InvalidOperationException(
                "Authentication requires database access.");
        }

        var options = builder.Configuration
            .GetSection(JwtOptions.SectionName)
            .Get<JwtOptions>()
            ?? throw new InvalidOperationException(
                "JWT configuration is missing.");

        options.Validate();

        builder.Services.AddM1Authentication(options);

        builder.Services
            .AddAuthentication(
                JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.SaveToken = false;
                jwt.IncludeErrorDetails = false;

                jwt.TokenValidationParameters =
                    JwtValidationParametersFactory.Create(options);

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!JwtSubjectValidator.IsValid(
                            context.Principal))
                        {
                            context.Fail(
                                "Invalid authentication identity.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode =
                StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy("M1Authentication", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection
                        .RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
        });

        builder.Services.AddSingleton<
            IApiEndpointModule,
            AuthenticationEndpointModule>();

        return true;
    }
}