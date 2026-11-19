using BuildMind.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildMind.API.Configuration;

/// <summary>
/// Start-up validation adapter for <see cref="ServicesOptions"/> (decision C8). Outside Development an enabled RAG
/// service requires an internal auth token (G11 MVP, fail closed).
/// </summary>
public sealed class ServicesOptionsValidation(IHostEnvironment environment) : IValidateOptions<ServicesOptions>
{
    public ValidateOptionsResult Validate(string? name, ServicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.Validate(requireRagAuthToken: !environment.IsDevelopment(), enforceSecureTransport: !environment.IsDevelopment());
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
