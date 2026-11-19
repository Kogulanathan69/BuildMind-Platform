namespace BuildMind.Infrastructure.Persistence.Records;

// BE-M4-D03 persistence row models. They are Infrastructure-only: Domain and Application never see them,
// and domain objects are rebuilt through their own validating constructors/factories (no reflection).
// Every column mirrors a property of an existing domain type; nothing here invents feature data.

/// <summary>Row of <c>users</c>, shaped after M1 <c>BuildMind.Domain.Entities.User</c>.</summary>
/// <remarks>
/// M1's <c>User</c> has private setters for <c>Id</c>/<c>IsActive</c> and no rehydration factory, so
/// <c>IUserRepository</c> cannot be implemented without reflection yet (blocked on M1; see D03 register).
/// The table exists now because <c>projects.owner_id</c> references it.
/// </remarks>
internal sealed class UserRecord
{
    public const int EmailMaxLength = 254;          // M1 auth validators
    public const int PasswordHashMaxLength = 512;   // hash format undecided (A1/A2); sized generously
    public const int FullNameMaxLength = 100;       // M1 UpdateProfileRequestValidator
    public const int PhoneNumberMaxLength = 16;     // M1 UpdateProfileRequestValidator

    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string? FullName { get; set; }

    public string? PhoneNumber { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Row of <c>projects</c>, shaped after M1 <c>BuildMind.Domain.Entities.Project</c>.</summary>
/// <remarks>
/// <c>Project.Id</c>, <c>Status</c>, <c>Revision</c> and <c>UpdatedAtUtc</c> have private setters and no rehydration
/// factory, so <c>IProjectRepository</c> is blocked on M1 (see D03 register). The table exists now because
/// requirement revisions are project-scoped.
/// </remarks>
internal sealed class ProjectRecord
{
    public const int NameMaxLength = 150;               // M1 project validators
    public const int ProjectTypeMaxLength = 100;        // M1 project validators
    public const int LocationReferenceMaxLength = 500;  // M1 project validators
    public const int StatusMaxLength = 32;              // domain default "Created"; status set undecided

    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ProjectType { get; set; }

    public string? LocationReference { get; set; }

    public string Status { get; set; } = string.Empty;

    public long Revision { get; set; }

    public long AuthorizationVersion { get; set; } = 1;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

/// <summary>Immutable row of <c>requirement_revisions</c> (M5 <c>RequirementRevision</c>).</summary>
internal sealed class RequirementRevisionRecord
{
    public const int VersionMaxLength = 64; // no length rule exists in M5 contracts; M4 storage limit

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public long RevisionNumber { get; set; }

    public string SchemaVersion { get; set; } = string.Empty;

    public string CatalogueVersion { get; set; } = string.Empty;

    /// <summary>Canonical JSON of <c>RequirementContent</c> (native <c>json</c> column).</summary>
    public string ContentJson { get; set; } = "{}";

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Row of <c>project_requirement_heads</c>: the per-project "current revision" pointer required by
/// <c>IRequirementRevisionRepository.TrySaveAtomicallyAsync</c> (compare-and-set target).
/// </summary>
internal sealed class RequirementHeadRecord
{
    public Guid ProjectId { get; set; }

    public Guid CurrentRevisionId { get; set; }

    public long CurrentRevisionNumber { get; set; }
}
