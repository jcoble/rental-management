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
    }
}
