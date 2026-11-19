using BuildMind.Application.Abstractions;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Application.Features.Requirements.Abstractions;
using BuildMind.Application.Features.Requirements.Contracts;

namespace BuildMind.Application.Features.Requirements.Services;

/// <summary>Isolated owner-scoped workflow using existing policy validation and immutable persistence.</summary>
/// <remarks>
/// Sequential ownership checks are NOT atomic with revision reads/writes. D07 forbids production
/// activation until an approved atomic ownership protocol is implemented and verified. No owner,
/// policy, authorization evidence or identity is accepted from an HTTP client by this service.
/// Unknown exceptions (including internal uncertain transaction outcomes) propagate to the shared
/// safe error handler; they are never guessed to be an unavailable dependency or retried.
/// </remarks>
public sealed class RequirementsApplicationService : IRequirementsApplicationService
{
    private readonly ICurrentUser _user;
    private readonly IProjectRepository _projects;
    private readonly IRequirementCatalogueProvider _catalogues;
    private readonly RequirementRevisionReader _reader;
    private readonly ValidatedRequirementRevisionWriter _writer;

    public RequirementsApplicationService(ICurrentUser user, IProjectRepository projects,
        IRequirementCatalogueProvider catalogues, RequirementRevisionReader reader, ValidatedRequirementRevisionWriter writer)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _catalogues = catalogues ?? throw new ArgumentNullException(nameof(catalogues));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task<RequirementsOperationResult> GetAsync(Guid projectId, CancellationToken cancellationToken) =>
        ExecuteAsync(projectId, null, false, cancellationToken);

    public Task<RequirementsOperationResult> SaveAsync(Guid projectId, SaveRequirementsRequest? request, CancellationToken cancellationToken) =>
        ExecuteAsync(projectId, request, true, cancellationToken);

    private async Task<RequirementsOperationResult> ExecuteAsync(Guid projectId, SaveRequirementsRequest? request,
        bool save, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (projectId == Guid.Empty) throw new ArgumentException("A project identity is required.", nameof(projectId));
        try
        {
            var denied = await CheckOwnerAsync(projectId, token).ConfigureAwait(false);
            if (denied is not null) return new(denied.Value);
            if (!save)
            {
                var revision = await _reader.GetCurrentAsync(projectId, token).ConfigureAwait(false);
                return revision is null ? new(RequirementsOperationStatus.NoSavedRevision)
                    : new(RequirementsOperationStatus.Success, new RequirementRevisionDto(revision));
            }

            var catalogue = await _catalogues.GetForProjectAsync(projectId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var validation = RequirementContractValidator.Validate(request, catalogue);
            if (validation.Status == RequirementValidationStatus.PolicyUnavailable)
                return new(RequirementsOperationStatus.DependencyUnavailable);
            var conflicts = validation.Issues.Where(item => item.Code == "CATALOGUE_VERSION_MISMATCH").ToArray();
            if (conflicts.Length > 0) return new(RequirementsOperationStatus.CatalogueConflict, issues: conflicts);
            if (validation.Status != RequirementValidationStatus.Valid)
                return new(RequirementsOperationStatus.ValidationFailed, issues: validation.Issues);

            var result = await _writer.SaveAsync(projectId, request!.ExpectedRevisionId, validation, token).ConfigureAwait(false);
            // No post-save cancellation check: a confirmed commit must remain a confirmed commit.
            return result.Status switch
            {
                RequirementSaveStatus.Saved => new(RequirementsOperationStatus.Success, new RequirementRevisionDto(result.SavedRevision!)),
                RequirementSaveStatus.RevisionConflict => new(RequirementsOperationStatus.RevisionConflict),
                RequirementSaveStatus.ProjectNotFound => new(RequirementsOperationStatus.NotFound),
                _ => new(RequirementsOperationStatus.InvalidDependencyResponse)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(RequirementsOperationStatus.DependencyUnavailable); }
        catch (TimeoutException) { token.ThrowIfCancellationRequested(); return new(RequirementsOperationStatus.DependencyTimeout); }
        catch (DependencyFailureException exception)
        {
            token.ThrowIfCancellationRequested();
            return new(exception.Kind switch
            {
                DependencyFailureKind.Timeout => RequirementsOperationStatus.DependencyTimeout,
                DependencyFailureKind.Disabled or DependencyFailureKind.Unavailable => RequirementsOperationStatus.DependencyUnavailable,
                // D06 distinguishes an unavailable dependency from a corrupt/unsupported response.
                _ => RequirementsOperationStatus.InvalidDependencyResponse
            });
        }
        catch (ArgumentException) { token.ThrowIfCancellationRequested(); return new(RequirementsOperationStatus.InvalidDependencyResponse); }
        catch (InvalidOperationException) { token.ThrowIfCancellationRequested(); return new(RequirementsOperationStatus.InvalidDependencyResponse); }
        catch (OverflowException) { token.ThrowIfCancellationRequested(); return new(RequirementsOperationStatus.InvalidDependencyResponse); }
    }

    private async Task<RequirementsOperationStatus?> CheckOwnerAsync(Guid projectId, CancellationToken token)
    {
        Guid owner;
        try
        {
            if (!_user.IsAuthenticated) return RequirementsOperationStatus.Unauthenticated;
            owner = _user.GetRequiredUserId();
            if (owner == Guid.Empty || _user.UserId != owner) return RequirementsOperationStatus.Unauthenticated;
        }
        catch (UnauthenticatedAccessException) { token.ThrowIfCancellationRequested(); return RequirementsOperationStatus.Unauthenticated; }
        token.ThrowIfCancellationRequested();
        var project = await _projects.GetByIdAndOwnerAsync(projectId, owner, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (project is null) return RequirementsOperationStatus.NotFound;
        if (project.Id != projectId || project.OwnerId != owner) return RequirementsOperationStatus.InvalidDependencyResponse;
        return null;
    }
}
