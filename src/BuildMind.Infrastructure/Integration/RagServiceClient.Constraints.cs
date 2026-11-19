using System.Text.Json.Serialization;
using BuildMind.Application.Abstractions.Regulations;

namespace BuildMind.Infrastructure.Integration;

/// <summary>
/// M7 constraint evidence packs (constraints contract v0, PENDING M6 + lead approval) on the same hardened transport.
/// Every pack is checked on receipt: bound to EXACTLY the requested revisions, jurisdiction and kinds; per-kind states
/// consistent with the returned constraints (VERIFIED requires a human-reviewed value, MISSING has none, REJECTED never
/// appears); kind-specific comparator and unit; finite values; never a legal-verification claim. Any violation is a
/// <c>MalformedResponse</c>, never a partially trusted pack.
/// </summary>
public sealed partial class RagServiceClient : IRegulatoryConstraintSource
{
    internal const string ConstraintsPath = "internal/v1/regulations/constraints";
    internal const string ConstraintsContractVersion = "constraints-0";

    private static readonly Dictionary<RegulatoryConstraintKind, (string Wire, string Comparator, string Unit)> KindRules = new()
    {
        [RegulatoryConstraintKind.RearSpaceMin] = ("REAR_SPACE_MIN", ">=", "m"),
        [RegulatoryConstraintKind.SideSpaceMin] = ("SIDE_SPACE_MIN", ">=", "m"),
        [RegulatoryConstraintKind.FrontSetbackMin] = ("FRONT_SETBACK_MIN", ">=", "m"),
        [RegulatoryConstraintKind.BuildingHeightMax] = ("BUILDING_HEIGHT_MAX", "<=", "m"),
        [RegulatoryConstraintKind.PlotCoverageMax] = ("PLOT_COVERAGE_MAX", "<=", "percent"),
        [RegulatoryConstraintKind.FloorAreaRatioMax] = ("FLOOR_AREA_RATIO_MAX", "<=", "ratio"),
        [RegulatoryConstraintKind.ParkingSpacesMin] = ("PARKING_SPACES_MIN", ">=", "count"),
    };

    private static readonly Dictionary<string, ConstraintKindStatus> KindStatuses = new(StringComparer.Ordinal)
    {
        ["VERIFIED"] = ConstraintKindStatus.Verified,
        ["CONFLICTING"] = ConstraintKindStatus.Conflicting,
        ["UNVERIFIED"] = ConstraintKindStatus.Unverified,
        ["MISSING"] = ConstraintKindStatus.Missing,
    };

    private static readonly HashSet<string> ConditionFields = ["building_height_m", "plot_area_m2", "floors", "road_width_m"];
    private static readonly HashSet<string> ConditionComparators = ["<", "<=", ">", ">=", "=="];

    public async Task<RegulatoryConstraintPack> GetConstraintsAsync(RegulatoryConstraintQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        var request = new WireConstraintsRequest(
            query.Jurisdiction,
            [.. query.Kinds.Select(k => KindRules[k].Wire)],
            query.ResidentialOnly ? "residential" : null,
            WireBinding.From(query.Binding));

        var wire = await PostJsonAsync<WireConstraintsRequest, WireConstraintPack>(
                ConstraintsPath, request, pack => IsValidPack(pack, request), cancellationToken)
            .ConfigureAwait(false);

        return new RegulatoryConstraintPack(
            wire.EvidencePackId!,
            wire.TableVersion!,
            wire.Synthetic!.Value,
            wire.Jurisdiction!,
            query.Binding,
            [.. wire.Kinds!.Select(k => k!).Select(k => new ConstraintKindResult(
                KindRules.Single(r => r.Value.Wire == k.Kind).Key,
                KindStatuses[k.Status!],
                [.. k.Constraints!.Select(c => c!).Select(c => new RegulatoryConstraint(
                    c.ConstraintId!, c.Comparator!, c.Value!.Value, c.Unit!, c.AppliesTo!,
                    [.. c.Conditions!.Select(x => x!).Select(x => new ConstraintCondition(x.Field!, x.Comparator!, x.Value!.Value, x.Unit!))],
                    c.ReviewStatus!, c.ReviewerRole, c.ReviewedAt,
                    new ConstraintSource(c.Source!.DocumentId!, c.Source.Page, c.Source.StartChar, c.Source.EndChar, c.Source.Excerpt!)))]))],
            wire.OverallStatus == "COMPLETE",
            [.. wire.Warnings!.Select(w => w!)]); // every element proven non-null by IsValidPack
    }

    private static void ValidateQuery(RegulatoryConstraintQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Jurisdiction) || query.Jurisdiction.Length > 200)
        {
            throw new ArgumentException("Jurisdiction must contain 1 to 200 characters.", nameof(query));
        }

        if (query.Kinds is null || query.Kinds.Count is < 1 || query.Kinds.Count > KindRules.Count
            || query.Kinds.Distinct().Count() != query.Kinds.Count || query.Kinds.Any(k => !KindRules.ContainsKey(k)))
        {
            throw new ArgumentException("Kinds must be 1 to 7 distinct known constraint kinds.", nameof(query));
        }

        if (query.Binding is null || query.Binding.ProjectRevision < 1 || query.Binding.ProjectId == Guid.Empty
            || query.Binding.LandRevisionId == Guid.Empty || query.Binding.RequirementRevisionId == Guid.Empty)
        {
            throw new ArgumentException("The binding must identify exact, non-empty project, land and requirement revisions.", nameof(query));
        }
    }

    internal static bool IsValidPack(WireConstraintPack p, WireConstraintsRequest request)
    {
        if (p.ContractVersion != ConstraintsContractVersion || p.LegalStatusVerified is not false || p.Synthetic is null
            || p.EvidencePackId is null || !DocumentIdPattern().IsMatch(p.EvidencePackId)
            || string.IsNullOrEmpty(p.TableVersion) || p.Jurisdiction != request.Jurisdiction
            || p.BuildingUse != request.BuildingUse || p.Binding != request.Binding
            || p.Warnings is null || p.Warnings.Any(w => w is null)
            || p.Kinds is null || !p.Kinds.Select(k => k?.Kind).SequenceEqual(request.Kinds)
            || p.OverallStatus is not ("COMPLETE" or "INCOMPLETE"))
        {
            return false;
        }

        foreach (var k in p.Kinds)
        {
            if (k!.Status is null || !KindStatuses.TryGetValue(k.Status, out var status) || k.Constraints is null)
            {
                return false;
            }

            var rule = KindRules.Single(r => r.Value.Wire == k.Kind).Value;
            if (!k.Constraints.All(c => IsValidConstraint(c, rule.Comparator, rule.Unit)))
            {
                return false;
            }

            var reviewed = k.Constraints.Count(c => c!.ReviewStatus == "HUMAN_REVIEWED");
            var consistent = status switch
            {
                ConstraintKindStatus.Verified => reviewed >= 1,
                ConstraintKindStatus.Conflicting => reviewed >= 2,
                ConstraintKindStatus.Unverified => reviewed == 0 && k.Constraints.Count >= 1,
                ConstraintKindStatus.Missing => k.Constraints.Count == 0,
                _ => false,
            };
            if (!consistent)
            {
                return false;
            }
        }

        var complete = p.Kinds.All(k => k!.Status == "VERIFIED");
        return (p.OverallStatus == "COMPLETE") == complete;
    }

    private static bool IsValidConstraint(WireConstraint? c, string comparator, string unit) =>
        c is not null
        && !string.IsNullOrEmpty(c.ConstraintId)
        && c.Comparator == comparator && c.Unit == unit
        && c.Value is { } value && double.IsFinite(value)
        && c.AppliesTo is "residential" or "any"
        && c.ReviewStatus is "HUMAN_REVIEWED" or "UNREVIEWED" // REJECTED must never be returned
        && (c.ReviewStatus != "HUMAN_REVIEWED" || (!string.IsNullOrEmpty(c.ReviewerRole) && c.ReviewedAt is not null))
        && c.Conditions is not null && c.Conditions.All(x => x is not null && x.Field is not null && ConditionFields.Contains(x.Field)
            && x.Comparator is not null && ConditionComparators.Contains(x.Comparator) && x.Value is { } v && double.IsFinite(v) && !string.IsNullOrEmpty(x.Unit))
        && c.Source is { } s && s.DocumentId is not null && DocumentIdPattern().IsMatch(s.DocumentId)
        && s.Page >= 1 && s.StartChar >= 0 && s.EndChar > s.StartChar && !string.IsNullOrEmpty(s.Excerpt);

    internal sealed record WireConstraintsRequest(
        [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
        [property: JsonPropertyName("kinds")] IReadOnlyList<string> Kinds,
        [property: JsonPropertyName("building_use"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuildingUse,
        [property: JsonPropertyName("binding")] WireBinding Binding);

    internal sealed record WireBinding(
        [property: JsonPropertyName("project_id")] Guid ProjectId,
        [property: JsonPropertyName("project_revision")] int ProjectRevision,
        [property: JsonPropertyName("land_revision_id")] Guid LandRevisionId,
        [property: JsonPropertyName("requirement_revision_id")] Guid RequirementRevisionId)
    {
        public static WireBinding From(ConstraintBinding b) => new(b.ProjectId, b.ProjectRevision, b.LandRevisionId, b.RequirementRevisionId);
    }

    internal sealed record WireConstraintPack(
        [property: JsonPropertyName("contract_version")] string? ContractVersion,
        [property: JsonPropertyName("table_version")] string? TableVersion,
        [property: JsonPropertyName("synthetic")] bool? Synthetic,
        [property: JsonPropertyName("jurisdiction")] string? Jurisdiction,
        [property: JsonPropertyName("building_use")] string? BuildingUse,
        [property: JsonPropertyName("binding")] WireBinding? Binding,
        [property: JsonPropertyName("kinds")] List<WireKindResult?>? Kinds,
        [property: JsonPropertyName("overall_status")] string? OverallStatus,
        [property: JsonPropertyName("legal_status_verified")] bool? LegalStatusVerified,
        [property: JsonPropertyName("evidence_pack_id")] string? EvidencePackId,
        [property: JsonPropertyName("warnings")] List<string?>? Warnings);

    internal sealed record WireKindResult(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("constraints")] List<WireConstraint?>? Constraints);

    internal sealed record WireConstraint(
        [property: JsonPropertyName("constraint_id")] string? ConstraintId,
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("comparator")] string? Comparator,
        [property: JsonPropertyName("value")] double? Value,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("applies_to")] string? AppliesTo,
        [property: JsonPropertyName("conditions")] List<WireCondition?>? Conditions,
        [property: JsonPropertyName("review_status")] string? ReviewStatus,
        [property: JsonPropertyName("reviewer_role")] string? ReviewerRole,
        [property: JsonPropertyName("reviewed_at")] DateOnly? ReviewedAt,
        [property: JsonPropertyName("source")] WireSource? Source);

    internal sealed record WireCondition(
        [property: JsonPropertyName("field")] string? Field,
        [property: JsonPropertyName("comparator")] string? Comparator,
        [property: JsonPropertyName("value")] double? Value,
        [property: JsonPropertyName("unit")] string? Unit);

    internal sealed record WireSource(
        [property: JsonPropertyName("document_id")] string? DocumentId,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("start_char")] int StartChar,
        [property: JsonPropertyName("end_char")] int EndChar,
        [property: JsonPropertyName("excerpt")] string? Excerpt);
}
