using BuildMind.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildMind.API.Configuration;

/// <summary>Start-up validation adapter for <see cref="ServicesOptions"/> (decision C8).</summary>
public sealed class ServicesOptionsValidation : IValidateOptions<ServicesOptions>
{
    public ValidateOptionsResult Validate(string? name, ServicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
