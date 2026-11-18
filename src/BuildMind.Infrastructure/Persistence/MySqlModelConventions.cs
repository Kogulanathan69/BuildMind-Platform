using Microsoft.EntityFrameworkCore;

namespace BuildMind.Infrastructure.Persistence;

/// <summary>
/// Model-wide MySQL conventions owned by M4 (BE-M4-D02b). They are not a feature schema, only the default
/// storage of common CLR types.
/// </summary>
/// <remarks>
/// The Oracle provider maps <see cref="DateTimeOffset"/> and <see cref="DateTime"/> to <c>datetime</c> without
/// fractional seconds (observed in CI on MySQL 8.4.13, where the microseconds were silently lost). Every timestamp is
/// therefore stored as <c>datetime(6)</c>. The provider writes <see cref="DateTimeOffset"/> values as UTC
/// (observed: a +05:30 value is read back as the same instant with offset +00:00).
/// </remarks>
internal static class MySqlModelConventions
{
    public const string TimestampColumnType = "datetime(6)";

    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType(TimestampColumnType);
        configurationBuilder.Properties<DateTime>().HaveColumnType(TimestampColumnType);
    }
}
