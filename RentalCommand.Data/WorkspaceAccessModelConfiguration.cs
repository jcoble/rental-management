using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Authorization;

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
        ConfigureSessionRefreshCredentials(modelBuilder);
        ConfigureLoginContextSelectionChallenges(modelBuilder);
        ConfigureAccessEnvelopeProjection(modelBuilder);
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
            entity.HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
            entity.HasIndex(e => new { e.UserId, e.PortfolioId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Status });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkspaceAccessContexts_AccessRevision_Positive",
                    "\"AccessRevision\" > 0");
                table.HasCheckConstraint(
                    "CK_WorkspaceAccessContexts_StatusFacts",
                    "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
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
            entity.HasQueryFilter(e => e.AccessContext!.Portfolio!.DeletedAt == null);
            entity.HasIndex(e => e.AccessContextId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.EffectiveFromUtc });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkspaceMemberships_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_WorkspaceMemberships_StatusFacts",
                    "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
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
            entity.Property(e => e.AuthorizationTargetKind).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasData(AccessCatalog.Capabilities.Select(capability => new
            {
                capability.Id,
                capability.Key,
                capability.Description,
                capability.AuthorizationTargetKind,
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
            entity.HasQueryFilter(e => e.WorkspaceMembership!.AccessContext!.Portfolio!.DeletedAt == null);
            entity.HasIndex(e => new { e.WorkspaceMembershipId, e.Status, e.EffectiveFromUtc });
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.EffectiveFromUtc });
            entity.HasIndex(e => new { e.RoleProfileId, e.Status });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_ScopeKind",
                    "\"ScopeKind\" IN ('AllProperties', 'SelectedProperties', 'AssignedWorkOrders')");
                table.HasCheckConstraint(
                    "CK_MembershipRoleAssignments_StatusFacts",
                    "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR " +
                    "(\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
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
            entity.HasQueryFilter(e =>
                e.Property!.DeletedAt == null &&
                e.MembershipRoleAssignment!.WorkspaceMembership!.AccessContext!.Portfolio!.DeletedAt == null);
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
            entity.HasQueryFilter(e => e.ActiveAccessContext!.Portfolio!.DeletedAt == null);
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

    private static void ConfigureSessionRefreshCredentials(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthSessionRefreshTokenFamily>(entity =>
        {
            entity.HasKey(family => family.Id);
            entity.Property(family => family.Id).ValueGeneratedNever();
            entity.Property(family => family.RevocationReason).HasMaxLength(500);
            entity.HasQueryFilter(family =>
                family.AuthSession!.ActiveAccessContext!.Portfolio!.DeletedAt == null);
            entity.HasIndex(family => new { family.AuthSessionId, family.AbsoluteExpiresAtUtc });
            entity.HasIndex(family => family.ReuseDetectedAtUtc);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshTokenFamilies_Expiry",
                    "\"AbsoluteExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshTokenFamilies_RevocationFacts",
                    "(\"RevokedAtUtc\" IS NULL AND \"RevocationReason\" IS NULL) OR " +
                    "(\"RevokedAtUtc\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshTokenFamilies_ReuseRevokes",
                    "\"ReuseDetectedAtUtc\" IS NULL OR " +
                    "(\"RevokedAtUtc\" IS NOT NULL AND \"ReuseDetectedAtUtc\" >= \"CreatedAtUtc\")");
            });

            entity.HasOne(family => family.AuthSession)
                .WithMany(session => session.RefreshTokenFamilies)
                .HasForeignKey(family => family.AuthSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthSessionRefreshCredential>(entity =>
        {
            entity.HasKey(credential => credential.Id);
            entity.HasAlternateKey(credential => new { credential.Id, credential.RefreshTokenFamilyId });
            entity.Property(credential => credential.Id).ValueGeneratedNever();
            entity.Property(credential => credential.TokenHash).IsRequired().HasMaxLength(128);
            entity.Property(credential => credential.RevocationReason).HasMaxLength(500);
            entity.HasQueryFilter(credential =>
                credential.RefreshTokenFamily!.AuthSession!.ActiveAccessContext!.Portfolio!.DeletedAt == null);
            entity.HasIndex(credential => credential.TokenHash).IsUnique();
            entity.HasIndex(credential => new { credential.RefreshTokenFamilyId, credential.ExpiresAtUtc });
            entity.HasIndex(credential => credential.ReplacedByCredentialId)
                .IsUnique()
                .HasFilter("\"ReplacedByCredentialId\" IS NOT NULL");
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshCredentials_Expiry",
                    "\"ExpiresAtUtc\" > \"IssuedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshCredentials_ConsumedFacts",
                    "(\"ConsumedAtUtc\" IS NULL AND \"ConsumedByOperationId\" IS NULL) OR " +
                    "(\"ConsumedAtUtc\" IS NOT NULL AND \"ConsumedByOperationId\" IS NOT NULL AND " +
                    "\"ConsumedAtUtc\" >= \"IssuedAtUtc\")");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshCredentials_ReplacementRequiresConsumption",
                    "\"ReplacedByCredentialId\" IS NULL OR " +
                    "(\"ConsumedAtUtc\" IS NOT NULL AND \"ReplacedByCredentialId\" <> \"Id\")");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshCredentials_RevocationFacts",
                    "(\"RevokedAtUtc\" IS NULL AND \"RevocationReason\" IS NULL) OR " +
                    "(\"RevokedAtUtc\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_AuthSessionRefreshCredentials_ReuseFacts",
                    "\"ReuseDetectedAtUtc\" IS NULL OR " +
                    "(\"ConsumedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NOT NULL AND " +
                    "\"ReuseDetectedAtUtc\" >= \"ConsumedAtUtc\")");
            });

            entity.HasOne(credential => credential.RefreshTokenFamily)
                .WithMany(family => family.Credentials)
                .HasForeignKey(credential => credential.RefreshTokenFamilyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(credential => credential.ReplacedByCredential)
                .WithMany()
                .HasForeignKey(credential => new
                {
                    credential.ReplacedByCredentialId,
                    credential.RefreshTokenFamilyId,
                })
                .HasPrincipalKey(replacement => new
                {
                    replacement.Id,
                    replacement.RefreshTokenFamilyId,
                })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLoginContextSelectionChallenges(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoginContextSelectionChallenge>(entity =>
        {
            entity.HasKey(challenge => challenge.Id);
            entity.Property(challenge => challenge.Id).ValueGeneratedNever();
            entity.Property(challenge => challenge.TokenHash).IsRequired().HasMaxLength(128);
            entity.HasIndex(challenge => challenge.TokenHash).IsUnique();
            entity.HasIndex(challenge => new { challenge.UserId, challenge.ExpiresAtUtc });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LoginContextSelectionChallenges_Expiry",
                    "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_LoginContextSelectionChallenges_ConsumedFacts",
                    "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
            });

            entity.HasOne(challenge => challenge.User)
                .WithMany(user => user.LoginContextSelectionChallenges)
                .HasForeignKey(challenge => challenge.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAccessEnvelopeProjection(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccessEnvelopeProjectionRow>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_access_envelopes");
            entity.Property(row => row.EnvelopeJson).HasColumnType("text");
        });
    }
}
