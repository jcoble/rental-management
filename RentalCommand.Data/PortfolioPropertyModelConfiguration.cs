using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class PortfolioPropertyModelConfiguration
{
    internal static void ConfigurePortfoliosAndProperties(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Portfolio>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ManagementCompanyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TimeZone).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(8).HasDefaultValue("USD");
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Settings).HasMaxLength(10000);
            entity.Property(e => e.PublicApplicationToken).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<int>();
            // Account-wide sandbox/live flag. Defaults to false so every existing/seeded portfolio
            // (incl. the dev-admin portfolio 1) stays Live and is unaffected by the migration.
            entity.Property(e => e.IsSandbox).HasDefaultValue(false);
            entity.HasIndex(e => e.Status);
            // Resolve the public application link by token; unique + filtered so multiple null
            // tokens (portfolios not accepting applications) never collide.
            entity.HasIndex(e => e.PublicApplicationToken)
                  .IsUnique()
                  .HasFilter("\"PublicApplicationToken\" IS NOT NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<Property>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AddressLine1).IsRequired().HasMaxLength(250);
            entity.Property(e => e.AddressLine2).HasMaxLength(250);
            entity.Property(e => e.City).IsRequired().HasMaxLength(100);
            entity.Property(e => e.State).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PostalCode).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ManagementFeePercent).HasPrecision(18, 2);
            entity.Property(e => e.PurchasePrice).HasPrecision(18, 2);
            entity.Property(e => e.LandValue).HasPrecision(18, 2);
            entity.Property(e => e.ManualAnnualDepreciation).HasPrecision(18, 2);
            entity.Property(e => e.AccumulatedDepreciation).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.PropertyType).HasConversion<int>();
            entity.Property(e => e.RentalStructure).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Properties)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PropertyOwnership>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnershipSharePercent).HasPrecision(7, 4);
            entity.Property(e => e.StatementRecipientName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.StatementRecipientEmail).HasMaxLength(254);
            entity.Property(e => e.PayeeName).IsRequired().HasMaxLength(200);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_PropertyOwnerships_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_PropertyOwnerships_OwnershipSharePercent",
                    "\"OwnershipSharePercent\" > 0 AND \"OwnershipSharePercent\" <= 100");
                table.HasCheckConstraint(
                    "CK_PropertyOwnerships_StatementRecipientName",
                    "length(btrim(\"StatementRecipientName\")) > 0");
                table.HasCheckConstraint(
                    "CK_PropertyOwnerships_PayeeName",
                    "length(btrim(\"PayeeName\")) > 0");
            });
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId, e.EffectiveFromUtc });
            entity.HasIndex(e => new { e.PortfolioId, e.OwnerEntityId, e.EffectiveFromUtc })
                .HasDatabaseName("IX_PropertyOwnerships_Portfolio_OwnerEntity_EffectiveFromUtc");
            entity.HasIndex(e => new { e.PropertyId, e.OwnerEntityId, e.EffectiveToUtc })
                .IsUnique()
                .HasFilter("\"EffectiveToUtc\" IS NULL");
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.PropertyOwnerships)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Ownerships)
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(p => new { p.Id, p.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany(o => o.PropertyOwnerships)
                .HasForeignKey(e => new { e.OwnerEntityId, e.PortfolioId })
                .HasPrincipalKey(o => new { o.Id, o.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Unit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.PropertyId, e.PortfolioId });
            entity.Property(e => e.UnitNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FloorPlan).HasMaxLength(100);
            entity.Property(e => e.Bedrooms).HasPrecision(4, 1);
            entity.Property(e => e.Bathrooms).HasPrecision(4, 1);
            entity.Property(e => e.MarketRent).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.PropertyId, e.PortfolioId });
            // Unique unit number within a property — filtered to live rows so a soft-deleted unit
            // (DeletedAt set) frees its number for reuse instead of permanently occupying the slot.
            // The baseline migration owns the expression index on (PropertyId, lower(UnitNumber));
            // EF does not model PostgreSQL expression indexes.
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Units)
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(p => new { p.Id, p.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}

