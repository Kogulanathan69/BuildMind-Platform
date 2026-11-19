using System.Text.Json;
using System.Text.Json.Nodes;
using BuildMind.API;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Application.Abstractions.Regulations;
using BuildMind.Application.Features.Generation.Contracts;
using BuildMind.Infrastructure.Configuration;
using BuildMind.Infrastructure.Integration;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace BuildMind.Persistence.IntegrationTests;

/// <summary>
/// LIVE cross-service acceptance (M4/M6/M7), SYNTHETIC evidence only. The CI job starts the test-only acceptance
/// service (<c>rag-service/tests/acceptance_app.py</c>): the REAL rag-service FastAPI app with real internal auth and
/// strict parsing, one mount per shared scenario. Each pack crosses a real loopback socket from Python into the
/// production <see cref="RagServiceClient"/>, which recomputes the BM-JCS-1 digest from the received bytes; the
/// result then goes through <see cref="ConstraintEvidenceGate"/> and M6's own contract validation. The tests FAIL
/// (never skip) when the service is not configured.
/// </summary>
public sealed class RagConstraintAcceptanceTests(ITestOutputHelper output)
{
    public const string UrlVariable = "BUILDMIND_TEST_RAG_ACCEPTANCE_URL";
    public const string TokenVariable = "BUILDMIND_TEST_RAG_ACCEPTANCE_TOKEN";

    private static readonly RegulatoryConstraintKind[] Setbacks =
        [RegulatoryConstraintKind.FrontSetbackMin, RegulatoryConstraintKind.RearSpaceMin, RegulatoryConstraintKind.SideSpaceMin];

    private static readonly JsonArray Scenarios = JsonNode.Parse(File.ReadAllText(Path.Combine(
        RepositoryRoot(), "rag-service", "contract", "constraints_scenarios.v0.json")))!["scenarios"]!.AsArray();

    public static TheoryData<string> ScenarioNames => [.. Scenarios.Select(s => s!["name"]!.GetValue<string>())];

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BuildMind.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (BuildMind.sln) not found.");
    }

    private static string Require(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} is not set: the acceptance service must be started by the CI job.");

    private static JsonNode Scenario(string name) => Scenarios.Single(s => s!["name"]!.GetValue<string>() == name)!;

    private static RagServiceClient Client(string scenario, string? token, bool allowSynthetic = true) =>
        new(new HttpClient(ApiServiceCollectionExtensions.CreateInternalServiceHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new ServicesOptions
            {
                Rag = new InternalServiceOptions
                {
                    Enabled = true,
                    BaseUrl = $"{Require(UrlVariable).TrimEnd('/')}/scenario/{scenario}/",
                    TimeoutSeconds = 10,
                    AuthToken = token,
                    AllowSyntheticConstraints = allowSynthetic,
                },
            }),
            new InternalServiceBulkhead());

    private static RegulatoryConstraintQuery Query(JsonNode request, long? projectRevision = null) => new(
        request["jurisdiction"]!.GetValue<string>(),
        [.. request["kinds"]!.AsArray().Select(k => k!.GetValue<string>() switch
        {
            "FRONT_SETBACK_MIN" => RegulatoryConstraintKind.FrontSetbackMin,
            "REAR_SPACE_MIN" => RegulatoryConstraintKind.RearSpaceMin,
            "SIDE_SPACE_MIN" => RegulatoryConstraintKind.SideSpaceMin,
            var other => throw new InvalidOperationException(other),
        })],
        new ConstraintBinding(
            Guid.Parse(request["binding"]!["project_id"]!.GetValue<string>()),
            projectRevision ?? request["binding"]!["project_revision"]!.GetValue<long>(),
            Guid.Parse(request["binding"]!["land_revision_id"]!.GetValue<string>()),
            Guid.Parse(request["binding"]!["requirement_revision_id"]!.GetValue<string>())),
        ResidentialOnly: request["building_use"]?.GetValue<string>() == "residential");

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task AC01_live_pack_is_verified_in_csharp_and_never_falsely_feasible(string name)
    {
        var scenario = Scenario(name);
        var expected = scenario["expected"]!;

        // The client recomputes the digest from the live bytes; a mismatch would throw MalformedResponse.
        var pack = await Client(name, Require(TokenVariable)).GetConstraintsAsync(Query(scenario["request"]!), CancellationToken.None);
        var assessment = ConstraintEvidenceGate.Assess(pack, Setbacks, allowSynthetic: true);
        output.WriteLine($"{name}: pack={pack.EvidencePackId[..12]} m6={assessment.RegulatoryEvidenceStatus} gaps={string.Join(',', assessment.Gaps)}");

        Assert.Equal(scenario["response"]!["evidence_pack_id"]!.GetValue<string>(), pack.EvidencePackId);
        Assert.Equal(expected["m6_regulatory_evidence_status"]!.GetValue<string>(), assessment.RegulatoryEvidenceStatus);
        Assert.Equal(expected["feasible_permitted"]!.GetValue<bool>(), assessment.PermitsFeasible);

        var feasible = JsonSerializer.Deserialize<FeasibilityResultDto>(FeasibleJson(assessment), GenerationContractJson.StrictOptions)!;
        if (assessment.PermitsFeasible)
        {
            GenerationContractValidation.Validate(feasible);
        }
        else
        {
            Assert.Throws<GenerationContractException>(() => GenerationContractValidation.Validate(feasible));
        }
    }

    [Fact]
    public async Task AC02_live_synthetic_evidence_is_rejected_without_the_isolated_opt_in()
    {
        var scenario = Scenario("all-setbacks-verified");

        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client("all-setbacks-verified", Require(TokenVariable), allowSynthetic: false).GetConstraintsAsync(Query(scenario["request"]!), CancellationToken.None));

        Assert.Equal(DependencyFailureKind.MalformedResponse, failure.Kind);
        var gated = ConstraintEvidenceGate.Assess(
            await Client("all-setbacks-verified", Require(TokenVariable)).GetConstraintsAsync(Query(scenario["request"]!), CancellationToken.None),
            Setbacks); // gate default: synthetic never counts as reviewed
        Assert.False(gated.PermitsFeasible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-wrong-token-0123456789abcdef0123")]
    public async Task AC03_live_unauthenticated_calls_never_receive_evidence(string? token)
    {
        var scenario = Scenario("all-setbacks-verified");

        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client("all-setbacks-verified", token).GetConstraintsAsync(Query(scenario["request"]!), CancellationToken.None));

        Assert.Equal(401, failure.StatusCode);
    }

    [Fact]
    public async Task AC04_live_int64_project_revision_is_bound_and_digest_verified()
    {
        var scenario = Scenario("all-setbacks-verified");
        const long Revision = 3_000_000_000; // beyond Int32 (Project.Revision is Int64)

        var pack = await Client("all-setbacks-verified", Require(TokenVariable))
            .GetConstraintsAsync(Query(scenario["request"]!, Revision), CancellationToken.None);

        Assert.Equal(Revision, pack.Binding.ProjectRevision);
        Assert.NotEqual(scenario["response"]!["evidence_pack_id"]!.GetValue<string>(), pack.EvidencePackId); // bound to the revision
    }

    private static string FeasibleJson(ConstraintEvidenceAssessment assessment)
    {
        const string Land = "{\"schemaVersion\":\"metric-land/0.1-draft\",\"coordinateSystem\":{\"type\":\"localCartesian\",\"unit\":\"m\",\"origin\":{\"x\":0,\"y\":0},\"xDirection\":\"right\",\"yDirection\":\"up\"},\"boundary\":{\"exterior\":[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":8},{\"x\":0,\"y\":8},{\"x\":0,\"y\":0}],\"holes\":[]}}";
        var version = assessment.RuleSetVersion is null ? "null" : JsonSerializer.Serialize(assessment.RuleSetVersion);
        return "{\"projectId\":\"11111111-1111-4111-8111-111111111111\",\"landRevisionId\":\"22222222-2222-4222-8222-222222222222\","
            + "\"requirementsRevisionId\":\"33333333-3333-4333-8333-333333333333\",\"schemaVersion\":\"m6-feasibility-result/0.1-draft\","
            + $"\"status\":\"FEASIBLE\",\"buildableFootprint\":{Land},\"landAreaM2\":120,\"buildableAreaM2\":80,\"requiredAreaM2\":60,"
            + $"\"regulatoryEvidenceStatus\":\"{assessment.RegulatoryEvidenceStatus}\",\"ruleSetVersion\":{version},"
            + "\"feasibilityPolicyVersion\":\"synthetic-policy-v1\",\"diagnostics\":[]}";
    }
}
