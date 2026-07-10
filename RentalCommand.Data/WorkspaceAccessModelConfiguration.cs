using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class WorkspaceAccessModelConfiguration
{
    public static void ConfigureWorkspaceAccessKernel(this ModelBuilder modelBuilder)
    {
        ConfigureAccessContext(modelBuilder);
        ConfigureMembership(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureAssignments(modelBuilder);
        ConfigureAuthSession(modelBuilder);
    }

    private static void ConfigureAccessContext(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceAccessContext>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.UserId });
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.AccessRevision).IsConcurrencyToken();
            entity.Property(e => e.LastAuthorizedExperience).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(e => new { e.UserId, e.PortfolioId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Status });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkspaceAccessContexts_AccessRevision_Positive",
                    "\"AccessRevision\" > 0");
                table.HasCheckConstraint(
                    "CK_WorkspaceAccessContexts_SuspendedAt_Status",
                    "\"Status\" <> 'Suspended' OR \"SuspendedAtUtc\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_WorkspaceAccessContexts_RevokedAt_Status",
                    "\"Status\" <> 'Revoked' OR \"RevokedAtUtc\" IS NOT NULL");
            });

            entity.HasOne(e => e.User)
                .WithMany(user => user.WorkspaceAccessContexts)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Portfolio)
                .WithMany(portfolio => portfolio.AccessContexts)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMembership(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceMembership>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.DefaultExperience).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(e => e.AccessContextId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.EffectiveFromUtc });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkspaceMemberships_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_WorkspaceMemberships_SuspendedAt_Status",
                    "\"Status\" <> 'Suspended' OR \"SuspendedAtUtc\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_WorkspaceMemberships_RevokedAt_Status",
                    "\"Status\" <> 'Revoked' OR \"RevokedAtUtc\" IS NOT NULL");
            });

            entity.HasOne(e => e.AccessContext)
                .WithOne(context => context.Membership)
                .HasForeignKey<WorkspaceMembership>(e => new { e.AccessContextId, e.PortfolioId })
                .HasPrincipalKey<WorkspaceAccessContext>(context => new { context.Id, context.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoleProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Key).IsRequired().HasMaxLength(80);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.DefaultExperience).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.DefaultScopeKind).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasData(AccessCatalog.Roles.Select(role => new
            {
                role.Id,
                role.Key,
                role.DisplayName,
                role.Description,
                role.DefaultExperience,
                role.DefaultScopeKind,
            }));
        });

        modelBuilder.Entity<CapabilityDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Key).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasData(AccessCatalog.Capabilities.Select(capability => new
            {
                capability.Id,
                capability.Key,
                capability.Description,
            }));
        });

        modelBuilder.Entity<RoleProfileCapability>(entity =>
        {
            entity.HasKey(e => new { e.RoleProfileId, e.CapabilityDefinitionId });
            entity.HasOne(e => e.RoleProfile)
                .WithMany(profile => profile.Capabilities)
                .HasForeignKey(e => e.RoleProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CapabilityDefinition)
                .WithMany(capability => capability.RoleProfiles)
                .HasForeignKey(e => e.CapabilityDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasData(
                AccessCatalog.CapabilityIdsByRole.SelectMany(
                    pair => pair.Value.Select(capabilityId => new
                    {
                        RoleProfileId = pair.Key,
                        CapabilityDefinitionId = capabilityId,
                    })));
        });
    }

    private static void ConfigureAssignments(ModelBuilder modelBuilder)
    {
        // Provides the principal key for the workspace-safe property scope FK below. Property.Id
        // remains the primary key; this alternate key proves the redundant PortfolioId agrees.
        modelBuilder.Entity<Property>().HasAlternateKey(property => new { property.Id, property.PortfolioId });

        modelBuilder.Entity<MembershipRoleAssignment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.ScopeKind).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(e => new { e.WorkspaceMembershipId, e.Status, e.EffectiveFromUtc });
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.EffectiveFromUtc });
            entity.HasIndex(e => new { e.RoleProfileId, e.Status });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_SuspendedAt_Status",
                    "\"Status\" <> 'Suspended' OR \"SuspendedAtUtc\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_RevokedAt_Status",
                    "\"Status\" <> 'Revoked' OR \"RevokedAtUtc\" IS NOT NULL");
            });

            entity.HasOne(e => e.WorkspaceMembership)
                .WithMany(membership => membership.RoleAssignments)
                .HasForeignKey(e => new { e.WorkspaceMembershipId, e.PortfolioId })
                .HasPrincipalKey(membership => new { membership.Id, membership.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.RoleProfile)
                .WithMany(profile => profile.Assignments)
                .HasForeignKey(e => e.RoleProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MembershipRoleAssignmentProperty>(entity =>
        {
            entity.HasKey(e => new { e.MembershipRoleAssignmentId, e.PropertyId });
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId });
            entity.HasOne(e => e.MembershipRoleAssignment)
                .WithMany(assignment => assignment.SelectedProperties)
                .HasForeignKey(e => new { e.MembershipRoleAssignmentId, e.PortfolioId })
                .HasPrincipalKey(assignment => new { assignment.Id, assignment.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(property => new { property.Id, property.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAuthSession(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.RevocationReason).HasMaxLength(500);
            entity.HasIndex(e => new { e.UserId, e.Status, e.ExpiresAtUtc });
            entity.HasIndex(e => new { e.ActiveAccessContextId, e.Status });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_AuthSessions_ExpiresAfterCreation",
                    "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_AuthSessions_RevokedAt_Status",
                    "\"Status\" <> 'Revoked' OR \"RevokedAtUtc\" IS NOT NULL");
            });

            entity.HasOne(e => e.User)
                .WithMany(user => user.AuthSessions)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ActiveAccessContext)
                .WithMany(context => context.ActiveSessions)
                .HasForeignKey(e => new { e.ActiveAccessContextId, e.UserId })
                .HasPrincipalKey(context => new { context.Id, context.UserId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
