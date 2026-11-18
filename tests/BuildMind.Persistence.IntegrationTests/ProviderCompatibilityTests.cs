using System.Text.Json;
using BuildMind.Infrastructure.Persistence.Errors;
using BuildMind.Infrastructure.Persistence.Transactions;
using BuildMind.Persistence.IntegrationTests.Model;
using BuildMind.Persistence.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.Data.MySqlClient;
using Xunit.Abstractions;

namespace BuildMind.Persistence.IntegrationTests;

/// <summary>
/// BE-M4-D02 provider compatibility spike: 20 objectives plus least-privilege and SQL-plan checks. Each test
/// runs against its own freshly created <c>buildmind_test_&lt;guid&gt;</c> database, which is dropped afterwards.
/// Observed provider facts (column types, server version) are written to the test output as evidence.
/// </summary>
public sealed class ProviderCompatibilityTests(ITestOutputHelper output)
{
    private const string Secret = "SYNTHETIC-SECRET-7f3a9c41";

    [Fact]
    public async Task T01_connects_to_a_mysql_8_4_server()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();

        var version = (string)(await ScalarAsync(database.ConnectionString, "SELECT VERSION();"))!;

        output.WriteLine($"Server version: {version}");
        Assert.StartsWith("8.4", version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T02_context_initialises_and_reaches_the_database()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);

        Assert.Equal(2, context.Model.GetEntityTypes().Count());
        Assert.True(await context.Database.CanConnectAsync());
        output.WriteLine($"Provider: {context.Database.ProviderName}");
    }

    [Fact]
    public void T03_migration_operations_and_sql_are_generated_from_the_model()
    {
        // Same pipeline as `dotnet ef migrations add` + `migrations script`: model diff → SQL. No connection needed.
        using var context = SpikeDbContext.Create("Server=127.0.0.1;Port=1;Database=buildmind_test_generation;User ID=bm_test;SslMode=Required;Password=unused");
        var model = context.GetService<IDesignTimeModel>().Model;

        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var sql = string.Join(
            Environment.NewLine,
            context.GetService<IMigrationsSqlGenerator>().Generate(operations, model).Select(c => c.CommandText));

        output.WriteLine(sql);
        Assert.Contains("spike_owners", sql, StringComparison.Ordinal);
        Assert.Contains("spike_items", sql, StringComparison.Ordinal);
        Assert.Contains("fk_spike_items_spike_owners_owner_id", sql, StringComparison.Ordinal);
        Assert.Contains("ux_spike_owners_name", sql, StringComparison.Ordinal);
        Assert.Contains("json", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T04_migration_applies_to_an_empty_database()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);

        await context.Database.MigrateAsync();

        Assert.Equal(2L, await SpikeTableCountAsync(database));
        Assert.Contains(SpikeInitialMigration.Id, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task T05_migration_rolls_back_to_an_empty_schema()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(SpikeInitialMigration.Id);
        await migrator.MigrateAsync(Migration.InitialDatabase);

        Assert.Equal(0L, await SpikeTableCountAsync(database));
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task T06_migration_reapplies_and_idempotent_script_is_safe_to_rerun()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(SpikeInitialMigration.Id);
        await migrator.MigrateAsync(Migration.InitialDatabase);
        await migrator.MigrateAsync(SpikeInitialMigration.Id);
        Assert.Equal(2L, await SpikeTableCountAsync(database));

        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        output.WriteLine(script);
        await RunScriptAsync(database.ConnectionString, script);
        await RunScriptAsync(database.ConnectionString, script);

        Assert.Equal(2L, await SpikeTableCountAsync(database));
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task T07_entities_are_inserted_and_read_back()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = NewOwner("owner-insert");

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            context.Owners.Add(owner);
            context.Items.Add(new SpikeItem { Id = Guid.NewGuid(), OwnerId = owner.Id, Amount = 1.5m, PayloadJson = "{\"a\":1}" });
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        var loaded = await reader.Owners.Include(o => o.Items).AsNoTracking().SingleAsync(o => o.Id == owner.Id);
        Assert.Equal("owner-insert", loaded.Name);
        Assert.Single(loaded.Items);
    }

    [Fact]
    public async Task T08_updates_persist_with_revision_increment()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-update");

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            var tracked = await context.Owners.SingleAsync(o => o.Id == owner.Id);
            tracked.Name = "owner-updated";
            tracked.Revision++;
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        var loaded = await reader.Owners.AsNoTracking().SingleAsync(o => o.Id == owner.Id);
        Assert.Equal(("owner-updated", 2L), (loaded.Name, loaded.Revision));
    }

    [Fact]
    public async Task T09_deletes_remove_rows()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-delete");

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            context.Owners.Remove(await context.Owners.SingleAsync(o => o.Id == owner.Id));
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.False(await reader.Owners.AnyAsync(o => o.Id == owner.Id));
    }

    [Fact]
    public async Task T10_foreign_key_rejects_orphans_and_is_classified()
    {
        await using var database = await MigratedDatabaseAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        context.Items.Add(new SpikeItem { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Amount = 1m, PayloadJson = "{}" });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var failure = PersistenceErrorClassifier.Classify(exception);
        Assert.Equal(PersistenceFailureKind.ForeignKeyViolation, failure.Kind);
        Assert.Equal("fk_spike_items_spike_owners_owner_id", failure.ConstraintName);
    }

    [Fact]
    public async Task T11_unique_constraint_rejects_duplicates()
    {
        await using var database = await MigratedDatabaseAsync();
        await SeedOwnerAsync(database, "owner-unique");
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        context.Owners.Add(NewOwner("owner-unique"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Equal(PersistenceFailureKind.DuplicateKey, PersistenceErrorClassifier.Classify(exception).Kind);
    }

    [Fact]
    public async Task T12_duplicate_translation_keeps_constraint_and_drops_values()
    {
        await using var database = await MigratedDatabaseAsync();
        await SeedOwnerAsync(database, Secret);
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        context.Owners.Add(NewOwner(Secret));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var failure = PersistenceErrorClassifier.Classify(exception);

        Assert.Equal(1062, failure.ProviderErrorNumber);
        Assert.Equal("ux_spike_owners_name", failure.ConstraintName);
        Assert.Equal("CONFLICT", failure.DefaultCode);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T13_transaction_helper_commits_multi_row_writes()
    {
        await using var database = await MigratedDatabaseAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        var owner = NewOwner("owner-commit");

        await new TransactionRunner(new SpikeTransactionSource(context)).ExecuteAsync(async token =>
        {
            context.Owners.Add(owner);
            await context.SaveChangesAsync(token);
            context.Items.Add(new SpikeItem { Id = Guid.NewGuid(), OwnerId = owner.Id, Amount = 2m, PayloadJson = "{}" });
            await context.SaveChangesAsync(token);
        }, CancellationToken.None);

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.Equal(1, await reader.Items.CountAsync(i => i.OwnerId == owner.Id));
    }

    [Fact]
    public async Task T14_transaction_helper_rolls_back_partial_writes()
    {
        await using var database = await MigratedDatabaseAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        var owner = NewOwner("owner-rollback");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransactionRunner(new SpikeTransactionSource(context)).ExecuteAsync(async token =>
            {
                context.Owners.Add(owner);
                await context.SaveChangesAsync(token);
                throw new InvalidOperationException("forced failure after first write");
            }, CancellationToken.None));

        Assert.Empty(context.ChangeTracker.Entries()); // no stale "saved" entity survives the rollback
        await context.SaveChangesAsync();              // and a later save in the same scope writes nothing
        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.False(await reader.Owners.AnyAsync(o => o.Id == owner.Id));
    }

    [Fact]
    public async Task T15_stale_revision_raises_a_concurrency_conflict()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-concurrency");
        await using var first = SpikeDbContext.Create(database.ConnectionString);
        await using var second = SpikeDbContext.Create(database.ConnectionString);
        var a = await first.Owners.SingleAsync(o => o.Id == owner.Id);
        var b = await second.Owners.SingleAsync(o => o.Id == owner.Id);

        a.Name = "first-writer";
        a.Revision++;
        await first.SaveChangesAsync();

        b.Name = "second-writer";
        b.Revision++;
        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        Assert.Equal(PersistenceFailureKind.ConcurrencyConflict, PersistenceErrorClassifier.Classify(exception).Kind);
    }

    [Fact]
    public async Task T16_conditional_revision_update_has_exactly_one_winner()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-cas");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = SpikeDbContext.Create(database.ConnectionString);
            return await context.Owners
                .Where(o => o.Id == owner.Id && o.Revision == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Revision, 2L));
        }));

        Assert.Equal(1, results.Sum());
    }

    [Fact]
    public async Task T17_guid_round_trips_and_storage_type_is_recorded()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-guid");

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.True(await reader.Owners.AnyAsync(o => o.Id == owner.Id));
        output.WriteLine($"Guid column type: {await ColumnTypeAsync(database, "spike_owners", "id")}");
    }

    [Fact]
    public async Task T18_decimal_precision_round_trips_exactly()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-decimal");
        var item = new SpikeItem { Id = Guid.NewGuid(), OwnerId = owner.Id, Amount = 12345678901234.5678m, PayloadJson = "{}" };

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            context.Items.Add(item);
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.Equal(12345678901234.5678m, (await reader.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Amount);
        output.WriteLine($"Decimal column type: {await ColumnTypeAsync(database, "spike_items", "amount")}");
    }

    [Fact]
    public async Task T19_utc_timestamps_round_trip_to_the_microsecond()
    {
        await using var database = await MigratedDatabaseAsync();
        var utc = new DateTimeOffset(2026, 11, 18, 10, 20, 30, TimeSpan.Zero).AddTicks(1_234_560);
        var offset = new DateTimeOffset(2026, 11, 18, 15, 50, 30, TimeSpan.FromHours(5.5));
        var utcOwner = NewOwner("owner-utc", utc);
        var offsetOwner = NewOwner("owner-offset", offset);

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            context.Owners.AddRange(utcOwner, offsetOwner);
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        var loadedUtc = (await reader.Owners.AsNoTracking().SingleAsync(o => o.Id == utcOwner.Id)).CreatedAtUtc;
        var loadedOffset = (await reader.Owners.AsNoTracking().SingleAsync(o => o.Id == offsetOwner.Id)).CreatedAtUtc;

        output.WriteLine($"Timestamp column type: {await ColumnTypeAsync(database, "spike_owners", "created_at_utc")}; offset value read back as {loadedOffset:O}");
        Assert.Equal(utc, loadedUtc);
        Assert.Equal(TimeSpan.Zero, loadedUtc.Offset);
        Assert.Equal(offset.UtcDateTime, loadedOffset.UtcDateTime); // the instant must never shift
    }

    [Fact]
    public async Task T20_json_round_trips_semantically()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-json");
        const string json = "{\"rooms\":[{\"id\":\"r1\",\"w\":3.25}],\"name\":\"ü-ñ\",\"flag\":true}";
        var item = new SpikeItem { Id = Guid.NewGuid(), OwnerId = owner.Id, Amount = 0m, PayloadJson = json };

        await using (var context = SpikeDbContext.Create(database.ConnectionString))
        {
            context.Items.Add(item);
            await context.SaveChangesAsync();
        }

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        var stored = (await reader.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id)).PayloadJson;

        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(stored);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal("json", await ColumnTypeAsync(database, "spike_items", "payload_json"));
        // Native JSON normalises text; geometry hashes must be computed over a canonical form, not stored bytes.
        output.WriteLine($"JSON byte-identical after round trip: {stored == json}");
    }

    [Fact]
    public async Task T21_cli_generated_migration_matches_the_model_and_applies_cleanly()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = GeneratedSpikeDbContext.Create(database.ConnectionString);

        var migrations = context.Database.GetMigrations().ToArray();
        Assert.True(
            migrations.Length > 0,
            "No CLI-generated migration found. Run `dotnet ef migrations add` for GeneratedSpikeDbContext first (CI step; see README).");
        output.WriteLine($"Generated migrations: {string.Join(", ", migrations)}");

        Assert.False(context.Database.HasPendingModelChanges()); // generated snapshot matches the model

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(2L, await SpikeTableCountAsync(database));
        // The CLI-generated schema must carry the M4 timestamp convention (microsecond precision).
        Assert.Equal("datetime(6)", await ColumnTypeAsync(database, "spike_owners", "created_at_utc"));

        var owner = NewOwner("owner-generated");
        context.Owners.Add(owner);
        await context.SaveChangesAsync();
        Assert.True(await context.Owners.AsNoTracking().AnyAsync(o => o.Id == owner.Id));

        await context.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        Assert.Equal(0L, await SpikeTableCountAsync(database));
    }

    [Fact]
    public async Task T22_connections_are_encrypted_with_tls()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        var version = await SessionStatusAsync(connection, "Ssl_version");
        var cipher = await SessionStatusAsync(connection, "Ssl_cipher");

        output.WriteLine($"TLS: {version} / {cipher}");
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.False(string.IsNullOrWhiteSpace(cipher));
    }

    [Fact]
    public async Task T23_plaintext_connections_are_refused_for_the_test_identity()
    {
        var plaintext = new MySqlConnectionStringBuilder(IntegrationEnvironment.Require(IntegrationEnvironment.TestConnectionVariable))
        {
            SslMode = MySqlSslMode.Disabled,
            Database = string.Empty,
        };

        await using var connection = new MySqlConnection(plaintext.ConnectionString);
        var refused = await Assert.ThrowsAnyAsync<MySqlException>(() => connection.OpenAsync());

        output.WriteLine($"Plaintext connection refused with provider error {refused.Number}");
    }

    [Fact]
    public async Task T24_lock_wait_timeout_keeps_the_transaction_open_and_the_runner_rolls_back_all_of_it()
    {
        await using var database = await MigratedDatabaseAsync();
        var first = await SeedOwnerAsync(database, "owner-first");
        var locked = await SeedOwnerAsync(database, "owner-locked");

        await using var blocker = new MySqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockRow = new MySqlCommand("SELECT id FROM spike_owners WHERE id = @id FOR UPDATE;", blocker, blockerTransaction))
        {
            lockRow.Parameters.AddWithValue("@id", locked.Id);
            await lockRow.ExecuteScalarAsync();
        }

        await using var context = SpikeDbContext.Create(database.ConnectionString);
        string? firstNameInsideTransaction = null;

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            new TransactionRunner(new SpikeTransactionSource(context)).ExecuteAsync(async token =>
            {
                await context.Database.ExecuteSqlRawAsync("SET SESSION innodb_lock_wait_timeout = 1;", token);
                await context.Owners.Where(o => o.Id == first.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Name, "first-changed"), token);
                try
                {
                    await context.Owners.Where(o => o.Id == locked.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.Name, "locked-changed"), token);
                }
                catch (Exception exception) when (PersistenceErrorClassifier.Classify(exception).Kind == PersistenceFailureKind.LockTimeout)
                {
                    // Only the timed-out statement was rolled back: the earlier write is still visible here.
                    firstNameInsideTransaction = await context.Owners.AsNoTracking()
                        .Where(o => o.Id == first.Id).Select(o => o.Name).SingleAsync(token);
                    throw;
                }
            }, CancellationToken.None));

        await blockerTransaction.RollbackAsync();

        var classified = PersistenceErrorClassifier.Classify(failure);
        Assert.Equal(PersistenceFailureKind.LockTimeout, classified.Kind);
        Assert.Equal(1205, classified.ProviderErrorNumber);
        Assert.False(classified.ServerRolledBackTransaction);
        Assert.Equal("first-changed", firstNameInsideTransaction);

        await using var reader = SpikeDbContext.Create(database.ConnectionString);
        Assert.Equal("owner-first", await reader.Owners.AsNoTracking().Where(o => o.Id == first.Id).Select(o => o.Name).SingleAsync());
    }

    /// <summary>
    /// Safe deployment path while the provider's idempotent script is not executable on MySQL (see T06):
    /// a reviewed, RANGED (non-idempotent) script applied once, with repeat-safety coming from the EF
    /// migration history rather than from conditional SQL.
    /// </summary>
    [Fact]
    public async Task T25_ranged_migration_script_applies_once_and_history_prevents_reapplication()
    {
        await using var database = await DisposableMySqlDatabase.CreateAsync();
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(Migration.InitialDatabase, SpikeInitialMigration.Id);
        output.WriteLine(script);
        Assert.DoesNotContain("IF NOT EXISTS(SELECT", script, StringComparison.Ordinal);

        await RunScriptAsync(database.ConnectionString, script);

        Assert.Equal(2L, await SpikeTableCountAsync(database));
        Assert.Equal(new[] { SpikeInitialMigration.Id }, await context.Database.GetAppliedMigrationsAsync());

        // Nothing is left to deploy: the next ranged script is empty and a history-tracked update is a no-op.
        var next = migrator.GenerateScript(SpikeInitialMigration.Id, null);
        Assert.DoesNotContain("CREATE TABLE", next, StringComparison.OrdinalIgnoreCase);
        await context.Database.MigrateAsync();
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2L, await SpikeTableCountAsync(database));
        Assert.Equal("datetime(6)", await ColumnTypeAsync(database, "spike_owners", "created_at_utc"));
    }

    [Fact]
    public async Task LP_runtime_identity_has_no_schema_or_grant_privileges()
    {
        // The runtime string names the schema its grants cover; the probe connects WITHOUT selecting that
        // (possibly existing) database and only reads grants and attempts a denied no-op.
        var runtime = new MySqlConnectionStringBuilder(IntegrationEnvironment.Require(IntegrationEnvironment.RuntimeConnectionVariable));
        var runtimeDatabase = runtime.Database;
        Assert.False(string.IsNullOrWhiteSpace(runtimeDatabase), "The runtime connection must name the schema its grants cover.");
        Assert.NotEqual("root", runtime.UserID, StringComparer.OrdinalIgnoreCase);
        runtime.Database = string.Empty;

        await using var connection = new MySqlConnection(runtime.ConnectionString);
        await connection.OpenAsync();
        var grants = new List<string>();
        await using (var command = new MySqlCommand("SHOW GRANTS FOR CURRENT_USER();", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                grants.Add(reader.GetString(0).ToUpperInvariant());
            }
        }

        string[] forbidden = ["ALL PRIVILEGES", "GRANT OPTION", "SUPER", "CREATE", "ALTER", "DROP", "INDEX", "REFERENCES", "FILE", "PROCESS", "SHUTDOWN"];
        Assert.All(grants, grant => Assert.DoesNotContain(forbidden, privilege => grant.Contains(privilege, StringComparison.Ordinal)));
        Assert.Contains(grants, grant => grant.Contains($"ON `{runtimeDatabase.ToUpperInvariant()}`.*", StringComparison.Ordinal));

        // Harmless probe: dropping a non-existent table still requires DROP privilege.
        await using var probe = new MySqlCommand(
            $"DROP TABLE IF EXISTS `{runtimeDatabase}`.`bm_lp_probe_{Guid.NewGuid():N}`;", connection);
        var denied = await Assert.ThrowsAsync<MySqlException>(() => probe.ExecuteNonQueryAsync());
        Assert.Equal(1142, denied.Number);
    }

    [Fact]
    public async Task SQL_owner_scoped_query_uses_its_index()
    {
        await using var database = await MigratedDatabaseAsync();
        var owner = await SeedOwnerAsync(database, "owner-plan");
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        output.WriteLine(context.Items.Where(i => i.OwnerId == owner.Id).ToQueryString());

        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("EXPLAIN SELECT id FROM spike_items WHERE owner_id = @owner;", connection);
        command.Parameters.AddWithValue("@owner", owner.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("ix_spike_items_owner_id", reader["key"] as string);
    }

    private static SpikeOwner NewOwner(string name, DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedAtUtc = createdAt ?? new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero),
    };

    private static async Task<DisposableMySqlDatabase> MigratedDatabaseAsync()
    {
        var database = await DisposableMySqlDatabase.CreateAsync();
        try
        {
            await using var context = SpikeDbContext.Create(database.ConnectionString);
            await context.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<SpikeOwner> SeedOwnerAsync(DisposableMySqlDatabase database, string name)
    {
        var owner = NewOwner(name);
        await using var context = SpikeDbContext.Create(database.ConnectionString);
        context.Owners.Add(owner);
        await context.SaveChangesAsync();
        return owner;
    }

    private static async Task<long> SpikeTableCountAsync(DisposableMySqlDatabase database) =>
        Convert.ToInt64(await ScalarAsync(
            database.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('spike_owners', 'spike_items');"));

    private static async Task<string?> ColumnTypeAsync(DisposableMySqlDatabase database, string table, string column) =>
        (await ScalarAsync(
            database.ConnectionString,
            "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c;",
            ("@t", table),
            ("@c", column)))?.ToString();

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }

    private static async Task<string?> SessionStatusAsync(MySqlConnection connection, string variable)
    {
        await using var command = new MySqlCommand($"SHOW SESSION STATUS LIKE '{variable}';", connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetString(1) : null;
    }

    private static async Task RunScriptAsync(string connectionString, string script)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await new MySqlScript(connection, script).ExecuteAsync();
    }
}
