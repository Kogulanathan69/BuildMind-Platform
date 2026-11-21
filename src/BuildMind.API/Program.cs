using BuildMind.API;
using BuildMind.API.Documentation;
using BuildMind.API.Health;
using BuildMind.API.Routing;
using BuildMind.API.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using BuildMind.Application;
using BuildMind.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root: the API project is the only place that wires layers together.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddBuildMindApiInfrastructure(builder.Configuration);

// C6 (proposed): exact-origin CORS allowlist from configuration. No origins configured => no CORS policy at all.
var corsEnabled = builder.Services.AddBuildMindCors(builder.Configuration, builder.Environment);

// C7 (proposed): OpenAPI document and Swagger UI, Development only. No bearer scheme until end-user auth exists.
builder.AddBuildMindDevelopmentSwagger(bearerAuthenticationEnabled: false);

// S4-B: rate limiting (global chained limiter + auth-strict policy + expensive-operation concurrency).
builder.Services.AddBuildMindRateLimiting(builder.Configuration);

var app = builder.Build();

// D04: safe response headers first, so they are also applied to error responses.
app.UseMiddleware<SecurityHeadersMiddleware>();

// C3: safe RFC 9457 responses for unhandled exceptions and for empty 4xx/5xx responses (e.g. unknown routes).
app.UseExceptionHandler();
app.UseStatusCodePages();

// Development only; a no-op in every other environment.
app.UseBuildMindDevelopmentSwagger();

// Order: routing -> CORS (answers preflight before any future authentication).
app.UseRouting();
if (corsEnabled)
{
    app.UseCors(CorsConfiguration.PolicyName);
}

// S4-B: the single rate-limiter middleware. After routing (endpoint policies and markers are visible) and after CORS
// (preflights are not counted); Swagger and the exception/status-code pages run earlier. When end-user authentication
// is integrated it goes here: UseAuthentication() BEFORE UseRateLimiter() (per-user partitions), and the per-request
// account-state check AFTER it, so floods are throttled before they reach the database. Feature modules must not
// call UseRateLimiter() themselves.
app.UseRateLimiter();

// Liveness only: reports that the API process is running. It does not check MySQL, Python or RAG.
app.MapGet("/health", () => Results.Ok(new { service = "buildmind-api", status = "ok" }));

// D04 readiness (C11b): MySQL mandatory (503 when not configured/unreachable); Python/RAG Degraded until D05.
app.MapHealthChecks(Readiness.Path, new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(Readiness.Tag),
    ResponseWriter = Readiness.WriteResponseAsync,
    ResultStatusCodes = new Dictionary<Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus, int>(Readiness.StatusCodes),
    AllowCachingResponses = false,
});

// C1: public product API under /api/v1; feature modules register IApiEndpointModule implementations.
app.MapBuildMindApiV1();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program
{
}
