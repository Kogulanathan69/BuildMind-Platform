using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildMind.API.Endpoints.PlanEditor;
using BuildMind.API.Errors;
using BuildMind.API.Security;
using BuildMind.Application.Abstractions;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Editing;
using BuildMind.Application.Features.PlanEditor.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildMind.Infrastructure.Tests.Api;

public sealed class PlanEditorEndpointContractTests
{
    private static readonly Guid ActorId = Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid ProjectId = Guid.Parse("71000000-0000-0000-0000-000000000002");
    private static readonly Guid PlanId = Guid.Parse("71000000-0000-0000-0000-000000000003");
    private static readonly Guid CandidateId = Guid.Parse("71000000-0000-0000-0000-000000000004");
    private static readonly Guid BaseRevisionId = Guid.Parse("71000000-0000-0000-0000-000000000005");
    private static readonly Guid CommandId = Guid.Parse("71000000-0000-0000-0000-000000000006");
    private static readonly Guid FloorId = Guid.Parse("71000000-0000-0000-0000-000000000007");
    private static readonly Guid LandRevisionId = Guid.Parse("71000000-0000-0000-0000-000000000010");
    private static readonly Guid RequirementsRevisionId = Guid.Parse("71000000-0000-0000-0000-000000000011");

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_before_M3_application_code()
    {
        var actions = new StubActionFacade();
        await using var app = await Host(actions);
        using var response = await app.GetTestClient().PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Fact]
    public async Task Non_persisting_validation_route_is_not_mapped()
    {
        var actions = new StubActionFacade();
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(
            ValidateRoute,
            new StringContent(
                "{\"diagnostics\":[{\"message\":\"PRIVATE_DIAGNOSTIC\"}]}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", body, StringComparison.Ordinal);
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Fact]
    public async Task Wrong_owner_apply_fails_closed_without_protected_payload()
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.Unauthorized),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal("NOT_FOUND", json["code"]!.GetValue<string>());
        Assert.Null(json["candidate"]);
        Assert.Null(json["revision"]);
        Assert.DoesNotContain("geometryHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_binds_exact_route_scope_and_preserves_valid_success()
    {
        var candidate = Candidate(CandidatePreviewStatus.Applied);
        var revision = Revision(candidate);
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.Applied, candidate, revision),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ApplyCandidateCommand(CandidateId, ProjectId, PlanId, true), actions.LastApply);
        Assert.Equal(CandidateId, json["candidateId"]!.GetValue<Guid>());
        Assert.Equal(revision.RevisionId, json["revision"]!["revisionId"]!.GetValue<Guid>());
        Assert.Null(json["ownerId"]);
        Assert.Null(json["validation"]);
        Assert.Null(json["landRevisionId"]);
        Assert.Null(json["geometry"]);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Fact]
    public async Task Contradictory_apply_success_is_rejected_without_foreign_payload()
    {
        var foreignProject = Guid.Parse("71000000-0000-0000-0000-000000000099");
        var foreign = Candidate(CandidatePreviewStatus.Applied) with { ProjectId = foreignProject };
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.Applied, foreign, Revision(foreign)),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(foreignProject.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("geometryHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Fact]
    public async Task Reject_binds_exact_route_scope_and_creates_no_revision_payload()
    {
        var candidate = Candidate(CandidatePreviewStatus.Rejected);
        var actions = new StubActionFacade
        {
            RejectResult = new CandidateActionResult(CandidateActionStatus.Rejected, candidate),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(RejectRoute, null);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new CandidateActionCommand(CandidateId, ProjectId, PlanId), actions.LastReject);
        Assert.Equal(CandidateId, json["candidateId"]!.GetValue<Guid>());
        Assert.Null(json["revision"]);
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Equal(1, actions.RejectCalls);
    }

    [Fact]
    public async Task Dependency_failure_has_safe_problem_details_and_no_payload()
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.DependencyUnavailable),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = false }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", json["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["traceId"]!.GetValue<string>()));
        Assert.DoesNotContain("geometryHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CandidateActionStatus.AlreadyApplied, CandidatePreviewStatus.Applied, "CANDIDATE_ALREADY_APPLIED")]
    [InlineData(CandidateActionStatus.AlreadyRejected, CandidatePreviewStatus.Rejected, "CANDIDATE_ALREADY_REJECTED")]
    [InlineData(CandidateActionStatus.Expired, CandidatePreviewStatus.Expired, "CANDIDATE_EXPIRED")]
    public async Task Terminal_results_preserve_classification_without_disclosing_candidate(
        CandidateActionStatus status,
        CandidatePreviewStatus candidateStatus,
        string expectedCode)
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(status, Candidate(candidateStatus)),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expectedCode, json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Theory]
    [InlineData(CandidateActionStatus.AlreadyApplied)]
    [InlineData(CandidateActionStatus.TransactionOutcomeUnknown)]
    [InlineData(CandidateActionStatus.GeometryTampered)]
    public async Task Failure_states_that_require_candidate_payload_reject_missing_payload(
        CandidateActionStatus status)
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(status),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(
            ApplyRoute,
            JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INVALID_DEPENDENCY_RESPONSE", json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Theory]
    [InlineData("apply", false)]
    [InlineData("apply", true)]
    [InlineData("reject", false)]
    [InlineData("reject", true)]
    public async Task Persistence_failure_accepts_contract_permitted_candidate_variants(
        string operation,
        bool includePendingCandidate)
    {
        var result = new CandidateActionResult(
            CandidateActionStatus.PersistenceFailure,
            includePendingCandidate ? Candidate(CandidatePreviewStatus.Pending) : null);
        var actions = new StubActionFacade
        {
            ApplyResult = result,
            RejectResult = result,
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = operation == "apply"
            ? await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }))
            : await client.PostAsync(RejectRoute, null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("TRANSACTION_FAILURE", json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(operation == "apply" ? 1 : 0, actions.ApplyCalls);
        Assert.Equal(operation == "reject" ? 1 : 0, actions.RejectCalls);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("reject")]
    public async Task Persistence_failure_rejects_foreign_candidate_without_disclosure(string operation)
    {
        var foreignCandidate = Candidate(CandidatePreviewStatus.Pending) with
        {
            ProjectId = Guid.Parse("71000000-0000-0000-0000-000000000099"),
        };
        var result = new CandidateActionResult(CandidateActionStatus.PersistenceFailure, foreignCandidate);
        var actions = new StubActionFacade
        {
            ApplyResult = result,
            RejectResult = result,
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = operation == "apply"
            ? await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }))
            : await client.PostAsync(RejectRoute, null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INVALID_DEPENDENCY_RESPONSE", json["code"]!.GetValue<string>());
        Assert.DoesNotContain(foreignCandidate.ProjectId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        AssertNoProtectedPayload(body);
        Assert.Equal(operation == "apply" ? 1 : 0, actions.ApplyCalls);
        Assert.Equal(operation == "reject" ? 1 : 0, actions.RejectCalls);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("reject")]
    public async Task Persistence_failure_rejects_unexpected_revision_without_disclosure(string operation)
    {
        var candidate = Candidate(CandidatePreviewStatus.Pending);
        var result = new CandidateActionResult(
            CandidateActionStatus.PersistenceFailure,
            candidate,
            Revision(candidate));
        var actions = new StubActionFacade
        {
            ApplyResult = result,
            RejectResult = result,
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = operation == "apply"
            ? await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }))
            : await client.PostAsync(RejectRoute, null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INVALID_DEPENDENCY_RESPONSE", json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(operation == "apply" ? 1 : 0, actions.ApplyCalls);
        Assert.Equal(operation == "reject" ? 1 : 0, actions.RejectCalls);
    }

    [Fact]
    public async Task Unknown_transaction_outcome_is_preserved_without_payload_or_retry()
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(
                CandidateActionStatus.TransactionOutcomeUnknown,
                Candidate(CandidatePreviewStatus.Pending)),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("TRANSACTION_OUTCOME_UNKNOWN", json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    [Theory]
    [InlineData(CandidateActionStatus.DependencyTimeout, HttpStatusCode.GatewayTimeout, "DEPENDENCY_TIMEOUT")]
    [InlineData(CandidateActionStatus.DependencyUnavailable, HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE")]
    public async Task Dependency_failures_with_internal_candidate_preserve_safe_classification(
        CandidateActionStatus status,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(status, Candidate(CandidatePreviewStatus.Pending)),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, json["code"]!.GetValue<string>());
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Fact]
    public async Task Same_project_foreign_owner_success_is_rejected_without_payload()
    {
        var foreignOwner = Guid.Parse("71000000-0000-0000-0000-000000000098");
        var foreign = Candidate(CandidatePreviewStatus.Applied) with { OwnerId = foreignOwner };
        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.Applied, foreign, Revision(foreign)),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(foreignOwner.ToString(), body, StringComparison.OrdinalIgnoreCase);
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("evidence")]
    [InlineData("revision")]
    public async Task Contradictory_protected_success_fields_are_rejected(string contradiction)
    {
        var candidate = Candidate(CandidatePreviewStatus.Applied);
        var revision = Revision(candidate);
        if (contradiction == "geometry")
            candidate = candidate with { GeometryHash = "sha256:foreign" };
        else if (contradiction == "evidence")
            candidate = candidate with { Validation = Validation("sha256:foreign") };
        else
            revision = revision with { GeometryHash = "sha256:foreign" };

        var actions = new StubActionFacade
        {
            ApplyResult = new CandidateActionResult(CandidateActionStatus.Applied, candidate, revision),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsync(ApplyRoute, JsonContent.Create(new { acknowledgedWarnings = true }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertNoProtectedPayload(body);
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("actor")]
    public async Task Invalid_apply_json_shapes_fail_before_action_facade(string mode)
    {
        var actions = new StubActionFacade();
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        var content = mode switch
        {
            "malformed" => "{",
            "duplicate" => "{\"acknowledgedWarnings\":true,\"acknowledgedWarnings\":false}",
            "unknown" => "{\"acknowledgedWarnings\":true,\"unexpected\":true}",
            _ => $"{{\"acknowledgedWarnings\":true,\"actorId\":\"{ActorId}\"}}",
        };

        using var response = await client.PostAsync(
            ApplyRoute,
            new StringContent(content, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Fact]
    public async Task Missing_apply_request_body_fails_before_action_facade()
    {
        var actions = new StubActionFacade();
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var applyResponse = await client.PostAsync(
            ApplyRoute,
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, applyResponse.StatusCode);
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Fact]
    public async Task Wrong_owner_reject_is_generic_and_payload_free()
    {
        var actions = new StubActionFacade
        {
            RejectResult = new CandidateActionResult(CandidateActionStatus.NotFound),
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);

        using var reject = await client.PostAsync(RejectRoute, null);
        var rejectBody = await reject.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, reject.StatusCode);
        AssertNoProtectedPayload(rejectBody);
        Assert.Equal(1, actions.RejectCalls);
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_facade_once_and_produces_no_response_payload()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new StubActionFacade
        {
            ApplyHandler = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CandidateActionResult(CandidateActionStatus.UnexpectedInternalFailure);
            },
        };
        await using var app = await Host(actions);
        using var client = AuthenticatedClient(app);
        using var cancellation = new CancellationTokenSource();
        var request = client.PostAsync(
            ApplyRoute,
            JsonContent.Create(new { acknowledgedWarnings = true }),
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(0, actions.RejectCalls);
    }

    private static string ValidateRoute => $"/api/v1/projects/{ProjectId}/plans/{PlanId}/edits/validate";
    private static string ApplyRoute => $"/api/v1/projects/{ProjectId}/plans/{PlanId}/candidates/{CandidateId}/apply";
    private static string RejectRoute => $"/api/v1/projects/{ProjectId}/plans/{PlanId}/candidates/{CandidateId}/reject";

    private static void AssertNoProtectedPayload(string body)
    {
        Assert.DoesNotContain("geometryHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("landRevisionId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requirementsRevisionId", body, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient AuthenticatedClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-M3-Test-Auth", "yes");
        return client;
    }

    private static async Task<WebApplication> Host(ITrustedCandidateActionFacade actions)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication("M3Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("M3Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Apply);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        builder.Services.AddSingleton(actions);

        var app = builder.Build();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        new PlanEditorEndpointModule().MapEndpoints(app.MapGroup("/api/v1"));
        await app.StartAsync();
        return app;
    }

    private static CandidatePreviewSnapshot Candidate(CandidatePreviewStatus status)
    {
        var geometryJson = JsonSerializer.Serialize(Geometry(), PlanEditorJson.StrictOptions);
        var geometryHash = PlanRevisionService.ComputeGeometryHash(geometryJson);
        return new CandidatePreviewSnapshot(
            CandidateId,
            ProjectId,
            PlanId,
            ActorId,
            BaseRevisionId,
            LandRevisionId,
            RequirementsRevisionId,
            CommandId,
            geometryJson,
            geometryHash,
            GeometrySchemaInfo.DraftVersion,
            Validation(geometryHash),
            DateTimeOffset.Parse("2026-11-21T00:00:00Z"),
            DateTimeOffset.Parse("2026-11-21T00:15:00Z"),
            status);
    }

    private static PlanGeometryDto Geometry() => new()
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = ProjectId,
        PlanId = PlanId,
        LandRevisionId = LandRevisionId,
        RequirementsRevisionId = RequirementsRevisionId,
        CoordinateSystem = new CoordinateSystemDto
        {
            Type = "localCartesian",
            Unit = "m",
            Origin = new CoordinatePointDto { X = 0, Y = 0 },
            XDirection = "right",
            YDirection = "up",
        },
        Floors =
        [
            new FloorDto
            {
                Id = FloorId,
                Level = 0,
                Rooms = [],
                Walls = [],
                Openings = [],
            },
        ],
    };

    private static PlanValidationReferenceDto Validation(string geometryHash) => new()
    {
        ValidationId = Guid.Parse("71000000-0000-0000-0000-000000000012"),
        ValidatorVersion = "synthetic-test",
        GeometryHash = geometryHash,
        GeometryValid = true,
        RequirementsMatch = true,
        RegulatoryEvidenceStatus = "VERIFIED",
        HasBlockingIssues = false,
        ValidatedAtUtc = DateTimeOffset.Parse("2026-11-21T00:00:00Z"),
    };

    private static PlanRevisionSnapshot Revision(CandidatePreviewSnapshot candidate) => new(
        Guid.Parse("71000000-0000-0000-0000-000000000013"),
        candidate.PlanId,
        candidate.ProjectId,
        candidate.BaseRevisionId,
        2,
        candidate.GeometrySchemaVersion,
        candidate.GeometryJson,
        candidate.GeometryHash,
        candidate.LandRevisionId,
        candidate.RequirementsRevisionId,
        candidate.Validation,
        candidate.CommandId,
        candidate.OwnerId,
        candidate.CreatedAtUtc,
        "Synthetic test revision");

    private sealed class StubActionFacade : ITrustedCandidateActionFacade
    {
        public CandidateActionResult ApplyResult { get; init; } =
            new(CandidateActionStatus.UnexpectedInternalFailure);
        public CandidateActionResult RejectResult { get; init; } =
            new(CandidateActionStatus.UnexpectedInternalFailure);
        public int ApplyCalls { get; private set; }
        public int RejectCalls { get; private set; }
        public ApplyCandidateCommand? LastApply { get; private set; }
        public CandidateActionCommand? LastReject { get; private set; }
        public Func<ApplyCandidateCommand, CancellationToken, Task<CandidateActionResult>>? ApplyHandler { get; init; }
        public Func<CandidateActionCommand, CancellationToken, Task<CandidateActionResult>>? RejectHandler { get; init; }

        public Task<CandidateActionResult> ApplyAsync(
            ApplyCandidateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastApply = command;
            return ApplyHandler is null
                ? Task.FromResult(ApplyResult)
                : ApplyHandler(command, cancellationToken);
        }

        public Task<CandidateActionResult> RejectAsync(
            CandidateActionCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectCalls++;
            LastReject = command;
            return RejectHandler is null
                ? Task.FromResult(RejectResult)
                : RejectHandler(command, cancellationToken);
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-M3-Test-Auth"] != "yes")
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, ActorId.ToString())],
                Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
