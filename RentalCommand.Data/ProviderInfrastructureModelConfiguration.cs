using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class ProviderInfrastructureModelConfiguration
{
    internal static void ConfigureProviderInfrastructure(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderInboxEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ProviderEventId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.ProviderObjectId).HasMaxLength(200);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.ClaimOwner).HasMaxLength(200);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderObjectId });
            entity.HasIndex(e => new { e.NextAttemptAtUtc, e.ReceivedAtUtc, e.Id })
                .HasFilter("\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL");
            entity.HasIndex(e => new { e.ClaimExpiresAtUtc, e.Id })
                .HasFilter("\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL AND \"ClaimToken\" IS NOT NULL");
        });

        modelBuilder.Entity<EngineWorkerHeartbeat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.WorkerName).IsRequired().HasMaxLength(120);
            entity.Property(e => e.LastErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.WorkerName).IsUnique();
        });

    }
}

