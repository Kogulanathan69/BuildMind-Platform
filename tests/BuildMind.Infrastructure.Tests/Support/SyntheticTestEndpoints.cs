using BuildMind.API.Errors;
using BuildMind.API.Routing;
using BuildMind.Application.Abstractions;
using BuildMind.Application.Abstractions.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BuildMind.Infrastructure.Tests.Support;

/// <summary>
/// TEST-ONLY endpoints, registered exclusively through <see cref="BuildMindApiFactory"/>. They exist to
/// exercise shared framework behaviour where no product route exists yet; they are never part of the API.
/// </summary>
internal sealed class SyntheticTestEndpoints : IApiEndpointModule
{
    public const string Prefix = ApiRouteExtensions.ApiV1Prefix + "/__synthetic-tests";

    /// <summary>Synthetic marker standing in for a secret. It is not a real credential.</summary>
    public const string SyntheticSecret = "SYNTHETIC-SECRET-7f3a9c41";

    public void MapEndpoints(RouteGroupBuilder apiV1)
    {
        var tests = apiV1.MapGroup("/__synthetic-tests");

        tests.MapGet("/throw", ThrowWithSecretMessage);
        tests.MapGet("/argument-exception", ThrowArgumentException);
        tests.MapGet("/server-cancelled", ThrowServerSideCancellation);
        tests.MapGet("/bad-request/{status:int}", ThrowBadHttpRequest);

        tests.MapGet("/throw-with-segment/{segment}", ThrowWithSegment);

        tests.MapGet("/status/{status:int}", (int status) => Results.StatusCode(status));

        // An endpoint that (wrongly) puts sensitive text into a 500 problem; the shared customizer must reduce it.
        tests.MapGet("/leaky-500/{codeKind}", (string codeKind) => TypedResults.Problem(
            detail: $"Database password is {SyntheticSecret}",
            statusCode: StatusCodes.Status500InternalServerError,
            title: $"Leaky title {SyntheticSecret}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = codeKind == "feature" ? "SYNTHETIC_FEATURE_FAILURE" : $"bad-code-{SyntheticSecret}",
                ["debug"] = SyntheticSecret,
                ["connectionString"] = $"Server=db;Password={SyntheticSecret}",
            }));

        tests.MapPost("/json", (SyntheticPayload payload) => Results.NoContent());

        tests.MapGet("/validation", () => ApiProblems.ValidationFailed(
            new Dictionary<string, string[]> { ["name"] = ["Name is required."] }));

        tests.MapGet("/domain-rule", () => ApiProblems.DomainRuleViolation(
            "SYNTHETIC_RULE_VIOLATED",
            "The synthetic rule was violated.",
            new Dictionary<string, object?> { ["diagnostics"] = new[] { "synthetic-diagnostic" } }));

        tests.MapGet("/require-user", (ICurrentUser currentUser) =>
            Results.Ok(new { userId = currentUser.GetRequiredUserId() }));

        tests.MapGet("/current-user", (ICurrentUser currentUser) => Results.Ok(new
        {
            isAuthenticated = currentUser.IsAuthenticated,
            userId = currentUser.UserId,
            roles = currentUser.Roles.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
        }));
    }

    private static IResult ThrowWithSecretMessage() =>
        throw new InvalidOperationException(
            $"Server=db.internal;Uid=root;Password={SyntheticSecret};Path=C:\\private\\keys");

    private static IResult ThrowWithSegment(string segment) =>
        throw new InvalidOperationException($"Failure for segment {segment}");

    private static IResult ThrowArgumentException() =>
        throw new ArgumentException($"Invalid internal argument {SyntheticSecret}");

    private static IResult ThrowServerSideCancellation() =>
        throw new OperationCanceledException($"Server-side cancellation {SyntheticSecret}");

    private static IResult ThrowBadHttpRequest(int status) =>
        throw new BadHttpRequestException($"Framework detail {SyntheticSecret}", status);

    internal sealed record SyntheticPayload(string Name, int Count);
}
