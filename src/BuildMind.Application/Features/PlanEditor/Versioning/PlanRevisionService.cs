using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildMind.Application.Features.PlanEditor.Contracts;

namespace BuildMind.Application.Features.PlanEditor.Versioning;

public sealed class PlanRevisionService(
    IPlanRevisionStore store,
    IPlanValidationEvidenceVerifier validationEvidenceVerifier,
    TimeProvider timeProvider)
{
    public async Task<AppendRevisionResult> CreateAsync(
        CreatePlanRevisionCommand command,
        Guid authenticatedCreatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PlanEditorContractValidator.Validate(command.Geometry);
        if (command.Geometry.ProjectId != command.ProjectId || command.Geometry.PlanId != command.PlanId)
            throw new ArgumentException("Geometry identity does not match the revision command.");

        var current = await store.GetAsync(
            command.ProjectId, command.PlanId, command.ExpectedBaseRevisionId, cancellationToken);
        if (current is null) return new(AppendRevisionStatus.NotFound);

        if (authenticatedCreatorId == Guid.Empty)
            throw new ArgumentException("Authenticated creator identity is required.");
        var json = JsonSerializer.Serialize(command.Geometry, PlanEditorJson.StrictOptions);
        var hash = ComputeGeometryHash(json);
        if (!string.Equals(command.Validation.GeometryHash, hash, StringComparison.Ordinal))
            throw new ArgumentException("Validation evidence does not match the exact geometry snapshot.");
        if (!command.Validation.GeometryValid || command.Validation.HasBlockingIssues)
            throw new ArgumentException("A revision cannot be created from blocking validation evidence.");
        var evidenceStatus = await validationEvidenceVerifier.VerifyAsync(
            command.ProjectId, command.PlanId, command.Validation, hash, cancellationToken);
        if (evidenceStatus == ValidationEvidenceStatus.Unavailable)
            return new(AppendRevisionStatus.ValidationUnavailable);
        if (evidenceStatus != ValidationEvidenceStatus.Verified)
            return new(AppendRevisionStatus.ValidationRejected);

        var revision = new PlanRevisionSnapshot(
            Guid.NewGuid(), command.PlanId, command.ProjectId, current.RevisionId,
            checked(current.VersionNumber + 1), command.Geometry.SchemaVersion, json, hash,
            command.Geometry.LandRevisionId, command.Geometry.RequirementsRevisionId,
            command.Validation, command.EditCommandId, authenticatedCreatorId,
            timeProvider.GetUtcNow(), command.ChangeSummary);

        return await store.TryAppendAtomicallyAsync(command.ExpectedBaseRevisionId, revision, cancellationToken);
    }

    public async Task<AppendRevisionResult> RestoreAsync(
        Guid projectId,
        Guid planId,
        Guid revisionToRestoreId,
        Guid expectedCurrentRevisionId,
        PlanValidationReferenceDto freshValidation,
        Guid authenticatedRestorerId,
        CancellationToken cancellationToken = default)
    {
        var source = await store.GetAsync(projectId, planId, revisionToRestoreId, cancellationToken);
        if (source is null) return new(AppendRevisionStatus.NotFound);
        var geometry = JsonSerializer.Deserialize<PlanGeometryDto>(source.GeometryJson, PlanEditorJson.StrictOptions)
            ?? throw new InvalidOperationException("Stored geometry snapshot could not be read.");
        return await CreateAsync(
            new(projectId, planId, expectedCurrentRevisionId, geometry, freshValidation, null,
                $"Restore revision {revisionToRestoreId}"), authenticatedRestorerId, cancellationToken);
    }

    public static string ComputeGeometryHash(string geometryJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(geometryJson));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
