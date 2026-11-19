namespace BuildMind.Application.Abstractions.Regulations;

/// <summary>
/// M7 human-reviewed regulatory constraint port (constraints contract v0; PENDING M6 + lead approval, doc 29).
/// Returns an evidence pack bound to the exact project, land and requirement revisions supplied. It never converts
/// retrieved passages into constraints and never decides plan compliance: M6's deterministic validator is
/// authoritative. Only <see cref="ConstraintKindStatus.Verified"/> kinds carry human-reviewed values that may block.
/// </summary>
public interface IRegulatoryConstraintSource
{
    Task<RegulatoryConstraintPack> GetConstraintsAsync(RegulatoryConstraintQuery query, CancellationToken cancellationToken);
}

public enum RegulatoryConstraintKind
{
    RearSpaceMin,
    SideSpaceMin,
    FrontSetbackMin,
    BuildingHeightMax,
    PlotCoverageMax,
    FloorAreaRatioMax,
    ParkingSpacesMin,
}

/// <summary>Per-kind evidence state. Anything other than <see cref="Verified"/> requires review; MISSING is never "no limit".</summary>
public enum ConstraintKindStatus
{
    Verified,
    Conflicting,
    Unverified,
    Missing,
}

/// <summary>The exact revisions an evidence pack is bound to (never inferred by the provider).</summary>
public sealed record ConstraintBinding(Guid ProjectId, int ProjectRevision, Guid LandRevisionId, Guid RequirementRevisionId);

public sealed record RegulatoryConstraintQuery(
    string Jurisdiction,
    IReadOnlyList<RegulatoryConstraintKind> Kinds,
    ConstraintBinding Binding,
    bool ResidentialOnly = false);

public sealed record RegulatoryConstraintPack(
    string EvidencePackId,
    string TableVersion,
    bool Synthetic,
    string Jurisdiction,
    ConstraintBinding Binding,
    IReadOnlyList<ConstraintKindResult> Kinds,
    bool Complete,
    IReadOnlyList<string> Warnings);

public sealed record ConstraintKindResult(RegulatoryConstraintKind Kind, ConstraintKindStatus Status, IReadOnlyList<RegulatoryConstraint> Constraints);

/// <param name="ReviewStatus"><c>HUMAN_REVIEWED</c> or <c>UNREVIEWED</c> (rejected constraints are never returned).</param>
public sealed record RegulatoryConstraint(
    string ConstraintId,
    string Comparator,
    double Value,
    string Unit,
    string AppliesTo,
    IReadOnlyList<ConstraintCondition> Conditions,
    string ReviewStatus,
    string? ReviewerRole,
    DateOnly? ReviewedAt,
    ConstraintSource Source);

public sealed record ConstraintCondition(string Field, string Comparator, double Value, string Unit);

/// <param name="Page">One-based PDF page position.</param>
public sealed record ConstraintSource(string DocumentId, int Page, int StartChar, int EndChar, string Excerpt);
