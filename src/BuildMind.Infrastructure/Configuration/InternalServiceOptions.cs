namespace BuildMind.Infrastructure.Configuration;

/// <summary>
/// Destination and timeout for one private internal service (Python FastAPI or RAG).
/// </summary>
/// <remarks>
/// Binding these options never opens a connection. Internal service authentication and transport
/// policy are pending (G11), so no credentials belong here.
/// </remarks>
public sealed class InternalServiceOptions
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>When true, <see cref="BaseUrl"/> is mandatory. Disabled integrations never block startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>Absolute http/https URL without credentials, query or fragment. Optional while disabled.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Per-request timeout in seconds, between <see cref="MinTimeoutSeconds"/> and <see cref="MaxTimeoutSeconds"/>.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
