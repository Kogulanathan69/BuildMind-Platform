using BuildMind.API;
using BuildMind.API.Routing;
using BuildMind.Application;
using BuildMind.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root: the API project is the only place that wires layers together.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddBuildMindApiInfrastructure(builder.Configuration);

var app = builder.Build();

// C3: safe RFC 9457 responses for unhandled exceptions and for empty 4xx/5xx responses (e.g. unknown routes).
app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness only: reports that the API process is running. It does not check MySQL, Python or RAG.
// Dependency readiness (GET /health/ready) is planned for BE-M4-D04 and is deliberately not mapped yet.
app.MapGet("/health", () => Results.Ok(new { service = "buildmind-api", status = "ok" }));

// C1: public product API under /api/v1; feature modules register IApiEndpointModule implementations.
app.MapBuildMindApiV1();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program
{
}
