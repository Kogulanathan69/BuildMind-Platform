using System.Text.Json;
using System.Text.Json.Serialization;
using BuildMind.API.Errors;
using BuildMind.API.Routing;
using BuildMind.Application.Abstractions;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Diagnostics;
using BuildMind.Application.Features.PlanEditor.Editing;
using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.API.Endpoints.PlanEditor;

/// <summary>
/// Isolated M3 HTTP contract for explicit candidate actions.
/// This module is deliberately absent from production DI until authentication and M4 persistence
/// adapters are approved and available.
/// The documented validation-only route is intentionally not mapped because the available application
/// orchestration persists candidates and therefore cannot satisfy its no-persistence contract.
/// </summary>
public sealed class PlanEditorEndpointModule : IApiEndpointModule
{
    private static readonly JsonSerializerOptions StrictJson = new(PlanEditorJson.StrictOptions)
    {
        AllowDuplicateProperties = false,
    };

    public void MapEndpoints(RouteGroupBuilder apiV1)
    {
        ArgumentNullException.ThrowIfNull(apiV1);

        var plans = apiV1
            .MapGroup("/projects/{projectId}/plans/{planId}")
            .WithTags("Plan Editor")
            .RequireAuthorization();

        plans.MapPost("/candidates/{candidateId}/apply", ApplyCandidateAsync)
            .WithName("ApplyPlanEditCandidate");
        plans.MapPost("/candidates/{candidateId}/reject", RejectCandidateAsync)
            .WithName("RejectPlanEditCandidate");
    }

    private static async Task<IResult> ApplyCandidateAsync(
        string projectId,
        string planId,
        string candidateId,
        HttpContext context,
        ICurrentUser currentUser,
        ITrustedCandidateActionFacade facade,
        CancellationToken cancellationToken)
    {
        NoStore(context);
        if (!TryReadScope(projectId, planId, out var project, out var plan, out var scopeFailure))
            return scopeFailure!;
        if (!TryReadIdentity(candidateId, "candidateId", out var candidate, out var candidateFailure))
            return candidateFailure!;
        if (!TryReadActor(currentUser, out var actor, out var actorFailure))
            return actorFailure!;
        if (!context.Request.HasJsonContentType())
            return Problem(StatusCodes.Status415UnsupportedMediaType, SharedErrorCodes.UnsupportedMediaType);

        ApplyCandidateHttpRequest? request;
        try
        {
            request = await context.Request
                .ReadFromJsonAsync<ApplyCandidateHttpRequest>(StrictJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return InvalidRequest("request", "A complete valid apply request is required.");
        }
        catch (NotSupportedException)
        {
            return InvalidRequest("request", "A supported JSON apply request is required.");
        }

        if (request is null)
            return InvalidRequest("request", "A complete apply request is required.");

        var result = await facade.ApplyAsync(
            new ApplyCandidateCommand(candidate, project, plan, request.AcknowledgedWarnings),
            cancellationToken).ConfigureAwait(false);

        return MapActionResult(result, actor, project, plan, candidate, CandidateActionStatus.Applied);
    }

    private static async Task<IResult> RejectCandidateAsync(
        string projectId,
        string planId,
        string candidateId,
        HttpContext context,
        ICurrentUser currentUser,
        ITrustedCandidateActionFacade facade,
        CancellationToken cancellationToken)
    {
        NoStore(context);
        if (!TryReadScope(projectId, planId, out var project, out var plan, out var scopeFailure))
            return scopeFailure!;
        if (!TryReadIdentity(candidateId, "candidateId", out var candidate, out var candidateFailure))
            return candidateFailure!;
        if (!TryReadActor(currentUser, out var actor, out var actorFailure))
            return actorFailure!;

        var result = await facade.RejectAsync(
            new CandidateActionCommand(candidate, project, plan),
            cancellationToken).ConfigureAwait(false);

        return MapActionResult(result, actor, project, plan, candidate, CandidateActionStatus.Rejected);
    }

    private static IResult MapActionResult(
        CandidateActionResult? result,
        Guid expectedActorId,
        Guid expectedProjectId,
        Guid expectedPlanId,
        Guid expectedCandidateId,
        CandidateActionStatus expectedSuccess)
    {
        if (result is null || !Enum.IsDefined(result.Status))
            return InvalidDependency();

        if (result.Status == expectedSuccess)
        {
            var expectedCandidateStatus = expectedSuccess == CandidateActionStatus.Applied
                ? CandidatePreviewStatus.Applied
                : CandidatePreviewStatus.Rejected;
            var validCandidate = TryValidateCandidate(
                result.Candidate,
                expectedActorId,
                expectedProjectId,
                expectedPlanId,
                expectedCandidateId,
                expectedBaseRevisionId: null,
                expectedCommandId: null,
                expectedCandidateStatus,
                out _);
            var candidate = result.Candidate;

            var validRevision = expectedSuccess == CandidateActionStatus.Applied
                ? validCandidate && RevisionMatchesCandidate(result.Revision, candidate!, expectedActorId)
                : result.Revision is null;

            if (!validCandidate || !validRevision)
                return InvalidDependency();

            return Results.Ok(ToActionResponse(candidate!, result.Revision));
        }

        if (result.Status is CandidateActionStatus.Applied or CandidateActionStatus.Rejected
            || result.Revision is not null)
            return InvalidDependency();

        if (result.Status is CandidateActionStatus.Unauthorized or CandidateActionStatus.NotFound
            or CandidateActionStatus.WrongProjectOrPlan)
        {
            if (result.Candidate is not null)
                return InvalidDependency();
            return Problem(StatusCodes.Status404NotFound, SharedErrorCodes.NotFound);
        }

        if (FailureRequiresCandidate(result.Status) && result.Candidate is null)
            return InvalidDependency();

        if (result.Candidate is not null)
        {
            var expectedFailureState = result.Status switch
            {
                CandidateActionStatus.AlreadyApplied => CandidatePreviewStatus.Applied,
                CandidateActionStatus.AlreadyRejected => CandidatePreviewStatus.Rejected,
                CandidateActionStatus.Expired => CandidatePreviewStatus.Expired,
                _ => CandidatePreviewStatus.Pending,
            };
            if (!CandidateScopeMatches(
                    result.Candidate,
                    expectedActorId,
                    expectedProjectId,
                    expectedPlanId,
                    expectedCandidateId,
                    expectedFailureState))
                return InvalidDependency();
        }

        var diagnostic = PlanEditorFailureMapper.FromCandidateAction(result.Status);
        return Problem(StatusFor(result.Status), diagnostic.Code, diagnostic.SafeMessage);
    }

    private static bool FailureRequiresCandidate(CandidateActionStatus status) => status is
        CandidateActionStatus.AlreadyApplied or
        CandidateActionStatus.AlreadyRejected or
        CandidateActionStatus.Expired or
        CandidateActionStatus.StaleBaseRevision or
        CandidateActionStatus.StaleInputs or
        CandidateActionStatus.GeometryTampered or
        CandidateActionStatus.ValidationUnavailable or
        CandidateActionStatus.ValidationRejected or
        CandidateActionStatus.BlockingIssues or
        CandidateActionStatus.WarningAcknowledgementRequired or
        CandidateActionStatus.TransactionOutcomeUnknown;

    private static bool TryValidateCandidate(
        CandidatePreviewSnapshot? candidate,
        Guid expectedActorId,
        Guid expectedProjectId,
        Guid expectedPlanId,
        Guid? expectedCandidateId,
        Guid? expectedBaseRevisionId,
        Guid? expectedCommandId,
        CandidatePreviewStatus expectedStatus,
        out PlanGeometryDto? geometry)
    {
        geometry = null;
        if (candidate is null
            || !CandidateScopeMatches(
                candidate,
                expectedActorId,
                expectedProjectId,
                expectedPlanId,
                expectedCandidateId,
                expectedStatus)
            || expectedBaseRevisionId is { } baseRevisionId && candidate.BaseRevisionId != baseRevisionId
            || expectedCommandId is { } commandId && candidate.CommandId != commandId
            || candidate.BaseRevisionId == Guid.Empty
            || candidate.LandRevisionId == Guid.Empty
            || candidate.RequirementsRevisionId == Guid.Empty
            || candidate.CommandId == Guid.Empty
            || string.IsNullOrWhiteSpace(candidate.GeometryJson)
            || string.IsNullOrWhiteSpace(candidate.GeometryHash)
            || string.IsNullOrWhiteSpace(candidate.GeometrySchemaVersion)
            || candidate.Validation is null
            || candidate.Validation.ValidationId == Guid.Empty
            || string.IsNullOrWhiteSpace(candidate.Validation.ValidatorVersion)
            || string.IsNullOrWhiteSpace(candidate.Validation.RegulatoryEvidenceStatus)
            || !candidate.Validation.GeometryValid
            || candidate.Validation.HasBlockingIssues
            || !string.Equals(candidate.Validation.GeometryHash, candidate.GeometryHash, StringComparison.Ordinal)
            || !string.Equals(
                PlanRevisionService.ComputeGeometryHash(candidate.GeometryJson),
                candidate.GeometryHash,
                StringComparison.Ordinal)
            || candidate.CreatedAtUtc >= candidate.ExpiresAtUtc)
            return false;

        try
        {
            geometry = JsonSerializer.Deserialize<PlanGeometryDto>(candidate.GeometryJson, StrictJson);
            if (geometry is null)
                return false;
            PlanEditorContractValidator.Validate(geometry);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            geometry = null;
            return false;
        }

        return geometry.ProjectId == candidate.ProjectId
            && geometry.PlanId == candidate.PlanId
            && geometry.LandRevisionId == candidate.LandRevisionId
            && geometry.RequirementsRevisionId == candidate.RequirementsRevisionId
            && string.Equals(geometry.SchemaVersion, candidate.GeometrySchemaVersion, StringComparison.Ordinal);
    }

    private static bool CandidateScopeMatches(
        CandidatePreviewSnapshot candidate,
        Guid expectedActorId,
        Guid expectedProjectId,
        Guid expectedPlanId,
        Guid? expectedCandidateId,
        CandidatePreviewStatus expectedStatus) =>
        candidate.OwnerId == expectedActorId
        && candidate.ProjectId == expectedProjectId
        && candidate.PlanId == expectedPlanId
        && (expectedCandidateId is null || candidate.CandidateId == expectedCandidateId)
        && candidate.CandidateId != Guid.Empty
        && candidate.Status == expectedStatus;

    private static bool RevisionMatchesCandidate(
        PlanRevisionSnapshot? revision,
        CandidatePreviewSnapshot candidate,
        Guid expectedActorId) =>
        revision is not null
        && revision.RevisionId != Guid.Empty
        && revision.ProjectId == candidate.ProjectId
        && revision.PlanId == candidate.PlanId
        && revision.ParentRevisionId == candidate.BaseRevisionId
        && revision.VersionNumber > 0
        && string.Equals(revision.GeometrySchemaVersion, candidate.GeometrySchemaVersion, StringComparison.Ordinal)
        && string.Equals(revision.GeometryJson, candidate.GeometryJson, StringComparison.Ordinal)
        && string.Equals(revision.GeometryHash, candidate.GeometryHash, StringComparison.Ordinal)
        && revision.LandRevisionId == candidate.LandRevisionId
        && revision.RequirementsRevisionId == candidate.RequirementsRevisionId
        && ValidationMatches(revision.Validation, candidate.Validation)
        && revision.EditCommandId == candidate.CommandId
        && revision.CreatedBy == expectedActorId;

    private static bool ValidationMatches(
        PlanValidationReferenceDto actual,
        PlanValidationReferenceDto expected) =>
        actual.ValidationId == expected.ValidationId
        && string.Equals(actual.ValidatorVersion, expected.ValidatorVersion, StringComparison.Ordinal)
        && string.Equals(actual.GeometryHash, expected.GeometryHash, StringComparison.Ordinal)
        && actual.GeometryValid == expected.GeometryValid
        && actual.RequirementsMatch == expected.RequirementsMatch
        && string.Equals(actual.RegulatoryEvidenceStatus, expected.RegulatoryEvidenceStatus, StringComparison.Ordinal)
        && actual.HasBlockingIssues == expected.HasBlockingIssues
        && actual.ValidatedAtUtc.EqualsExact(expected.ValidatedAtUtc);

    private static CandidateActionHttpResponse ToActionResponse(
        CandidatePreviewSnapshot candidate,
        PlanRevisionSnapshot? revision) => new(
        candidate.CandidateId,
        candidate.ProjectId,
        candidate.PlanId,
        candidate.Status.ToString(),
        revision is null
            ? null
            : new AppliedRevisionHttpResponse(
                revision.RevisionId,
                revision.ParentRevisionId!.Value,
                revision.VersionNumber));

    private static int StatusFor(CandidateActionStatus status) => status switch
    {
        CandidateActionStatus.Unauthorized or CandidateActionStatus.NotFound or
            CandidateActionStatus.WrongProjectOrPlan => StatusCodes.Status404NotFound,
        CandidateActionStatus.AlreadyApplied or CandidateActionStatus.AlreadyRejected or
            CandidateActionStatus.Expired or CandidateActionStatus.StaleBaseRevision or
            CandidateActionStatus.StaleInputs => StatusCodes.Status409Conflict,
        CandidateActionStatus.ValidationRejected or CandidateActionStatus.BlockingIssues or
            CandidateActionStatus.WarningAcknowledgementRequired => StatusCodes.Status422UnprocessableEntity,
        CandidateActionStatus.DependencyUnavailable or CandidateActionStatus.ValidationUnavailable =>
            StatusCodes.Status503ServiceUnavailable,
        CandidateActionStatus.DependencyTimeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static bool TryReadScope(
        string projectId,
        string planId,
        out Guid project,
        out Guid plan,
        out IResult? failure)
    {
        if (!TryReadIdentity(projectId, "projectId", out project, out failure))
        {
            plan = Guid.Empty;
            return false;
        }

        return TryReadIdentity(planId, "planId", out plan, out failure);
    }

    private static bool TryReadIdentity(
        string value,
        string field,
        out Guid identity,
        out IResult? failure)
    {
        if (Guid.TryParse(value, out identity) && identity != Guid.Empty)
        {
            failure = null;
            return true;
        }

        failure = InvalidRequest(field, $"A nonempty {field} is required.");
        return false;
    }

    private static bool TryReadActor(
        ICurrentUser currentUser,
        out Guid actorId,
        out IResult? failure)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        if (currentUser.UserId is { } resolved && resolved != Guid.Empty)
        {
            actorId = resolved;
            failure = null;
            return true;
        }

        actorId = Guid.Empty;
        failure = Problem(StatusCodes.Status401Unauthorized, SharedErrorCodes.Unauthenticated);
        return false;
    }

    private static IResult InvalidRequest(string field, string message) =>
        ApiProblems.ValidationFailed(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult InvalidDependency() => Problem(
        StatusCodes.Status500InternalServerError,
        PlanEditorErrorCodes.InvalidDependencyResponse,
        ProblemDetailsDefaults.InternalErrorDetail);

    private static IResult Problem(int status, string code, string? detail = null) =>
        Results.Problem(
            statusCode: status,
            type: ProblemDetailsDefaults.GetTypeForCode(code),
            detail: status == StatusCodes.Status500InternalServerError
                ? ProblemDetailsDefaults.InternalErrorDetail
                : detail,
            extensions: new Dictionary<string, object?>
            {
                [ProblemDetailsDefaults.CodeExtension] = code,
            });

    private static void NoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store";
}

public sealed record ApplyCandidateHttpRequest(
    [property: JsonPropertyName("acknowledgedWarnings")] bool AcknowledgedWarnings);

/// <summary>Provisional reduced response for an explicit candidate action.</summary>
public sealed record CandidateActionHttpResponse(
    Guid CandidateId,
    Guid ProjectId,
    Guid PlanId,
    string Status,
    AppliedRevisionHttpResponse? Revision);

public sealed record AppliedRevisionHttpResponse(
    Guid RevisionId,
    Guid ParentRevisionId,
    long VersionNumber);
