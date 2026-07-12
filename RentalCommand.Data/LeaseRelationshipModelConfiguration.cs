using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data;

internal static class LeaseRelationshipModelConfiguration
{
    internal static void ConfigureLeaseRelationshipKernel(this ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        ConfigureLeaseManagement(modelBuilder);
        ConfigureLeaseManagementParty(modelBuilder);
        ConfigureTenantUserAccess(modelBuilder);
        ConfigureUnitOperationalPeriod(modelBuilder);
    }

    private static void ConfigureLeaseManagement(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseManagement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.UnitId, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.UnitId, e.PropertyId, e.PortfolioId });

            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.RelationshipNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PossessionAgreementExceptionReason).HasMaxLength(1000);
            entity.Property(e => e.CancellationReasonCode).HasMaxLength(40);
            entity.Property(e => e.CancellationNote).HasMaxLength(2000);
            entity.Property(e => e.EndingDisposition)
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(LeaseManagementEndingDisposition.Undecided);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.RowVersion)
                .HasDefaultValueSql("gen_random_uuid()")
                .IsConcurrencyToken();

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.RelationshipNumber }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.UnitId, e.CreatedAtUtc, e.Id })
                .IsDescending(false, false, true, true);
            entity.HasIndex(e => new { e.PortfolioId, e.PlannedPossessionAtUtc, e.Id })
                .HasFilter("\"PossessionGivenAtUtc\" IS NULL AND \"CanceledAtUtc\" IS NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.PossessionReturnedAtUtc, e.AccountClosedAtUtc, e.Id });

            // Npgsql has no fluent EF mapping for PostgreSQL exclusion constraints. The clean
            // baseline migration adds the Unit possession GiST exclusion defined by the schema contract.

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseManagement_PossessionNotCanceled",
                    "\"PossessionGivenAtUtc\" IS NULL OR \"CanceledAtUtc\" IS NULL");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_ReturnRequiresPossession",
                    "\"PossessionReturnedAtUtc\" IS NULL OR \"PossessionGivenAtUtc\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_ReturnAfterPossession",
                    "\"PossessionReturnedAtUtc\" IS NULL OR \"PossessionReturnedAtUtc\" >= \"PossessionGivenAtUtc\"");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_PossessionExceptionPair",
                    "((\"PossessionAgreementExceptionReason\" IS NULL) = " +
                    "(\"PossessionAgreementExceptionAuthorizedByUserId\" IS NULL)) AND " +
                    "(\"PossessionAgreementExceptionReason\" IS NULL OR \"PossessionGivenAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_AccountCloseRequiresReturn",
                    "\"AccountClosedAtUtc\" IS NULL OR \"PossessionReturnedAtUtc\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_AccountCloseAfterReturn",
                    "\"AccountClosedAtUtc\" IS NULL OR \"AccountClosedAtUtc\" >= \"PossessionReturnedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_CancellationPair",
                    "(\"CanceledAtUtc\" IS NULL) = (\"CancellationReasonCode\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseManagement_EndingDisposition",
                    "(\"EndingDisposition\" = 'Undecided' AND \"EndingDispositionDecidedAtUtc\" IS NULL " +
                    "AND \"EndingDispositionDecidedByUserId\" IS NULL) OR " +
                    "(\"EndingDisposition\" IN ('OfferRenewal', 'OfferMonthToMonth', 'NonRenewalMoveOut') " +
                    "AND \"EndingDispositionDecidedAtUtc\" IS NOT NULL " +
                    "AND \"EndingDispositionDecidedByUserId\" IS NOT NULL)");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.LeaseManagements)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.LeaseManagements)
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(p => new { p.Id, p.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.LeaseManagements)
                .HasForeignKey(e => new { e.UnitId, e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(u => new { u.Id, u.PropertyId, u.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.PossessionAgreementExceptionAuthorizedByUser)
                .WithMany()
                .HasForeignKey(e => e.PossessionAgreementExceptionAuthorizedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.EndingDispositionDecidedByUser)
                .WithMany()
                .HasForeignKey(e => e.EndingDispositionDecidedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLeaseManagementParty(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseManagementParty>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.EffectiveFrom).HasColumnType("date");
            entity.Property(e => e.EffectiveThrough).HasColumnType("date");
            entity.Property(e => e.GuarantorLegalNoticeEligible).HasDefaultValue(false);
            entity.Property(e => e.ChangeReason).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => new { e.PortfolioId, e.TenantId, e.EffectiveFrom, e.Id })
                .IsDescending(false, false, true, true);
            entity.HasIndex(e => new
            {
                e.PortfolioId,
                e.LeaseManagementId,
                e.EffectiveFrom,
                e.EffectiveThrough,
                e.Role,
            });

            // The membership and single-primary daterange GiST exclusions are emitted as explicit
            // PostgreSQL in the clean baseline migration; all supporting scalar indexes live here.

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseManagementParty_EffectiveDates",
                    "\"EffectiveThrough\" IS NULL OR \"EffectiveThrough\" >= \"EffectiveFrom\"");
                table.HasCheckConstraint(
                    "CK_LeaseManagementParty_Role",
                    "\"Role\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Occupant')");
                table.HasCheckConstraint(
                    "CK_LeaseManagementParty_GuarantorNoticeEligibility",
                    "\"Role\" = 'Guarantor' OR \"GuarantorLegalNoticeEligible\" = false");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.LeaseManagementParties)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(lm => lm.Parties)
                .HasForeignKey(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(lm => new { lm.Id, lm.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.LeaseManagementParties)
                .HasForeignKey(e => new { e.TenantId, e.PortfolioId })
                .HasPrincipalKey(t => new { t.Id, t.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureTenantUserAccess(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantUserAccess>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.GrantedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.ApplicationUserId, e.RevokedAtUtc, e.Id });
            entity.HasIndex(e => new { e.ApplicationUserId, e.LeaseManagementPartyId })
                .IsUnique()
                .HasFilter("\"RevokedAtUtc\" IS NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantUserAccess_RevocationPair",
                    "(\"RevokedAtUtc\" IS NULL) = (\"RevokedByUserId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_TenantUserAccess_RevocationAfterGrant",
                    "\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"GrantedAtUtc\"");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.TenantUserAccesses)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ApplicationUser)
                .WithMany()
                .HasForeignKey(e => e.ApplicationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagementParty)
                .WithMany(p => p.UserAccesses)
                .HasForeignKey(e => new { e.LeaseManagementPartyId, e.PortfolioId })
                .HasPrincipalKey(p => new { p.Id, p.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.GrantedByUser)
                .WithMany()
                .HasForeignKey(e => e.GrantedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RevokedByUser)
                .WithMany()
                .HasForeignKey(e => e.RevokedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureUnitOperationalPeriod(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnitOperationalPeriod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => new { e.PortfolioId, e.UnitId, e.StartedAtUtc, e.Id });
            entity.HasIndex(e => new { e.UnitId, e.Type })
                .IsUnique()
                .HasFilter("\"EndedAtUtc\" IS NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_UnitOperationalPeriod_EndAfterStart",
                    "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" > \"StartedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_UnitOperationalPeriod_Type",
                    "\"Type\" IN ('Turnover', 'OutOfService', 'ManagementHold')");
                table.HasCheckConstraint(
                    "CK_UnitOperationalPeriod_TurnoverSource",
                    "\"Type\" <> 'Turnover' OR \"SourceLeaseManagementId\" IS NOT NULL");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.UnitOperationalPeriods)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.UnitOperationalPeriods)
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(p => new { p.Id, p.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.OperationalPeriods)
                .HasForeignKey(e => new { e.UnitId, e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(u => new { u.Id, u.PropertyId, u.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SourceLeaseManagement)
                .WithMany(lm => lm.SourceOperationalPeriods)
                .HasForeignKey(e => new { e.SourceLeaseManagementId, e.UnitId, e.PortfolioId })
                .HasPrincipalKey(lm => new { lm.Id, lm.UnitId, lm.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
