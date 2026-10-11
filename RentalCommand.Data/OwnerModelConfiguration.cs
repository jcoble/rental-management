using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class OwnerModelConfiguration
{
    internal static void ConfigureOwners(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TaxId).HasMaxLength(64);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.OwnerEntityType).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            // The portfolio can have at most one active primary/self-owner. The partial unique index
            // is the final guard for writers that do not use the property-setup advisory lock.
            entity.HasIndex(e => new { e.PortfolioId, e.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" AND \"DeletedAt\" IS NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.OwnerEntities)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OwnerDistribution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Method).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Memo).HasMaxLength(500);
            entity.Property(e => e.RejectionReason).HasMaxLength(500);
            entity.Property(e => e.BankReference).HasMaxLength(100);
            entity.Property(e => e.ExportReference).HasMaxLength(100);
            entity.HasIndex(e => new { e.PortfolioId, e.OwnerEntityId, e.Date });
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.Date });
            entity.HasIndex(e => e.PropertyId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany()
                .HasForeignKey(e => e.OwnerEntityId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ApprovedByUser)
                .WithMany()
                .HasForeignKey(e => e.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RejectedByUser)
                .WithMany()
                .HasForeignKey(e => e.RejectedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OwnerContribution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Method).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Memo).HasMaxLength(500);
            entity.Property(e => e.RejectionReason).HasMaxLength(500);
            entity.Property(e => e.BankReference).HasMaxLength(100);
            entity.Property(e => e.ExportReference).HasMaxLength(100);
            entity.HasIndex(e => new { e.PortfolioId, e.OwnerEntityId, e.Date });
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.Date });
            entity.HasIndex(e => e.PropertyId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany()
                .HasForeignKey(e => e.OwnerEntityId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ApprovedByUser)
                .WithMany()
                .HasForeignKey(e => e.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RejectedByUser)
                .WithMany()
                .HasForeignKey(e => e.RejectedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

    }
}

