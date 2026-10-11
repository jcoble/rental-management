using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class EvictionModelConfiguration
{
    internal static void ConfigureEvictions(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvictionCase>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.CourtName).HasMaxLength(200);
            entity.Property(e => e.CaseNumber).HasMaxLength(100);
            entity.Property(e => e.Resolution).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseManagementId, e.Status })
                .HasDatabaseName("IX_EvictionCases_Portfolio_LeaseManagement_Status");
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId, e.Status })
                .HasDatabaseName("IX_EvictionCases_Portfolio_Property_Status");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(l => l.EvictionCases)
                .HasForeignKey(e => e.LeaseManagementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany(a => a.EvictionCases)
                .HasForeignKey(e => e.LeaseAgreementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.EvictionCases)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EvictionCaseRespondent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.EvictionCaseId, e.LeaseManagementPartyId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseManagementPartyId });
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.EvictionCase).WithMany(e => e.Respondents).HasForeignKey(e => e.EvictionCaseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.LeaseManagementParty).WithMany(p => p.EvictionCaseRespondents).HasForeignKey(e => e.LeaseManagementPartyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EvictionCaseEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasConversion<int>();
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.EvictionCaseId, e.EventDate });
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.EvictionCase)
                .WithMany(e => e.Events)
                .HasForeignKey(e => e.EvictionCaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}

