namespace BuildMind.Infrastructure.Configuration;

/// <summary>
/// Configured internal service destinations (configuration section <c>Services</c>; decision C8).
/// </summary>
public sealed class ServicesOptions
{
    public const string SectionName = "Services";

    public InternalServiceOptions Python { get; set; } = new();

    public InternalServiceOptions Rag { get; set; } = new();

    /// <summary>
    /// Returns validation failures. Messages name configuration keys only and never echo configured
    /// values, which could contain credentials.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        ValidateService(Python, $"{SectionName}:Python", failures);
        ValidateService(Rag, $"{SectionName}:Rag", failures);
        return failures;
    }

    private static void ValidateService(InternalServiceOptions? service, string key, List<string> failures)
    {
        if (service is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        var hasUrl = !string.IsNullOrWhiteSpace(service.BaseUrl);

        if (service.Enabled && !hasUrl)
        {
            failures.Add($"{key}:BaseUrl is required when {key}:Enabled is true.");
        }

        if (hasUrl && !IsAcceptableBaseUrl(service.BaseUrl!))
        {
            failures.Add(
                $"{key}:BaseUrl must be an absolute http or https URL without credentials, query or fragment.");
        }

        if (service.TimeoutSeconds is < InternalServiceOptions.MinTimeoutSeconds or > InternalServiceOptions.MaxTimeoutSeconds)
        {
            failures.Add(
                $"{key}:TimeoutSeconds must be between {InternalServiceOptions.MinTimeoutSeconds} and {InternalServiceOptions.MaxTimeoutSeconds}.");
        }
    }

    private static bool IsAcceptableBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
