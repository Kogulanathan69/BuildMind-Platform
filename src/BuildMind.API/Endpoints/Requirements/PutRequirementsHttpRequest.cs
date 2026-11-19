using System.Text.Json.Serialization;
using BuildMind.Application.Features.Requirements.Contracts;

namespace BuildMind.API.Endpoints.Requirements;

/// <summary>D02 presence-aware, complete snapshot transport for isolated testing. No owner field.</summary>
public sealed class PutRequirementsHttpRequest
{
    [JsonRequired] public Guid? ExpectedRevisionId { get; init; }
    [JsonRequired] public string? CatalogueVersion { get; init; }
    [JsonRequired] public int? FloorCount { get; init; }
    [JsonRequired] public RoomHttpInput?[]? GroundFloorRooms { get; init; }
    [JsonRequired] public SpaceHttpInput?[]? AdditionalSpaces { get; init; }
    [JsonRequired] public RelationshipHttpInput?[]? Relationships { get; init; }
    [JsonRequired] public string? DesignMethod { get; init; }

    public Dictionary<string, string[]> GetStructuralErrors()
    {
        var errors = new Dictionary<string, string[]>();
        void Invalid(string field) => errors[field] = ["An explicitly supplied valid value is required."];
        if (ExpectedRevisionId == Guid.Empty) Invalid("expectedRevisionId");
        if (string.IsNullOrWhiteSpace(CatalogueVersion)) Invalid("catalogueVersion");
        if (FloorCount is null) Invalid("floorCount");
        if (GroundFloorRooms is null) Invalid("groundFloorRooms");
        else for (var i = 0; i < GroundFloorRooms.Length; i++)
        {
            var item = GroundFloorRooms[i];
            if (item is null || string.IsNullOrWhiteSpace(item.RoomType) || item.Count is null) Invalid($"groundFloorRooms[{i}]");
        }
        if (AdditionalSpaces is null) Invalid("additionalSpaces");
        else for (var i = 0; i < AdditionalSpaces.Length; i++)
            if (AdditionalSpaces[i] is not { } item || string.IsNullOrWhiteSpace(item.Option)) Invalid($"additionalSpaces[{i}]");
        if (Relationships is null) Invalid("relationships");
        else for (var i = 0; i < Relationships.Length; i++)
        {
            var item = Relationships[i];
            if (item is null || string.IsNullOrWhiteSpace(item.SourceRoomType) || string.IsNullOrWhiteSpace(item.TargetRoomType)
                || string.IsNullOrWhiteSpace(item.Kind)) Invalid($"relationships[{i}]");
        }
        return errors;
    }

    public SaveRequirementsRequest ToApplicationRequest()
    {
        if (GetStructuralErrors().Count != 0) throw new InvalidOperationException("Structural validation is required.");
        return new(ExpectedRevisionId, CatalogueVersion, FloorCount,
            GroundFloorRooms!.Select(item => (RoomCountInput?)new(item!.RoomType, item.Count)).ToArray(),
            AdditionalSpaces!.Select(item => (AdditionalSpaceInput?)new(item!.Option, item.OtherText)).ToArray(),
            Relationships!.Select(item => (RoomRelationshipInput?)new(item!.SourceRoomType, item.TargetRoomType, item.Kind)).ToArray(), DesignMethod);
    }
}

public sealed class RoomHttpInput
{
    [JsonRequired] public string? RoomType { get; init; }
    [JsonRequired] public int? Count { get; init; }
}
public sealed class SpaceHttpInput
{
    [JsonRequired] public string? Option { get; init; }
    [JsonRequired] public string? OtherText { get; init; }
}
public sealed class RelationshipHttpInput
{
    [JsonRequired] public string? SourceRoomType { get; init; }
    [JsonRequired] public string? TargetRoomType { get; init; }
    [JsonRequired] public string? Kind { get; init; }
}
