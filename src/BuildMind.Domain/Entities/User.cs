namespace BuildMind.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public required string Email { get; init; }

    public required string NormalizedEmail { get; init; }

    public required string PasswordHash { get; set; }

    public string? FullName { get; set; }

    public string? PhoneNumber { get; set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAtUtc { get; init; }
        = DateTimeOffset.UtcNow;

    public static User Restore(
        Guid id,
        string email,
        string normalizedEmail,
        string passwordHash,
        string? fullName,
        string? phoneNumber,
        bool isActive,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(
                "User ID cannot be empty.", nameof(id));

        return new User
        {
            Id = id,
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHash,
            FullName = fullName,
            PhoneNumber = phoneNumber,
            IsActive = isActive,
            CreatedAtUtc = createdAtUtc
        };
    }
}