namespace BuildMind.Application.Features.Auth.DTOs;

public sealed class AuthResponse
{
    public required string AccessToken { get; init; }

    public string TokenType { get; init; } = "Bearer";

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public required UserResponse User { get; init; }
}