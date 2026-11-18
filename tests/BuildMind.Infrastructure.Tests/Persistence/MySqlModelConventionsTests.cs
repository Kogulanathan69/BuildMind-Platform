using BuildMind.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildMind.Infrastructure.Tests.Persistence;

/// <summary>
/// M4 MySQL model conventions (BE-M4-D02b). CI on MySQL 8.4.13 showed the provider's default <c>datetime</c>
/// drops fractional seconds; every timestamp must map to <c>datetime(6)</c>. No connection is opened.
/// </summary>
public sealed class MySqlModelConventionsTests
{
    [Theory]
    [InlineData(nameof(ConventionProbe.OccurredAt))]
    [InlineData(nameof(ConventionProbe.OptionalAt))]
    [InlineData(nameof(ConventionProbe.LocalStamp))]
    public void Timestamps_are_stored_with_microsecond_precision(string property)
    {
        using var context = new ConventionProbeContext();

        var column = context.Model.FindEntityType(typeof(ConventionProbe))!.FindProperty(property)!.GetColumnType();

        Assert.Equal(MySqlModelConventions.TimestampColumnType, column);
        Assert.Equal("datetime(6)", column);
    }

    [Fact]
    public void BuildMind_context_applies_the_conventions()
    {
        var method = typeof(BuildMindDbContext).GetMethod(
            "ConfigureConventions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(typeof(BuildMindDbContext), method!.DeclaringType);
    }

    private sealed class ConventionProbe
    {
        public int Id { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public DateTimeOffset? OptionalAt { get; set; }

        public DateTime LocalStamp { get; set; }
    }

    private sealed class ConventionProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseMySQL("Server=127.0.0.1;Port=1;Database=buildmind_synthetic;User ID=bm_synthetic;SslMode=Required");

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            MySqlModelConventions.Apply(configurationBuilder);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ConventionProbe>();
    }
}
