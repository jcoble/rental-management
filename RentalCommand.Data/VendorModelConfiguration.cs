using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class VendorModelConfiguration
{
    internal static void ConfigureVendors(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ServiceType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.NormalizedPhone).HasMaxLength(32);
            entity.Property(e => e.Website).HasMaxLength(500);
            entity.Property(e => e.TaxId).HasMaxLength(50);
            entity.Property(e => e.AddressLine1).HasMaxLength(250);
            entity.Property(e => e.City).HasMaxLength(120);
            entity.Property(e => e.State).HasMaxLength(60);
            entity.Property(e => e.PostalCode).HasMaxLength(20);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.NormalizedPhone);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.Property(e => e.AverageRating).HasPrecision(3, 2);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Vendors)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VendorDispatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Message).HasMaxLength(1600);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.VendorId, e.Status, e.DispatchedAtUtc });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Dispatches)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VendorRating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Comment).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.ToTable(t => t.HasCheckConstraint("CK_VendorRating_Stars", "\"Stars\" >= 1 AND \"Stars\" <= 5"));
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Ratings)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

    }
}

