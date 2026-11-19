namespace BuildMind.Infrastructure.Configuration;

/// <summary>
/// Destination and timeout for one private internal service (Python FastAPI or RAG).
/// </summary>
/// <remarks>
/// Binding these options never opens a connection. G11 (lead-approved 2026-11-19, MVP): a per-service shared-secret
/// <see cref="AuthToken"/> supplied ONLY through environment variables or user secrets (never appsettings files or
/// source). It is sent in <see cref="AuthHeaderName"/> to the validated base URL and is never logged or echoed.
/// </remarks>
public sealed class InternalServiceOptions
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Internal authentication header (G11 MVP). Not <c>Authorization</c>: user credentials are never forwarded.</summary>
    public const string AuthHeaderName = "X-BuildMind-Internal-Token";

    /// <summary>Minimum shared-secret length (generate at least 32 random printable characters).</summary>
    public const int MinAuthTokenLength = 32;

    public const int MaxAuthTokenLength = 256;

    /// <summary>When true, <see cref="BaseUrl"/> is mandatory. Disabled integrations never block startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>Absolute http/https URL without credentials, query or fragment. Optional while disabled.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Per-request timeout in seconds, between <see cref="MinTimeoutSeconds"/> and <see cref="MaxTimeoutSeconds"/>.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Shared secret for the service's internal authentication (environment variable / user secrets only), or null.
    /// Required for RAG outside Development (fail closed).
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>
    /// Outside Development a token-bearing service must use https or a loopback address, so the shared secret never
    /// crosses a network in cleartext. Setting this to true is an explicit, reviewable exception for a private network
    /// until the hosting/TLS decision (G11) is made. Default false.
    /// </summary>
    public bool AllowPlaintextPrivateNetwork { get; set; }
}
