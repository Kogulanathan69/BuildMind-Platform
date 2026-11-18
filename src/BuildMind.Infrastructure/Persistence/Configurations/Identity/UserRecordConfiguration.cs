using BuildMind.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildMind.Infrastructure.Persistence.Configurations.Identity;

internal sealed class UserRecordConfiguration : IEntityTypeConfiguration<UserRecord>
{
    public void Configure(EntityTypeBuilder<UserRecord> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id).HasName("pk_users");
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(UserRecord.EmailMaxLength).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(UserRecord.EmailMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(UserRecord.PasswordHashMaxLength).IsRequired();
        builder.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(UserRecord.FullNameMaxLength);
        builder.Property(u => u.PhoneNumber).HasColumnName("phone_number").HasMaxLength(UserRecord.PhoneNumberMaxLength);
        builder.Property(u => u.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(u => u.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        // IUserRepository.GetByEmailAsync / ExistsByEmailAsync look users up by normalized email.
        builder.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
    }
}
