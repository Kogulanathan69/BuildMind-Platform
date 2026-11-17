using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildMind.Infrastructure.Tests.Api;

/// <summary>
/// Readiness must be truthful. Dependency checks (MySQL, Python, RAG) are not implemented yet
/// (planned BE-M4-D04), so the API must not report itself ready.
/// </summary>
/// <remarks>
/// This deliberately asserts "not successful" rather than a specific status such as 404, so it does not
/// freeze the current absence of the endpoint as a contract. When BE-M4-D04 implements readiness, replace
/// this test with dependency-driven cases (ready only when dependencies are healthy; 503 when unavailable).
/// </remarks>
public sealed class ReadinessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadinessTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Readiness_is_not_reported_while_dependency_checks_are_unimplemented()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.False(
            response.IsSuccessStatusCode,
            $"GET /health/ready returned {(int)response.StatusCode} but no dependency readiness checks exist yet.");
    }

    [Fact]
    public async Task Liveness_success_is_independent_of_readiness()
    {
        using var client = _factory.CreateClient();

        using var live = await client.GetAsync("/health");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.NotEqual(live.StatusCode, ready.StatusCode);
    }
}
