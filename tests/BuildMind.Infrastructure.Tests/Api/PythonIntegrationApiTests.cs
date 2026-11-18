using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BuildMind.API.Health;
using BuildMind.Infrastructure.Tests.Support;

namespace BuildMind.Infrastructure.Tests.Api;

/// <summary>
/// BE-M4-D05 through the real API host: Python readiness against the confirmed /health contract, dependency
/// failures through the public Problem Details contract, and no internal URLs or credentials in logs.
/// Python transport is a controlled stub; nothing leaves the process.
/// </summary>
public sealed class PythonIntegrationApiTests
{
    private const string PythonHost = "python-services.internal";

    private static readonly Dictionary<string, string?> PythonEnabled = new()
    {
        ["Services:Python:Enabled"] = "true",
        ["Services:Python:BaseUrl"] = $"http://{PythonHost}:8000",
        ["Services:Python:TimeoutSeconds"] = "1",
    };

    private static StubHttpHandler HealthyPython() => StubHttpHandler.Json("{\"service\":\"buildmind-python\",\"status\":\"ok\"}");

    [Theory]
    [InlineData("healthy", "Healthy", "reachable")]
    [InlineData("down", "Degraded", "unreachable")]
    [InlineData("error-500", "Degraded", "unreachable")]
    [InlineData("wrong-body", "Degraded", "unreachable")]
    [InlineData("hangs", "Degraded", "timeout")]
    public async Task Python_readiness_reflects_the_confirmed_health_contract(string scenario, string status, string reason)
    {
        var handler = scenario switch
        {
            "healthy" => HealthyPython(),
            "down" => StubHttpHandler.Throws(new HttpRequestException("synthetic")),
            "error-500" => StubHttpHandler.Status(HttpStatusCode.InternalServerError),
            "wrong-body" => StubHttpHandler.Json("{\"service\":\"something-else\",\"status\":\"ok\"}"),
            _ => StubHttpHandler.Hangs(),
        };
        await using var factory = new BuildMindApiFactory("Production", PythonEnabled);
        await using var host = factory.WithPythonTransport(handler);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(Readiness.Path);
        var python = Check(await response.Content.ReadAsStringAsync(), "python");

        Assert.Equal((status, reason), python);
        // MySQL is still not configured, so the instance is not ready whatever Python says.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_python_is_degraded_not_configured_and_never_contacted()
    {
        var handler = HealthyPython();
        await using var factory = new BuildMindApiFactory("Production");
        await using var host = factory.WithPythonTransport(handler);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(Readiness.Path);

        Assert.Equal(("Degraded", "not_configured"), Check(await response.Content.ReadAsStringAsync(), "python"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("down", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("hangs", HttpStatusCode.GatewayTimeout, "DEPENDENCY_TIMEOUT")]
    [InlineData("invalid-json", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("healthy", HttpStatusCode.NoContent, null)]
    public async Task Feature_calls_translate_dependency_failures_to_problem_details(string scenario, HttpStatusCode expected, string? code)
    {
        var handler = scenario switch
        {
            "down" => StubHttpHandler.Throws(new HttpRequestException("synthetic " + PythonHost)),
            "hangs" => StubHttpHandler.Hangs(),
            "invalid-json" => StubHttpHandler.Json("{ not json"),
            _ => HealthyPython(),
        };
        await using var factory = new BuildMindApiFactory("Production", PythonEnabled);
        await using var host = factory.WithPythonTransport(handler);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/problem+json"));

        using var response = await client.GetAsync(SyntheticTestEndpoints.Prefix + "/python-health");

        Assert.Equal(expected, response.StatusCode);
        if (code is not null)
        {
            var problem = await ProblemResponse.ReadAsync(response);
            Assert.Equal(code, problem.GetString("code"));
            Assert.DoesNotContain(PythonHost, problem.RawBody, StringComparison.Ordinal);
            Assert.DoesNotContain("8000", problem.RawBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", problem.RawBody, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Timeout", HttpStatusCode.GatewayTimeout, "DEPENDENCY_TIMEOUT")]
    [InlineData("Unavailable", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("Disabled", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("MalformedResponse", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("ResponseTooLarge", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("UnexpectedResponse", HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    public async Task Every_failure_kind_maps_to_the_shared_contract(string kind, HttpStatusCode expected, string code)
    {
        await using var factory = new BuildMindApiFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/problem+json"));

        using var response = await client.GetAsync(SyntheticTestEndpoints.Prefix + "/dependency-failure/" + kind);
        var problem = await ProblemResponse.ReadAsync(response);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, problem.GetString("code"));
        Assert.Contains(factory.Logs.Entries, e => e.Category == "BuildMind.API.Errors.GlobalExceptionHandler"
            && e.Properties.TryGetValue("FailureKind", out var logged) && logged == kind);
    }

    [Fact]
    public async Task Internal_urls_and_incoming_credentials_never_reach_logs_or_python()
    {
        var handler = StubHttpHandler.Throws(new HttpRequestException("synthetic " + PythonHost));
        await using var factory = new BuildMindApiFactory("Production", PythonEnabled);
        await using var host = factory.WithPythonTransport(handler);
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SyntheticTestEndpoints.Prefix + "/python-health");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SyntheticTestEndpoints.SyntheticSecret);
        request.Headers.TryAddWithoutValidation("Cookie", "session=" + SyntheticTestEndpoints.SyntheticSecret);

        using var response = await client.SendAsync(request);
        using var ready = await client.GetAsync(Readiness.Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.All(factory.Logs.Entries, e =>
        {
            Assert.DoesNotContain(PythonHost, e.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticTestEndpoints.SyntheticSecret, e.AllText, StringComparison.Ordinal);
        });
        Assert.All(handler.Requests, r =>
        {
            Assert.False(r.Headers.Contains("Authorization"));
            Assert.False(r.Headers.Contains("Cookie"));
        });
    }

    private static (string Status, string Reason) Check(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        var check = document.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        return (check.GetProperty("status").GetString()!, check.GetProperty("reason").GetString()!);
    }
}
