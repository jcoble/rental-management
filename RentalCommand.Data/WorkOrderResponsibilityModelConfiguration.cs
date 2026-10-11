using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class WorkOrderResponsibilityModelConfiguration
{
    internal static void ConfigureWorkOrderResponsibilities(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkOrder>()
            .HasAlternateKey(workOrder => new { workOrder.Id, workOrder.PropertyId, workOrder.PortfolioId });

        modelBuilder.Entity<WorkOrderResponsibility>(entity =>
        {
            entity.HasKey(responsibility => responsibility.Id);
            entity.Property(responsibility => responsibility.Id).ValueGeneratedNever();
            entity.Property(responsibility => responsibility.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(responsibility => responsibility.AssignedReason).IsRequired().HasMaxLength(1000);
            entity.Property(responsibility => responsibility.EndedReason).HasMaxLength(1000);
            entity.HasQueryFilter(responsibility =>
                responsibility.WorkOrder!.DeletedAt == null &&
                responsibility.WorkOrder.Portfolio!.DeletedAt == null);

            entity.HasIndex(responsibility => new
                {
                    responsibility.PortfolioId,
                    responsibility.WorkOrderId,
                    responsibility.Kind,
                })
                .IsUnique()
                .HasFilter("\"EffectiveToUtc\" IS NULL AND \"Kind\" = 'Primary'")
                .HasDatabaseName("UX_WorkOrderResponsibilities_CurrentPrimary");
            entity.HasIndex(responsibility => new
                {
                    responsibility.PortfolioId,
                    responsibility.WorkOrderId,
                    responsibility.WorkspaceMembershipId,
                })
                .IsUnique()
                .HasFilter("\"EffectiveToUtc\" IS NULL")
                .HasDatabaseName("UX_WorkOrderResponsibilities_CurrentMember");
            entity.HasIndex(responsibility => new
            {
                responsibility.PortfolioId,
                responsibility.WorkspaceMembershipId,
                responsibility.EffectiveFromUtc,
                responsibility.EffectiveToUtc,
            });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkOrderResponsibilities_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_WorkOrderResponsibilities_Kind",
                    "\"Kind\" IN ('Primary', 'Supporting')");
                table.HasCheckConstraint(
                    "CK_WorkOrderResponsibilities_AssignedFacts",
                    "\"AssignedAtUtc\" = \"EffectiveFromUtc\" AND length(btrim(\"AssignedReason\")) > 0");
                table.HasCheckConstraint(
                    "CK_WorkOrderResponsibilities_EndFacts",
                    "(\"EffectiveToUtc\" IS NULL AND \"EndedAtUtc\" IS NULL AND \"EndedByUserId\" IS NULL AND \"EndedByAccessContextId\" IS NULL AND \"EndedReason\" IS NULL) OR " +
                    "(\"EffectiveToUtc\" IS NOT NULL AND \"EndedAtUtc\" = \"EffectiveToUtc\" AND \"EndedByUserId\" IS NOT NULL AND \"EndedByAccessContextId\" IS NOT NULL AND \"EndedReason\" IS NOT NULL AND length(btrim(\"EndedReason\")) > 0)");
            });

            entity.HasOne(responsibility => responsibility.WorkOrder)
                .WithMany(workOrder => workOrder.Responsibilities)
                .HasForeignKey(responsibility => new
                    { responsibility.WorkOrderId, responsibility.PropertyId, responsibility.PortfolioId })
                .HasPrincipalKey(workOrder => new { workOrder.Id, workOrder.PropertyId, workOrder.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.WorkspaceMembership)
                .WithMany(membership => membership.WorkOrderResponsibilities)
                .HasForeignKey(responsibility => new
                    { responsibility.WorkspaceMembershipId, responsibility.PortfolioId })
                .HasPrincipalKey(membership => new { membership.Id, membership.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.MembershipRoleAssignment)
                .WithMany(assignment => assignment.WorkOrderResponsibilities)
                .HasForeignKey(responsibility => new
                    {
                        responsibility.MembershipRoleAssignmentId,
                        responsibility.WorkspaceMembershipId,
                        responsibility.PortfolioId,
                    })
                .HasPrincipalKey(assignment => new
                    { assignment.Id, assignment.WorkspaceMembershipId, assignment.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.AssignedByAccessContext)
                .WithMany()
                .HasForeignKey(responsibility => new
                    {
                        responsibility.AssignedByAccessContextId,
                        responsibility.AssignedByUserId,
                        responsibility.PortfolioId,
                    })
                .HasPrincipalKey(context => new { context.Id, context.UserId, context.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.AssignedByUser)
                .WithMany()
                .HasForeignKey(responsibility => responsibility.AssignedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.EndedByAccessContext)
                .WithMany()
                .HasForeignKey(responsibility => new
                    {
                        responsibility.EndedByAccessContextId,
                        responsibility.EndedByUserId,
                        responsibility.PortfolioId,
                    })
                .HasPrincipalKey(context => new { context.Id, context.UserId, context.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(responsibility => responsibility.EndedByUser)
                .WithMany()
                .HasForeignKey(responsibility => responsibility.EndedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TechnicianWorkEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(entry => entry.Note).HasMaxLength(2000);
            entity.Property(entry => entry.Quantity).HasPrecision(12, 3);
            entity.Property(entry => entry.Unit).HasMaxLength(40);
            entity.HasIndex(entry => new
            {
                entry.PortfolioId,
                entry.WorkOrderId,
                entry.CreatedAtUtc,
                entry.Id,
            });
            entity.HasIndex(entry => new
            {
                entry.PortfolioId,
                entry.WorkspaceMembershipId,
                entry.CreatedAtUtc,
            });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TechnicianWorkEntries_KindFacts",
                    "(\"Kind\" = 'Note' AND \"Note\" IS NOT NULL AND \"Quantity\" IS NULL AND \"StoredFileId\" IS NULL) OR " +
                    "(\"Kind\" IN ('Time', 'Material') AND \"Quantity\" > 0 AND \"Unit\" IS NOT NULL AND \"StoredFileId\" IS NULL) OR " +
                    "(\"Kind\" = 'Photo' AND \"StoredFileId\" IS NOT NULL AND \"Quantity\" IS NULL)");
            });
            entity.HasQueryFilter(entry =>
                entry.WorkOrder!.DeletedAt == null &&
                entry.WorkOrder.Portfolio!.DeletedAt == null);
            entity.HasOne(entry => entry.WorkOrder)
                .WithMany(workOrder => workOrder.TechnicianEntries)
                .HasForeignKey(entry => entry.WorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.WorkOrderResponsibility)
                .WithMany(responsibility => responsibility.TechnicianEntries)
                .HasForeignKey(entry => entry.WorkOrderResponsibilityId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.WorkspaceMembership)
                .WithMany(membership => membership.TechnicianWorkEntries)
                .HasForeignKey(entry => new { entry.WorkspaceMembershipId, entry.PortfolioId })
                .HasPrincipalKey(membership => new { membership.Id, membership.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.MembershipRoleAssignment)
                .WithMany(assignment => assignment.TechnicianWorkEntries)
                .HasForeignKey(entry => new
                    { entry.MembershipRoleAssignmentId, entry.WorkspaceMembershipId, entry.PortfolioId })
                .HasPrincipalKey(assignment => new
                    { assignment.Id, assignment.WorkspaceMembershipId, assignment.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.CreatedByUser)
                .WithMany()
                .HasForeignKey(entry => entry.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.StoredFile)
                .WithMany()
                .HasForeignKey(entry => entry.StoredFileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    internal static void ConfigureWorkOrders(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Category).HasMaxLength(120);
            entity.Property(e => e.EstimatedCost).HasPrecision(18, 2);
            entity.Property(e => e.ActualCost).HasPrecision(18, 2);
            entity.Property(e => e.CreatedBy).HasMaxLength(120);
            entity.Property(e => e.TechnicianAccessInstructions).HasMaxLength(2000);
            entity.Property(e => e.SubmittedByLabel).HasMaxLength(120);
            entity.Property(e => e.RequesterName).HasMaxLength(200);
            entity.Property(e => e.RequesterPhone).HasMaxLength(64);
            entity.Property(e => e.RequesterEmail).HasMaxLength(320);
            entity.Property(e => e.EntryNotes).HasMaxLength(2000);
            entity.Property(e => e.PetWarnings).HasMaxLength(2000);
            entity.Property(e => e.AccessWarnings).HasMaxLength(2000);
            entity.Property(e => e.ChronologyRepairOriginalUpdatedAtUtc);
            entity.Property(e => e.Priority).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            // Full scan-extraction superset for work orders created from a scan draft (Postgres jsonb).
            entity.Property(e => e.ExtractedData).HasColumnType("jsonb");
            // ScheduledFor + ScheduledWindowEnd bracket the tenant-facing arrival window.
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.LeaseManagementId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.RecurringMaintenanceTaskId);
            entity.HasIndex(e => e.Priority);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.WorkOrders)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.WorkOrders)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(l => l.WorkOrders)
                .HasForeignKey(e => e.LeaseManagementId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.WorkOrders)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RecurringMaintenanceTask)
                .WithMany(t => t.WorkOrders)
                .HasForeignKey(e => e.RecurringMaintenanceTaskId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkOrderStatusEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FromStatus).HasConversion<int>();
            entity.Property(e => e.ToStatus).HasConversion<int>();
            entity.Property(e => e.Kind).HasMaxLength(32).HasDefaultValue("Status");
            entity.Property(e => e.Visibility).HasMaxLength(32).HasDefaultValue("Public");
            entity.Property(e => e.ChronologyRepairOriginalCreatedAtUtc);
            entity.Property(e => e.Note).HasMaxLength(2000);
            entity.Property(e => e.ChangedByLabel).HasMaxLength(120);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.WorkOrder)
                .WithMany(w => w.StatusEvents)
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringMaintenanceTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.Category).HasMaxLength(120);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.EstimatedCost).HasPrecision(18, 2);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.RecurrenceInterval).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Priority).HasConversion<int>();
            entity.Property(e => e.WorkerClaimOwner).HasMaxLength(200);
            entity.Property(e => e.WorkerClaimLastFailureReason).HasMaxLength(2000);
            // The worker's hot path: scan active, not-yet-deleted tasks that are due. The query filter
            // already excludes soft-deleted rows; this index serves the (active, due) scan per portfolio.
            entity.HasIndex(e => new { e.PortfolioId, e.IsActive, e.NextDueDate });
            // #9 The generation sweep is CROSS-portfolio: WHERE IsActive AND NextDueDate <= today. The
            // (PortfolioId, IsActive, NextDueDate) index above leads with a column the sweep doesn't
            // filter (a skip-scan), so add (IsActive, NextDueDate) for the sweep to range-scan directly.
            entity.HasIndex(e => new { e.IsActive, e.NextDueDate })
                  .HasDatabaseName("IX_RecurringMaintenanceTasks_Active_NextDueDate");
            entity.HasIndex(e => new { e.IsActive, e.NextDueDate, e.WorkerClaimExpiresAtUtc, e.Id })
                  .HasDatabaseName("IX_RecurringMaintenanceTasks_GenerationClaim");
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.VendorId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.RecurringMaintenanceTasks)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany()
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

    }
}
