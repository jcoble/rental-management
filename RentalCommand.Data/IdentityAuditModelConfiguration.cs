using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class IdentityAuditModelConfiguration
{
    internal static void ConfigureIdentityAudit(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.TermsPrivacyVersion).HasMaxLength(100);
        });

        modelBuilder.Entity<AtomicCommandReceipt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.AttemptId).IsRequired();
            entity.Property(e => e.CommandType).IsRequired().HasMaxLength(160);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.RequestFingerprint).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.ResultContract).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.CommandType, e.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<AtomicAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CommandType).IsRequired().HasMaxLength(160);
            entity.Property(e => e.CommandIdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ActorLabel).HasMaxLength(120);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.ChangeReason).HasMaxLength(1000);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.OldValues).HasColumnType("jsonb");
            entity.Property(e => e.NewValues).HasColumnType("jsonb");
            entity.Property(e => e.Operation).HasConversion<int>();
            entity.HasIndex(e => new
            {
                e.CommandType,
                e.CommandIdempotencyKey,
                e.MutationOrdinal,
            })
                .IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Timestamp });
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });

    }
}

