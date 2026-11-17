using System.Text.Json;
using BuildMind.Application.Features.PlanEditor.Confirmation;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Integration;
using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.Application.Features.PlanEditor.Editing;

public sealed class CandidatePreviewService(
    IProjectOwnershipGate ownership,
    IPlanRevisionStore revisions,
    IApplicableInputRevisionSource currentInputs,
    IConfirmedLandRevisionPort landRevisions,
    IRequirementRevisionPort requirementRevisions,
    IPlanEditorComputeClient computeClient,
    ICandidateValidationEvidenceSource validationEvidenceSource,
    IPlanValidationEvidenceVerifier validationVerifier,
    ICandidatePreviewStore candidates,
    ICandidatePreviewTransaction transaction,
    TimeProvider timeProvider,
    TimeSpan candidateLifetime)
{
    public async Task<CandidatePreviewResult> PreviewAsync(
        CreateCandidatePreviewCommand command,
        Guid authenticatedOwnerId,
        CancellationToken cancellationToken = default)
    {
        if (authenticatedOwnerId == Guid.Empty ||
            !await ownership.OwnsProjectAsync(command.ProjectId, authenticatedOwnerId, cancellationToken))
            return new(CandidatePreviewResultStatus.Unauthorized);
        EnsureConfiguredLifetime();

        var current = await revisions.GetCurrentAsync(command.ProjectId, command.PlanId, cancellationToken);
        if (current is null) return new(CandidatePreviewResultStatus.RevisionNotFound);
        if (current.RevisionId != command.BaseRevisionId)
            return new(CandidatePreviewResultStatus.StaleBaseRevision);

        var applicable = await currentInputs.GetCurrentAsync(command.ProjectId, cancellationToken);
        if (current.LandRevisionId != applicable.LandRevisionId ||
            current.RequirementsRevisionId != applicable.RequirementsRevisionId)
            return new(CandidatePreviewResultStatus.StaleInputs);
        var land = await landRevisions.GetAsync(command.ProjectId, applicable.LandRevisionId, cancellationToken);
        var requirements = await requirementRevisions.GetAsync(
            command.ProjectId, applicable.RequirementsRevisionId, cancellationToken);
        if (land is null || requirements is null)
            return new(CandidatePreviewResultStatus.DependencyUnavailable);
        if (land.ProjectId != command.ProjectId || requirements.ProjectId != command.ProjectId ||
            land.RevisionId != applicable.LandRevisionId || requirements.RevisionId != applicable.RequirementsRevisionId)
            return new(CandidatePreviewResultStatus.StaleInputs);

        try
        {
            PlanEditorContractValidator.Validate(new PlanEditRequestDto
            {
                SchemaVersion = current.GeometrySchemaVersion,
                ProjectId = command.ProjectId,
                PlanId = command.PlanId,
                BaseRevisionId = command.BaseRevisionId,
                Command = command.Command
            });
        }
        catch (ArgumentException)
        {
            return new(CandidatePreviewResultStatus.InvalidCommand);
        }

        var sourceGeometry = JsonSerializer.Deserialize<PlanGeometryDto>(current.GeometryJson, PlanEditorJson.StrictOptions)
            ?? throw new InvalidOperationException("Current geometry snapshot could not be read.");
        PlanEditResultDto computed;
        try
        {
            computed = await computeClient.PreviewAsync(
                new PlanEditorComputeRequest(sourceGeometry, command.Command, command.CorrelationId), cancellationToken);
            PlanEditorContractValidator.Validate(computed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            return new(CandidatePreviewResultStatus.DependencyUnavailable);
        }
        catch (ArgumentException)
        {
            return new(CandidatePreviewResultStatus.CandidateRejected);
        }
        if (computed.CommandId != command.Command.CommandId || computed.BaseRevisionId != command.BaseRevisionId)
            return new(CandidatePreviewResultStatus.CandidateRejected, Diagnostics: computed.Diagnostics);
        if (computed.Status is not (PlanEditStatus.Accepted or PlanEditStatus.AcceptedWithWarnings) ||
            computed.CandidateGeometry is null)
            return new(CandidatePreviewResultStatus.CandidateRejected, Diagnostics: computed.Diagnostics);
        if (computed.CandidateGeometry.ProjectId != command.ProjectId ||
            computed.CandidateGeometry.PlanId != command.PlanId ||
            computed.CandidateGeometry.LandRevisionId != applicable.LandRevisionId ||
            computed.CandidateGeometry.RequirementsRevisionId != applicable.RequirementsRevisionId)
            return new(CandidatePreviewResultStatus.CandidateRejected, Diagnostics: computed.Diagnostics);

        var geometryJson = JsonSerializer.Serialize(computed.CandidateGeometry, PlanEditorJson.StrictOptions);
        var geometryHash = PlanRevisionService.ComputeGeometryHash(geometryJson);
        var evidence = await validationEvidenceSource.GetAsync(
            command.ProjectId, command.PlanId, geometryHash, cancellationToken);
        if (evidence is null) return new(CandidatePreviewResultStatus.ValidationUnavailable);
        if (!string.Equals(evidence.GeometryHash, geometryHash, StringComparison.Ordinal))
            return new(CandidatePreviewResultStatus.ValidationRejected);
        var validationStatus = await validationVerifier.VerifyAsync(
            command.ProjectId, command.PlanId, evidence, geometryHash, cancellationToken);
        if (validationStatus == ValidationEvidenceStatus.Unavailable)
            return new(CandidatePreviewResultStatus.ValidationUnavailable);
        if (validationStatus != ValidationEvidenceStatus.Verified || !evidence.GeometryValid || evidence.HasBlockingIssues)
            return new(CandidatePreviewResultStatus.ValidationRejected);

        var now = timeProvider.GetUtcNow();
        var candidate = new CandidatePreviewSnapshot(
            Guid.NewGuid(), command.ProjectId, command.PlanId, authenticatedOwnerId,
            command.BaseRevisionId, applicable.LandRevisionId, applicable.RequirementsRevisionId,
            command.Command.CommandId, geometryJson, geometryHash, computed.CandidateGeometry.SchemaVersion,
            evidence, now, now.Add(candidateLifetime), CandidatePreviewStatus.Pending);
        await candidates.CreateAsync(candidate, cancellationToken);
        return new(CandidatePreviewResultStatus.Created, candidate, computed.Diagnostics);
    }

    public async Task<CandidateActionResult> ApplyAsync(
        ApplyCandidateCommand command,
        Guid authenticatedOwnerId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetAuthorizedCandidateAsync(
            command.CandidateId, command.ProjectId, command.PlanId, authenticatedOwnerId, cancellationToken);
        if (candidate.Result is not null) return candidate.Result;
        var snapshot = candidate.Snapshot!;
        var now = timeProvider.GetUtcNow();
        var terminal = await ResolveTerminalAsync(snapshot, now, cancellationToken);
        if (terminal is not null) return terminal;

        var current = await revisions.GetCurrentAsync(command.ProjectId, command.PlanId, cancellationToken);
        if (current?.RevisionId != snapshot.BaseRevisionId)
            return new(CandidateActionStatus.StaleBaseRevision, snapshot);
        var applicable = await currentInputs.GetCurrentAsync(command.ProjectId, cancellationToken);
        if (applicable.LandRevisionId != snapshot.LandRevisionId ||
            applicable.RequirementsRevisionId != snapshot.RequirementsRevisionId)
            return new(CandidateActionStatus.StaleInputs, snapshot);

        PlanGeometryDto geometry;
        try
        {
            geometry = JsonSerializer.Deserialize<PlanGeometryDto>(snapshot.GeometryJson, PlanEditorJson.StrictOptions)
                ?? throw new JsonException("Candidate geometry was null.");
            PlanEditorContractValidator.Validate(geometry);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return new(CandidateActionStatus.GeometryTampered, snapshot);
        }
        var exactHash = PlanRevisionService.ComputeGeometryHash(
            JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
        if (!string.Equals(exactHash, snapshot.GeometryHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Validation.GeometryHash, exactHash, StringComparison.Ordinal))
            return new(CandidateActionStatus.GeometryTampered, snapshot);

        var validationStatus = await validationVerifier.VerifyAsync(
            snapshot.ProjectId, snapshot.PlanId, snapshot.Validation, exactHash, cancellationToken);
        if (validationStatus == ValidationEvidenceStatus.Unavailable)
            return new(CandidateActionStatus.ValidationUnavailable, snapshot);
        if (validationStatus != ValidationEvidenceStatus.Verified)
            return new(CandidateActionStatus.ValidationRejected, snapshot);
        if (!snapshot.Validation.GeometryValid || snapshot.Validation.HasBlockingIssues)
            return new(CandidateActionStatus.BlockingIssues, snapshot);
        var hasWarnings = snapshot.Validation.RequirementsMatch is not true ||
            !string.Equals(snapshot.Validation.RegulatoryEvidenceStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase);
        if (hasWarnings && !command.AcknowledgedWarnings)
            return new(CandidateActionStatus.WarningAcknowledgementRequired, snapshot);

        var revision = new PlanRevisionSnapshot(
            Guid.NewGuid(), snapshot.PlanId, snapshot.ProjectId, snapshot.BaseRevisionId,
            checked(current.VersionNumber + 1), snapshot.GeometrySchemaVersion, snapshot.GeometryJson,
            snapshot.GeometryHash, snapshot.LandRevisionId, snapshot.RequirementsRevisionId,
            snapshot.Validation, snapshot.CommandId, authenticatedOwnerId, now, command.ChangeSummary);
        try
        {
            return await transaction.TryApplyAtomicallyAsync(snapshot, revision, now, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return new(CandidateActionStatus.PersistenceFailure, snapshot);
        }
    }

    public async Task<CandidateActionResult> RejectAsync(
        CandidateActionCommand command,
        Guid authenticatedOwnerId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetAuthorizedCandidateAsync(
            command.CandidateId, command.ProjectId, command.PlanId, authenticatedOwnerId, cancellationToken);
        if (candidate.Result is not null) return candidate.Result;
        var snapshot = candidate.Snapshot!;
        var now = timeProvider.GetUtcNow();
        var terminal = await ResolveTerminalAsync(snapshot, now, cancellationToken);
        if (terminal is not null) return terminal;
        try
        {
            return await transaction.TryRejectAtomicallyAsync(snapshot, now, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return new(CandidateActionStatus.PersistenceFailure, snapshot);
        }
    }

    private async Task<(CandidatePreviewSnapshot? Snapshot, CandidateActionResult? Result)> GetAuthorizedCandidateAsync(
        Guid candidateId, Guid projectId, Guid planId, Guid authenticatedOwnerId,
        CancellationToken cancellationToken)
    {
        if (authenticatedOwnerId == Guid.Empty ||
            !await ownership.OwnsProjectAsync(projectId, authenticatedOwnerId, cancellationToken))
            return (null, new(CandidateActionStatus.Unauthorized));
        var candidate = await candidates.GetAsync(candidateId, cancellationToken);
        if (candidate is null) return (null, new(CandidateActionStatus.NotFound));
        if (candidate.OwnerId != authenticatedOwnerId) return (null, new(CandidateActionStatus.Unauthorized));
        if (candidate.ProjectId != projectId || candidate.PlanId != planId)
            return (null, new(CandidateActionStatus.WrongProjectOrPlan));
        return (candidate, null);
    }

    private async Task<CandidateActionResult?> ResolveTerminalAsync(
        CandidatePreviewSnapshot candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var status = candidate.Status switch
        {
            CandidatePreviewStatus.Applied => CandidateActionStatus.AlreadyApplied,
            CandidatePreviewStatus.Rejected => CandidateActionStatus.AlreadyRejected,
            CandidatePreviewStatus.Expired => CandidateActionStatus.Expired,
            _ => (CandidateActionStatus?)null
        };
        if (status is not null) return new(status.Value, candidate);
        if (now < candidate.ExpiresAtUtc) return null;
        return await transaction.TryExpireAtomicallyAsync(candidate, now, cancellationToken);
    }

    private void EnsureConfiguredLifetime()
    {
        if (candidateLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Candidate lifetime must be supplied by trusted configuration.");
    }
}
