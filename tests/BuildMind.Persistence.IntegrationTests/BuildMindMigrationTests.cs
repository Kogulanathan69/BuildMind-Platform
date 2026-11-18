using BuildMind.Infrastructure.Persistence;
using BuildMind.Persistence.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.Data.MySqlClient;
using Xunit.Abstractions;

namespace BuildMind.Persistence.IntegrationTests;

/// <summary>
/// BE-M4-D03 migration validation for the PRODUCTION <see cref="BuildMindDbContext"/> on disposable databases.
/// The reviewed migration <c>20261118133137_InitialFeatureSchema</c> is committed (STEP 06-E3); the CI tooling step
/// now only verifies it (<c>has-pending-model-changes</c>) and never regenerates it. These tests FAIL (never skip)
/// when no migration exists.
/// </summary>
public sealed class BuildMindMigrationTests(ITestOutputHelper output)
{
    private static readonly string[] Tables = ["project_requirement_heads", "projects", "requirement_revisions", "users"];

    [Fact]
    public async Task M01_migration_exists_and_matches_the_model()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);

        var migrations = context.Database.GetMigrations().ToArray();
        output.WriteLine("Migrations: " + string.Join(", ", migrations));

        Assert.Equal(new[] { "20261118133137_InitialFeatureSchema" }, migrations);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task M02_initial_migration_creates_the_reviewed_schema_and_history()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);

        await context.Database.MigrateAsync();

        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(Tables, await FeatureTablesAsync(database));

        var deleteRules = await ListAsync(database,
            "SELECT CONCAT(constraint_name, '=', delete_rule) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() ORDER BY constraint_name;");
        output.WriteLine("FK delete rules: " + string.Join(", ", deleteRules));
        Assert.Equal(
            new[]
            {
                "fk_project_requirement_heads_projects_project_id=RESTRICT",
                "fk_project_requirement_heads_requirement_revisions_current=RESTRICT",
                "fk_projects_users_owner_id=RESTRICT",
                "fk_requirement_revisions_projects_project_id=RESTRICT",
            },
            deleteRules);

        var indexes = await ListAsync(database,
            "SELECT DISTINCT CONCAT(table_name, '.', index_name, '=', non_unique) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name <> 'PRIMARY' ORDER BY 1;");
        output.WriteLine("Indexes: " + string.Join(", ", indexes));
        Assert.Contains("users.ux_users_normalized_email=0", indexes);
        Assert.Contains("requirement_revisions.ux_requirement_revisions_project_id_revision_number=0", indexes);
        Assert.Contains("projects.ix_projects_owner_id_created_at_utc=1", indexes);

        Assert.Equal("json", await ColumnTypeAsync(database, "requirement_revisions", "content_json"));
        foreach (var (table, column) in new[] { ("users", "created_at_utc"), ("projects", "created_at_utc"), ("projects", "updated_at_utc"), ("requirement_revisions", "created_at_utc") })
        {
            Assert.Equal("datetime(6)", await ColumnTypeAsync(database, table, column));
        }
    }

    [Fact]
    public async Task M03_migration_rolls_back_and_reapplies()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await FeatureTablesAsync(database));
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());

        await migrator.MigrateAsync();
        Assert.Equal(Tables, await FeatureTablesAsync(database));
    }

    [Fact]
    public async Task M04_reviewed_ranged_script_deploys_the_schema_once()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(Migration.InitialDatabase, context.Database.GetMigrations().Last());
        Assert.DoesNotContain("IF NOT EXISTS(SELECT", script, StringComparison.Ordinal); // ranged, not idempotent

        await using (var connection = new MySqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await new MySqlScript(connection, script).ExecuteAsync();
        }

        Assert.Equal(Tables, await FeatureTablesAsync(database));
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    internal static BuildMindDbContext CreateContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<BuildMindDbContext>();
        MySqlContextOptions.Apply(builder, connectionString, 60);
        return new BuildMindDbContext(builder.Options);
    }

    private static async Task<string[]> FeatureTablesAsync(DisposableMySqlDatabase database) =>
        [.. await ListAsync(database,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name <> '__EFMigrationsHistory' ORDER BY table_name;")];

    private static async Task<string?> ColumnTypeAsync(DisposableMySqlDatabase database, string table, string column) =>
        (await ListAsync(database,
            $"SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = '{table}' AND column_name = '{column}';"))
        .SingleOrDefault();

    private static async Task<List<string>> ListAsync(DisposableMySqlDatabase database, string sql)
    {
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}
