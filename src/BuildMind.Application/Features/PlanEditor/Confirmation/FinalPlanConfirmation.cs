using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.Application.Features.PlanEditor.Confirmation;

public sealed record ConfirmPlanCommand(
    Guid ProjectId,
    Guid PlanId,
    Guid RevisionId,
    string ExpectedGeometryHash,
    bool AcknowledgedWarnings);

public sealed record ApplicableInputRevisions(Guid LandRevisionId, Guid RequirementsRevisionId);

public sealed record PlanConfirmationSnapshot(
    Guid ConfirmationId,
    Guid ProjectId,
    Guid PlanId,
    Guid ConfirmedRevisionId,
    string GeometryHash,
    Guid ValidationId,
    Guid ConfirmedBy,
    DateTimeOffset ConfirmedAtUtc,
    bool WarningAcknowledgement);

public enum ConfirmationStatus
{
    Confirmed,
    Unauthorized,
    RevisionNotFound,
    GeometryHashMismatch,
    StaleInputs,
    ValidationStale,
    NotCurrentRevision,
    BlockingIssues,
    WarningAcknowledgementRequired,
    AlreadyConfirmed
}

public sealed record ConfirmationResult(ConfirmationStatus Status, PlanConfirmationSnapshot? Confirmation = null);

public interface IProjectOwnershipGate
{
    Task<bool> OwnsProjectAsync(Guid projectId, Guid authenticatedUserId, CancellationToken cancellationToken);
}

public interface IApplicableInputRevisionSource
{
    Task<ApplicableInputRevisions> GetCurrentAsync(Guid projectId, CancellationToken cancellationToken);
}

public interface IValidationFreshnessPolicy
{
    bool IsFresh(PlanRevisionSnapshot revision, DateTimeOffset now);
}

public interface IFinalConfirmationTransaction
{
    /// <summary>
    /// The M4 adapter must atomically create the immutable confirmation and
    /// apply the approved M1 lifecycle transition, with idempotency enforced.
    /// </summary>
    Task<ConfirmationResult> ConfirmAtomicallyAsync(
        PlanConfirmationSnapshot confirmation,
        PlanRevisionSnapshot exactRevision,
        CancellationToken cancellationToken);
}

public interface IConfirmedPlanSnapshotSink
{
    Task PublishAsync(PlanConfirmationSnapshot confirmation, PlanRevisionSnapshot exactRevision, CancellationToken cancellationToken);
}

public sealed class FinalPlanConfirmationService(
    IProjectOwnershipGate ownership,
    IPlanRevisionStore revisions,
    IApplicableInputRevisionSource inputs,
    IValidationFreshnessPolicy freshnessPolicy,
    IFinalConfirmationTransaction transaction,
    IConfirmedPlanSnapshotSink reportSink,
    TimeProvider timeProvider)
{
    public async Task<ConfirmationResult> ConfirmAsync(
        ConfirmPlanCommand command,
        Guid authenticatedUserId,
        CancellationToken cancellationToken = default)
    {
        if (authenticatedUserId == Guid.Empty
            || !await ownership.OwnsProjectAsync(command.ProjectId, authenticatedUserId, cancellationToken))
            return new(ConfirmationStatus.Unauthorized);

        var revision = await revisions.GetAsync(command.ProjectId, command.PlanId, command.RevisionId, cancellationToken);
        if (revision is null) return new(ConfirmationStatus.RevisionNotFound);
        var currentRevision = await revisions.GetCurrentAsync(command.ProjectId, command.PlanId, cancellationToken);
        if (currentRevision?.RevisionId != revision.RevisionId)
            return new(ConfirmationStatus.NotCurrentRevision);
        if (!string.Equals(revision.GeometryHash, command.ExpectedGeometryHash, StringComparison.Ordinal))
            return new(ConfirmationStatus.GeometryHashMismatch);
        if (!string.Equals(revision.Validation.GeometryHash, revision.GeometryHash, StringComparison.Ordinal))
            return new(ConfirmationStatus.ValidationStale);
        if (!freshnessPolicy.IsFresh(revision, timeProvider.GetUtcNow()))
            return new(ConfirmationStatus.ValidationStale);
        if (!revision.Validation.GeometryValid || revision.Validation.HasBlockingIssues)
            return new(ConfirmationStatus.BlockingIssues);

        var currentInputs = await inputs.GetCurrentAsync(command.ProjectId, cancellationToken);
        if (revision.LandRevisionId != currentInputs.LandRevisionId
            || revision.RequirementsRevisionId != currentInputs.RequirementsRevisionId)
            return new(ConfirmationStatus.StaleInputs);

        var hasWarnings = !string.Equals(
            revision.Validation.RegulatoryEvidenceStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase)
            || revision.Validation.RequirementsMatch is not true;
        if (hasWarnings && !command.AcknowledgedWarnings)
            return new(ConfirmationStatus.WarningAcknowledgementRequired);

        var confirmation = new PlanConfirmationSnapshot(
            Guid.NewGuid(), command.ProjectId, command.PlanId, revision.RevisionId,
            revision.GeometryHash, revision.Validation.ValidationId, authenticatedUserId,
            timeProvider.GetUtcNow(), command.AcknowledgedWarnings);
        var result = await transaction.ConfirmAtomicallyAsync(confirmation, revision, cancellationToken);
        if (result.Status == ConfirmationStatus.Confirmed && result.Confirmation is not null)
            await reportSink.PublishAsync(result.Confirmation, revision, cancellationToken);
        return result;
    }
}
