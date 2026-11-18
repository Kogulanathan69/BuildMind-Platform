using System.Data;
using BuildMind.Infrastructure.Persistence;
using BuildMind.Infrastructure.Persistence.Transactions;
using BuildMind.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BuildMind.Infrastructure.Tests.Persistence;

/// <summary>
/// Persistence registration in the real API host (BE-M4-D02a). No test here opens a database connection:
/// synthetic connection strings point at port 1 and connections are only inspected, never opened.
/// </summary>
public sealed class PersistenceRegistrationTests
{
    private const string Secret = DatabaseOptionsTests.Secret;

    [Fact]
    public async Task Shipped_configuration_starts_with_the_database_disabled()
    {
        await using var factory = new BuildMindApiFactory("Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Disabled_database_refuses_to_build_a_context()
    {
        await using var factory = new BuildMindApiFactory("Production");
        using var scope = factory.Services.CreateScope();

        var exception = Record.Exception(() => scope.ServiceProvider.GetRequiredService<BuildMindDbContext>());

        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(PersistenceServiceCollectionExtensions.DisabledMessage, invalid.Message);
    }

    [Fact]
    public async Task Disabled_database_ignores_an_unparseable_connection_string()
    {
        await using var factory = new BuildMindApiFactory(
            "Production",
            new Dictionary<string, string?> { ["ConnectionStrings:BuildMind"] = "not a connection string" });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Enabled_database_without_connection_string_fails_at_startup()
    {
        await using var factory = new BuildMindApiFactory(
            "Production",
            new Dictionary<string, string?> { ["Database:Enabled"] = "true" });

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = ExceptionChains.RequireStartupOptionsValidationFailure(exception, factory.Logs);
        Assert.Contains(validation.Failures, f => f.StartsWith("ConnectionStrings:BuildMind is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Root_connection_fails_at_startup_without_leaking_the_password()
    {
        await using var factory = new BuildMindApiFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Database:Enabled"] = "true",
                ["ConnectionStrings:BuildMind"] = "Server=127.0.0.1;Port=1;Database=buildmind_synthetic;User ID=root;Password=" + Secret,
            });

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = ExceptionChains.RequireStartupOptionsValidationFailure(exception, factory.Logs);
        Assert.Contains(validation.Failures, f => f.Contains("must not use the root account", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, validation.Message, StringComparison.Ordinal);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.All(factory.Logs.Entries, e => Assert.DoesNotContain(Secret, e.AllText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_database_builds_a_mysql_context_without_connecting_or_migrating()
    {
        await using var factory = new BuildMindApiFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Database:Enabled"] = "true",
                ["Database:CommandTimeoutSeconds"] = "45",
                ["ConnectionStrings:BuildMind"] = DatabaseOptionsTests.ValidConnection,
            });
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<BuildMindDbContext>();

        Assert.StartsWith("MySql", context.Database.ProviderName, StringComparison.Ordinal);
        Assert.Equal(45, context.Database.GetCommandTimeout());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.Model.GetEntityTypes());      // no feature mappings in D02a
        Assert.Empty(context.Database.GetMigrations());    // no migrations, none applied at start-up
        Assert.All(factory.Logs.Entries, e => Assert.DoesNotContain(Secret, e.AllText, StringComparison.Ordinal));
    }

    [Fact]
    public void Failure_events_that_quote_provider_messages_are_not_logged()
    {
        using var context = BuildMind.Infrastructure.Persistence.DesignTime.BuildMindDesignTimeDbContextFactory.Create(
            DatabaseOptionsTests.ValidConnection);
        var core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!;

        Assert.All(MySqlContextOptions.SuppressedFailureEvents, eventId =>
            Assert.Equal(WarningBehavior.Ignore, core.WarningsConfiguration.GetBehavior(eventId)));
        Assert.False(core.IsSensitiveDataLoggingEnabled);
    }

    [Fact]
    public async Task Context_and_transaction_helper_are_scoped()
    {
        await using var factory = new BuildMindApiFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Database:Enabled"] = "true",
                ["ConnectionStrings:BuildMind"] = DatabaseOptionsTests.ValidConnection,
            });
        _ = factory.Services;

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<BuildMindDbContext>(),
            first.ServiceProvider.GetRequiredService<BuildMindDbContext>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<BuildMindDbContext>(),
            second.ServiceProvider.GetRequiredService<BuildMindDbContext>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<TransactionRunner>(),
            second.ServiceProvider.GetRequiredService<TransactionRunner>());
    }
}
