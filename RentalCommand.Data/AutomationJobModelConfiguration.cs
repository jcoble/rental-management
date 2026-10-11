using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class AutomationJobModelConfiguration
{
    internal static void ConfigureAutomationJobs(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MessageType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(300);
            entity.Property(e => e.ClaimOwner).HasMaxLength(200);
            entity.Property(e => e.Provider).HasMaxLength(80);
            entity.Property(e => e.ProviderMessageId).HasMaxLength(300);
            entity.Property(e => e.FailureKind).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.LastError).HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.NextAttemptAtUtc, e.CreatedAtUtc, e.Id })
                .HasDatabaseName("IX_OutboxMessages_Ready")
                .HasFilter("\"AcceptedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL");
            entity.HasIndex(e => e.ClaimExpiresAtUtc)
                .HasDatabaseName("IX_OutboxMessages_ExpiredClaim")
                .HasFilter("\"ClaimToken\" IS NOT NULL");
            entity.HasIndex(e => new { e.Provider, e.ProviderMessageId })
                .HasDatabaseName("IX_OutboxMessages_ProviderReceipt")
                .HasFilter("\"ProviderMessageId\" IS NOT NULL");
            // PortfolioId is optional: system/auth emails are not scoped to any portfolio.
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QueuedJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.Error).HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
        });

    }
}

