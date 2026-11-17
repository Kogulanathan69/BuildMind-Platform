namespace BuildMind.Domain.Entities;

public sealed class Project
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public required Guid OwnerId { get; init; }

    public required string Name { get; init; }

    public string? ProjectType { get; set; }

    public string? LocationReference { get; set; }

    public string Status { get; private set; } = "Created";

    public long Revision { get; private set; } = 1;

    public DateTimeOffset CreatedAtUtc { get; init; }
        = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAtUtc { get; private set; }
}