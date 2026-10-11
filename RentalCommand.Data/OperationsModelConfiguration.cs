using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class OperationsModelConfiguration
{
    internal static void ConfigureOperations(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProspectName).HasMaxLength(200);
            entity.Property(e => e.ProspectEmail).HasMaxLength(200);
            entity.Property(e => e.AssignedTo).HasMaxLength(120);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseManagementId);
            entity.HasIndex(e => e.RentalApplicationId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.WorkOrderId)
                .IsUnique()
                .HasFilter("\"WorkOrderId\" IS NOT NULL");
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Appointments)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(l => l.Appointments)
                .HasForeignKey(e => e.LeaseManagementId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RentalApplication)
                .WithMany(a => a.Appointments)
                .HasForeignKey(e => e.RentalApplicationId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Appointments)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Inspection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Outcome).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Inspector).HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseManagementId);
            entity.HasIndex(e => e.LeaseAgreementId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledFor);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Inspections)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(l => l.Inspections)
                .HasForeignKey(e => e.LeaseManagementId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany(a => a.Inspections)
                .HasForeignKey(e => e.LeaseAgreementId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InspectionItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Area).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(300);
            entity.Property(e => e.Note).HasMaxLength(2000);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Result).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.InspectionId);
            entity.HasOne(e => e.Inspection)
                .WithMany(i => i.Items)
                .HasForeignKey(e => e.InspectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Photo + spawned work order are optional links; never cascade from those rows back here.
            entity.HasOne(e => e.PhotoStoredFile)
                .WithMany()
                .HasForeignKey(e => e.PhotoStoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.SpawnedWorkOrder)
                .WithMany()
                .HasForeignKey(e => e.SpawnedWorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InspectionTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.InspectionType).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            // PortfolioId is optional: built-in templates (if ever seeded) carry null.
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InspectionTemplateItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Area).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(300);
            entity.HasIndex(e => e.TemplateId);
            entity.HasOne(e => e.Template)
                .WithMany(t => t.Items)
                .HasForeignKey(e => e.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}

