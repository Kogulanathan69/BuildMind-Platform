using System.Data;
using BuildMind.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildMind.Persistence.IntegrationTests.Model;

/// <summary>
/// SYNTHETIC, test-only model used to prove provider behaviour. It is not a BuildMind feature schema and is
/// never registered in the application.
/// </summary>
public sealed class SpikeOwner
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Revision { get; set; } = 1;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<SpikeItem> Items { get; } = [];
}

public sealed class SpikeItem
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public decimal Amount { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public long Revision { get; set; } = 1;
}

public sealed class SpikeDbContext(DbContextOptions<SpikeDbContext> options) : DbContext(options)
{
    public DbSet<SpikeOwner> Owners => Set<SpikeOwner>();

    public DbSet<SpikeItem> Items => Set<SpikeItem>();

    public static SpikeDbContext Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<SpikeDbContext>()
            .UseMySQL(connectionString, mysql => mysql.MigrationsAssembly(typeof(SpikeDbContext).Assembly.GetName().Name))
            .EnableSensitiveDataLogging(false)
            // The spike migration is hand-written without a model snapshot.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        return new SpikeDbContext(builder.Options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => SpikeModel.Configure(modelBuilder);
}

/// <summary>The synthetic spike model, shared by the hand-written and the CLI-generated migration contexts.</summary>
public static class SpikeModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SpikeOwner>(owner =>
        {
            owner.ToTable("spike_owners");
            owner.HasKey(o => o.Id).HasName("pk_spike_owners");
            owner.Property(o => o.Id).HasColumnName("id");
            owner.Property(o => o.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            owner.Property(o => o.Revision).HasColumnName("revision").IsConcurrencyToken();
            owner.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc");
            owner.HasIndex(o => o.Name).IsUnique().HasDatabaseName("ux_spike_owners_name");
        });

        modelBuilder.Entity<SpikeItem>(item =>
        {
            item.ToTable("spike_items");
            item.HasKey(i => i.Id).HasName("pk_spike_items");
            item.Property(i => i.Id).HasColumnName("id");
            item.Property(i => i.OwnerId).HasColumnName("owner_id");
            item.Property(i => i.Amount).HasColumnName("amount").HasPrecision(18, 4);
            item.Property(i => i.PayloadJson).HasColumnName("payload_json").HasColumnType("json").IsRequired();
            item.Property(i => i.Revision).HasColumnName("revision").IsConcurrencyToken();
            item.HasIndex(i => i.OwnerId).HasDatabaseName("ix_spike_items_owner_id");
            item.HasOne<SpikeOwner>()
                .WithMany(o => o.Items)
                .HasForeignKey(i => i.OwnerId)
                .HasConstraintName("fk_spike_items_spike_owners_owner_id")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Adapts the production transaction helper's seam to the spike context.</summary>
internal sealed class SpikeTransactionSource(SpikeDbContext context) : IDbTransactionSource
{
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    public void DiscardTrackedChanges() => context.ChangeTracker.Clear();

    public Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
}
