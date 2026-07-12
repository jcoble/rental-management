using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class ApplicationFinanceModelConfiguration
{
    internal static void ConfigureApplicationFinance(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationFinancialAccount>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.Id, row.PortfolioId });
            entity.Property(row => row.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(row => row.Currency).IsRequired().HasMaxLength(3);
            entity.Property(row => row.OpenedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.HasIndex(row => row.PublicId).IsUnique();
            entity.HasIndex(row => row.RentalApplicationId).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_ApplicationFinancialAccount_Currency",
                "\"Currency\" ~ '^[A-Z]{3}$'"));

            entity.HasOne(row => row.Portfolio)
                .WithMany()
                .HasForeignKey(row => row.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.RentalApplication)
                .WithOne(row => row.FinancialAccount)
                .HasForeignKey<ApplicationFinancialAccount>(row => new
                    { row.RentalApplicationId, row.PortfolioId })
                .HasPrincipalKey<RentalApplication>(row => new { row.Id, row.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.CreatedByUser)
                .WithMany()
                .HasForeignKey(row => row.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicationFinancialEntry>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new
                { row.Id, row.ApplicationFinancialAccountId, row.PortfolioId });
            entity.Property(row => row.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(row => row.EntryType).HasConversion<string>().HasMaxLength(30);
            entity.Property(row => row.Direction).HasConversion<string>().HasMaxLength(20);
            entity.Property(row => row.Amount).HasPrecision(18, 2);
            entity.Property(row => row.Currency).IsRequired().HasMaxLength(3);
            entity.Property(row => row.OccurredAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(row => row.Description).IsRequired().HasMaxLength(500);
            entity.Property(row => row.Method).HasMaxLength(100);
            entity.Property(row => row.Provider).HasMaxLength(50);
            entity.Property(row => row.ProviderReference).HasMaxLength(200);
            entity.Property(row => row.Source).HasConversion<string>().HasMaxLength(30);
            entity.Property(row => row.SourceReference).HasMaxLength(300);
            entity.Property(row => row.IdempotencyKey).IsRequired().HasMaxLength(200);

            entity.HasIndex(row => row.PublicId).IsUnique();
            entity.HasIndex(row => new { row.PortfolioId, row.IdempotencyKey }).IsUnique();
            entity.HasIndex(row => new { row.PortfolioId, row.Provider, row.ProviderReference })
                .IsUnique()
                .HasFilter("\"Provider\" IS NOT NULL AND \"ProviderReference\" IS NOT NULL");
            entity.HasIndex(row => new
                { row.PortfolioId, row.PropertyId, row.EffectiveOn, row.Id });
            entity.HasIndex(row => new
                { row.ApplicationFinancialAccountId, row.EffectiveOn, row.Id });

            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_ApplicationFinancialEntry_Amount", "\"Amount\" > 0");
                table.HasCheckConstraint(
                    "CK_ApplicationFinancialEntry_Currency",
                    "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_ApplicationFinancialEntry_TypeDirection",
                    "(\"EntryType\" = 'FeeCollection' AND \"Direction\" = 'Increase' AND \"RelatedEntryId\" IS NULL) OR " +
                    "(\"EntryType\" = 'Refund' AND \"Direction\" = 'Decrease' AND \"RelatedEntryId\" IS NOT NULL) OR " +
                    "(\"EntryType\" = 'Adjustment')");
            });

            entity.HasOne(row => row.Portfolio)
                .WithMany()
                .HasForeignKey(row => row.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.ApplicationFinancialAccount)
                .WithMany(row => row.Entries)
                .HasForeignKey(row => new { row.ApplicationFinancialAccountId, row.PortfolioId })
                .HasPrincipalKey(row => new { row.Id, row.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.Property)
                .WithMany()
                .HasForeignKey(row => row.PropertyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.Unit)
                .WithMany()
                .HasForeignKey(row => row.UnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.RelatedEntry)
                .WithMany(row => row.RelatedEntries)
                .HasForeignKey(row => new
                    { row.RelatedEntryId, row.ApplicationFinancialAccountId, row.PortfolioId })
                .HasPrincipalKey(row => new
                    { row.Id, row.ApplicationFinancialAccountId, row.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.CreatedByUser)
                .WithMany()
                .HasForeignKey(row => row.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
