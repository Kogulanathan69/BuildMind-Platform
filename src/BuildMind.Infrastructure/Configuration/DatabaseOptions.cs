using MySql.Data.MySqlClient;

namespace BuildMind.Infrastructure.Configuration;

/// <summary>
/// Database settings (configuration section <c>Database</c>; BE-M4-D02a). The connection string is never part
/// of this section: it is read only from <c>ConnectionStrings:BuildMind</c>, supplied through User Secrets or
/// environment variables, and never committed, logged or echoed in validation messages.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Name under <c>ConnectionStrings</c> holding the runtime (least-privilege) connection string.</summary>
    public const string ConnectionStringName = "BuildMind";

    public const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringName;

    public const int MinCommandTimeoutSeconds = 1;

    public const int MaxCommandTimeoutSeconds = 300;

    /// <summary>When false (the default) no database services can be used and no connection is ever opened.</summary>
    public bool Enabled { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Returns validation failures for these options and the supplied connection string. Messages name
    /// configuration keys only. The connection string is inspected only when the database is enabled.
    /// </summary>
    public IReadOnlyList<string> Validate(string? connectionString)
    {
        var failures = new List<string>();

        if (CommandTimeoutSeconds is < MinCommandTimeoutSeconds or > MaxCommandTimeoutSeconds)
        {
            failures.Add(
                $"{SectionName}:CommandTimeoutSeconds must be between {MinCommandTimeoutSeconds} and {MaxCommandTimeoutSeconds}.");
        }

        if (Enabled)
        {
            failures.AddRange(ValidateConnectionString(connectionString, ConnectionStringKey));
        }

        return failures;
    }

    /// <summary>
    /// Validates a MySQL connection string without echoing any part of it. Requires an explicit server,
    /// database, non-root user and TLS; rejects settings that weaken credential or file-system safety.
    /// </summary>
    public static IReadOnlyList<string> ValidateConnectionString(string? connectionString, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [$"{key} is required when {SectionName}:Enabled is true."];
        }

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            // The parser's message can quote the offending text; never propagate it.
            return [$"{key} is not a valid MySQL connection string."];
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(builder.Server))
        {
            failures.Add($"{key} must specify a server.");
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            failures.Add($"{key} must specify a database.");
        }

        if (string.IsNullOrWhiteSpace(builder.UserID))
        {
            failures.Add($"{key} must specify a user.");
        }
        else if (string.Equals(builder.UserID.Trim(), "root", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{key} must not use the root account.");
        }

        // TLS is mandatory: the connector default (Preferred) silently falls back to plaintext.
        // Production deployments should use VerifyCA/VerifyFull (server certificate validation).
        if (builder.SslMode is not (MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull))
        {
            failures.Add($"{key} must set SslMode to Required, VerifyCA or VerifyFull.");
        }

        if (builder.PersistSecurityInfo)
        {
            failures.Add($"{key} must not enable Persist Security Info.");
        }

        if (builder.AllowLoadLocalInfile)
        {
            failures.Add($"{key} must not enable Allow Load Local Infile.");
        }

        return failures;
    }
}
