using BuildMind.Application;
using BuildMind.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root: the API project is the only place that wires layers together.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Liveness only: reports that the API process is running. It does not check MySQL, Python or RAG.
// Dependency readiness (GET /health/ready) is planned for BE-M4-D04 and is deliberately not mapped yet.
app.MapGet("/health", () => Results.Ok(new { service = "buildmind-api", status = "ok" }));

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program
{
}
