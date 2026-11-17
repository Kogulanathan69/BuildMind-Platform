using System.Text.Json;
using BuildMind.Application.Features.PlanEditor.Confirmation;
using BuildMind.Application.Features.PlanEditor.Editing;
using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.Application.Features.PlanEditor.Diagnostics;

public static class PlanEditorFailureMapper
{
    public static PlanEditorDiagnostic FromCandidatePreview(
        CandidatePreviewResultStatus status, string? correlationId = null) => status switch
    {
        CandidatePreviewResultStatus.Unauthorized => Failure(PlanEditorErrorCodes.UnauthorizedProjectAccess,
            "You are not authorized to access this project.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidatePreviewResultStatus.RevisionNotFound => Failure(PlanEditorErrorCodes.RevisionNotFound,
            "The requested plan revision was not found.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidatePreviewResultStatus.StaleBaseRevision => Failure(PlanEditorErrorCodes.RevisionConflict,
            "The plan changed before this request was completed.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidatePreviewResultStatus.StaleInputs => Failure(PlanEditorErrorCodes.CandidateBindingMismatch,
            "The candidate inputs are no longer current.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidatePreviewResultStatus.InvalidCommand => Failure(PlanEditorErrorCodes.InvalidEditCommand,
            "The edit command is invalid.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidatePreviewResultStatus.DependencyUnavailable => Retryable(PlanEditorErrorCodes.DependencyUnavailable,
            "A required service is temporarily unavailable.", correlationId),
        CandidatePreviewResultStatus.CandidateRejected => Failure(PlanEditorErrorCodes.InvalidDependencyResponse,
            "The edit candidate could not be accepted.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidatePreviewResultStatus.ValidationUnavailable => Retryable(PlanEditorErrorCodes.ValidationUnavailable,
            "Authoritative validation is temporarily unavailable.", correlationId),
        CandidatePreviewResultStatus.ValidationRejected => Failure(PlanEditorErrorCodes.ValidationEvidenceInvalid,
            "The validation evidence could not be accepted.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A successful result has no failure diagnostic.")
    };

    public static PlanEditorDiagnostic FromCandidateAction(
        CandidateActionStatus status, string? correlationId = null) => status switch
    {
        CandidateActionStatus.Unauthorized => Failure(PlanEditorErrorCodes.UnauthorizedProjectAccess,
            "You are not authorized to access this project.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidateActionStatus.NotFound => Failure(PlanEditorErrorCodes.CandidateNotFound,
            "The requested candidate was not found.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.WrongProjectOrPlan => Failure(PlanEditorErrorCodes.CandidateBindingMismatch,
            "The candidate does not belong to this plan.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidateActionStatus.AlreadyApplied => Failure(PlanEditorErrorCodes.CandidateAlreadyApplied,
            "The candidate has already been applied.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.AlreadyRejected => Failure(PlanEditorErrorCodes.CandidateAlreadyRejected,
            "The candidate has already been rejected.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.Expired => Failure(PlanEditorErrorCodes.CandidateExpired,
            "The candidate has expired.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.StaleBaseRevision => Failure(PlanEditorErrorCodes.RevisionConflict,
            "The plan changed before this candidate was applied.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.StaleInputs => Failure(PlanEditorErrorCodes.CandidateBindingMismatch,
            "The candidate inputs are no longer current.", PlanEditorDiagnosticSeverity.Error, correlationId),
        CandidateActionStatus.GeometryTampered => Failure(PlanEditorErrorCodes.CandidateTampered,
            "The candidate snapshot failed its integrity check.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidateActionStatus.ValidationUnavailable => Retryable(PlanEditorErrorCodes.ValidationUnavailable,
            "Authoritative validation is temporarily unavailable.", correlationId),
        CandidateActionStatus.ValidationRejected => Failure(PlanEditorErrorCodes.ValidationEvidenceInvalid,
            "The validation evidence could not be accepted.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidateActionStatus.BlockingIssues => Failure(PlanEditorErrorCodes.BlockingValidationIssues,
            "The candidate has blocking validation issues.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        CandidateActionStatus.WarningAcknowledgementRequired => new(
            PlanEditorErrorCodes.WarningAcknowledgementRequired,
            "Review and acknowledge the nonblocking warnings before continuing.",
            PlanEditorDiagnosticSeverity.Warning, true, correlationId, null, PlanEditorRetrySafety.Never),
        CandidateActionStatus.PersistenceFailure => new(
            PlanEditorErrorCodes.TransactionFailure, "The operation could not be saved.",
            PlanEditorDiagnosticSeverity.Critical, true, correlationId, null, PlanEditorRetrySafety.Conditional),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A successful result has no failure diagnostic.")
    };

    public static PlanEditorDiagnostic FromRevision(
        AppendRevisionStatus status, string? correlationId = null) => status switch
    {
        AppendRevisionStatus.RevisionConflict => Failure(PlanEditorErrorCodes.RevisionConflict,
            "The plan changed before the revision was saved.", PlanEditorDiagnosticSeverity.Error, correlationId),
        AppendRevisionStatus.NotFound => Failure(PlanEditorErrorCodes.RevisionNotFound,
            "The requested plan revision was not found.", PlanEditorDiagnosticSeverity.Error, correlationId),
        AppendRevisionStatus.Unauthorized => Failure(PlanEditorErrorCodes.UnauthorizedProjectAccess,
            "You are not authorized to access this project.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        AppendRevisionStatus.ValidationUnavailable => Retryable(PlanEditorErrorCodes.ValidationUnavailable,
            "Authoritative validation is temporarily unavailable.", correlationId),
        AppendRevisionStatus.ValidationRejected => Failure(PlanEditorErrorCodes.ValidationEvidenceInvalid,
            "The validation evidence could not be accepted.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        AppendRevisionStatus.DuplicateCommand => Failure(PlanEditorErrorCodes.DuplicateCommand,
            "This edit command has already been submitted.", PlanEditorDiagnosticSeverity.Error, correlationId),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A successful result has no failure diagnostic.")
    };

    public static PlanEditorDiagnostic FromConfirmation(
        ConfirmationStatus status, string? correlationId = null) => status switch
    {
        ConfirmationStatus.Unauthorized => Failure(PlanEditorErrorCodes.UnauthorizedProjectAccess,
            "You are not authorized to access this project.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        ConfirmationStatus.RevisionNotFound => Failure(PlanEditorErrorCodes.RevisionNotFound,
            "The requested plan revision was not found.", PlanEditorDiagnosticSeverity.Error, correlationId),
        ConfirmationStatus.GeometryHashMismatch => Failure(PlanEditorErrorCodes.GeometryHashMismatch,
            "The plan snapshot failed its integrity check.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        ConfirmationStatus.StaleInputs => Failure(PlanEditorErrorCodes.CandidateBindingMismatch,
            "The plan inputs are no longer current.", PlanEditorDiagnosticSeverity.Error, correlationId),
        ConfirmationStatus.ValidationStale => Failure(PlanEditorErrorCodes.ValidationEvidenceExpired,
            "The validation evidence is no longer current.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        ConfirmationStatus.NotCurrentRevision => Failure(PlanEditorErrorCodes.RevisionConflict,
            "Only the current plan revision can be confirmed.", PlanEditorDiagnosticSeverity.Error, correlationId),
        ConfirmationStatus.BlockingIssues => Failure(PlanEditorErrorCodes.ConfirmationBlocked,
            "The plan cannot be confirmed while blocking issues remain.", PlanEditorDiagnosticSeverity.Critical, correlationId),
        ConfirmationStatus.WarningAcknowledgementRequired => new(
            PlanEditorErrorCodes.WarningAcknowledgementRequired,
            "Review and acknowledge the nonblocking warnings before continuing.",
            PlanEditorDiagnosticSeverity.Warning, true, correlationId, null, PlanEditorRetrySafety.Never),
        ConfirmationStatus.AlreadyConfirmed => Failure(PlanEditorErrorCodes.ConfirmationAlreadyExists,
            "This exact plan revision is already confirmed.", PlanEditorDiagnosticSeverity.Error, correlationId),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A successful result has no failure diagnostic.")
    };

    public static PlanEditorDiagnostic FromDependencyException(
        PlanEditorDependencyOperation operation,
        Exception exception,
        string? correlationId,
        CancellationToken callerCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
            throw exception;
        if (exception is TimeoutException or OperationCanceledException)
            return new(PlanEditorErrorCodes.DependencyTimeout, "A required service timed out.",
                PlanEditorDiagnosticSeverity.Error, true, correlationId, null, PlanEditorRetrySafety.Safe);
        if (exception is HttpRequestException)
            return Retryable(PlanEditorErrorCodes.DependencyUnavailable,
                "A required service is temporarily unavailable.", correlationId);
        if (exception is JsonException or InvalidDataException or FormatException)
            return Failure(PlanEditorErrorCodes.InvalidDependencyResponse,
                "A required service returned an invalid response.", PlanEditorDiagnosticSeverity.Critical, correlationId);

        return operation switch
        {
            PlanEditorDependencyOperation.Persistence => Conditional(
                PlanEditorErrorCodes.PersistenceFailure, "The operation could not be saved.", correlationId),
            PlanEditorDependencyOperation.Transaction => Conditional(
                PlanEditorErrorCodes.TransactionFailure, "The operation could not be completed atomically.", correlationId),
            PlanEditorDependencyOperation.ReportPublication => Conditional(
                PlanEditorErrorCodes.ReportPublicationFailure, "The confirmed plan report could not be published.", correlationId),
            PlanEditorDependencyOperation.AiProposal => Failure(
                PlanEditorErrorCodes.AiProposalInvalid, "The AI proposal could not be accepted.",
                PlanEditorDiagnosticSeverity.Error, correlationId),
            _ => Failure(PlanEditorErrorCodes.UnexpectedInternalFailure,
                "The operation could not be completed.", PlanEditorDiagnosticSeverity.Critical, correlationId)
        };
    }

    public static int ProposedHttpStatus(PlanEditorDiagnostic diagnostic) => diagnostic.Code switch
    {
        PlanEditorErrorCodes.UnauthorizedProjectAccess => 403,
        PlanEditorErrorCodes.ProjectNotFound or PlanEditorErrorCodes.PlanNotFound or
            PlanEditorErrorCodes.RevisionNotFound or PlanEditorErrorCodes.CandidateNotFound => 404,
        PlanEditorErrorCodes.RevisionConflict or PlanEditorErrorCodes.DuplicateCommand or
            PlanEditorErrorCodes.CandidateAlreadyApplied or PlanEditorErrorCodes.CandidateAlreadyRejected or
            PlanEditorErrorCodes.ConfirmationAlreadyExists => 409,
        PlanEditorErrorCodes.InvalidEditCommand or PlanEditorErrorCodes.InvalidEditPayload or
            PlanEditorErrorCodes.UnsupportedSchemaVersion => 400,
        PlanEditorErrorCodes.DependencyUnavailable or PlanEditorErrorCodes.ValidationUnavailable => 503,
        PlanEditorErrorCodes.DependencyTimeout => 504,
        PlanEditorErrorCodes.PersistenceFailure or PlanEditorErrorCodes.TransactionFailure or
            PlanEditorErrorCodes.ReportPublicationFailure or PlanEditorErrorCodes.UnexpectedInternalFailure => 500,
        _ => 422
    };

    private static PlanEditorDiagnostic Failure(
        string code, string message, PlanEditorDiagnosticSeverity severity, string? correlationId) =>
        new(code, message, severity, true, correlationId, null, PlanEditorRetrySafety.Never);

    private static PlanEditorDiagnostic Retryable(string code, string message, string? correlationId) =>
        new(code, message, PlanEditorDiagnosticSeverity.Error, true, correlationId, null, PlanEditorRetrySafety.Safe);

    private static PlanEditorDiagnostic Conditional(string code, string message, string? correlationId) =>
        new(code, message, PlanEditorDiagnosticSeverity.Critical, true, correlationId, null,
            PlanEditorRetrySafety.Conditional);
}
