using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data;

internal static class LeaseLegalArtifactModelConfiguration
{
    internal static void ConfigureLeaseLegalArtifacts(this ModelBuilder modelBuilder)
    {
        // Scoped legal FKs require these existing parents to expose portfolio-safe keys.
        modelBuilder.Entity<StoredFile>().HasAlternateKey(e => new { e.Id, e.PortfolioId });
        modelBuilder.Entity<DocumentTemplate>().HasAlternateKey(e => new { e.Id, e.PortfolioId });

        ConfigureLegalDocumentArtifact(modelBuilder);
        ConfigureLeaseAgreement(modelBuilder);
        ConfigureLeaseAgreementSigner(modelBuilder);
        ConfigureLeaseAddendum(modelBuilder);
        ConfigureLeaseAddendumSigner(modelBuilder);
        ConfigureLeaseAddendumFinancialEffect(modelBuilder);
        ConfigureLeaseRenewalAddendumDecision(modelBuilder);
    }

    private static void ConfigureLegalDocumentArtifact(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegalDocumentArtifact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });

            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ArtifactKind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.StorageKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ContentSha256).IsRequired().HasColumnType("char(64)");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.StoredFileId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.StorageKey }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.ContentSha256 });

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LegalDocumentArtifact_Kind",
                    "\"ArtifactKind\" IN ('IssuedAgreement', 'ExecutedAgreement', 'IssuedAddendum', " +
                    "'ExecutedAddendum', 'CompletionCertificate')");
                table.HasCheckConstraint("CK_LegalDocumentArtifact_ByteLength", "\"ByteLength\" > 0");
                table.HasCheckConstraint(
                    "CK_LegalDocumentArtifact_ContentSha256",
                    "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "CK_LegalDocumentArtifact_ContentType",
                    "\"ContentType\" = 'application/pdf'");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.LegalDocumentArtifacts)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => new { e.StoredFileId, e.PortfolioId })
                .HasPrincipalKey(f => new { f.Id, f.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The clean-baseline migration installs an unconditional UPDATE/DELETE rejection trigger.
        });
    }

    private static void ConfigureLeaseAgreement(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAgreement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId });

            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.VersionNumber).HasDefaultValue(1);
            entity.Property(e => e.AgreementNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangeType).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.TermType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.TermStartOn).HasColumnType("date");
            entity.Property(e => e.TermEndOn).HasColumnType("date");
            entity.Property(e => e.GoverningFromOn).HasColumnType("date");
            entity.Property(e => e.SupersededEffectiveOn).HasColumnType("date");
            entity.Property(e => e.BaseRentAmount).HasPrecision(18, 2);
            entity.Property(e => e.SecurityDepositObligation).HasPrecision(18, 2);
            entity.Property(e => e.LateFeeAmount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.TermsPayload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.VoidReasonCode).HasMaxLength(40);
            entity.Property(e => e.VoidNote).HasMaxLength(2000);
            entity.Property(e => e.DraftCancellationReason).HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.DraftRevision).HasDefaultValue(1).IsConcurrencyToken();

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.LeaseManagementId, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.AgreementNumber }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseManagementId, e.VersionNumber })
                .IsDescending(false, false, true);
            entity.HasIndex(e => new { e.PortfolioId, e.GoverningFromOn, e.TermEndOn, e.Id })
                .HasFilter("\"FullyExecutedAtUtc\" IS NOT NULL AND \"VoidedAtUtc\" IS NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.TermEndOn, e.Id })
                .HasFilter(
                    "\"FullyExecutedAtUtc\" IS NOT NULL AND \"VoidedAtUtc\" IS NULL " +
                    "AND \"TermEndOn\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.IssuedAtUtc, e.FullyExecutedAtUtc, e.Id })
                .HasFilter("\"VoidedAtUtc\" IS NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_LeaseAgreement_Version", "\"VersionNumber\" >= 1");
                table.HasCheckConstraint("CK_LeaseAgreement_DraftRevision", "\"DraftRevision\" >= 1");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_ChangeType",
                    "\"ChangeType\" IN ('Initial', 'Correction', 'Renewal', 'MonthToMonth', 'Restatement')");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Term",
                    "(\"TermType\" = 'FixedTerm' AND \"TermEndOn\" IS NOT NULL " +
                    "AND \"TermEndOn\" >= \"TermStartOn\") OR " +
                    "(\"TermType\" = 'MonthToMonth' AND \"TermEndOn\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_GoverningDate",
                    "\"GoverningFromOn\" >= \"TermStartOn\" AND " +
                    "(\"TermEndOn\" IS NULL OR \"GoverningFromOn\" <= \"TermEndOn\")");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Supersession",
                    "(\"SupersededEffectiveOn\" IS NULL AND \"SupersededByAgreementId\" IS NULL " +
                    "AND \"SupersessionRecordedAtUtc\" IS NULL) OR " +
                    "(\"SupersededEffectiveOn\" IS NOT NULL AND \"SupersededByAgreementId\" IS NOT NULL " +
                    "AND \"SupersessionRecordedAtUtc\" IS NOT NULL " +
                    "AND \"SupersededEffectiveOn\" > \"GoverningFromOn\")");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Issuance",
                    "(\"IssuedAtUtc\" IS NULL) = (\"IssuedArtifactId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Execution",
                    "((\"FullyExecutedAtUtc\" IS NULL) = (\"ExecutedArtifactId\" IS NULL)) " +
                    "AND (\"FullyExecutedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Void",
                    "((\"VoidedAtUtc\" IS NULL) = (\"VoidReasonCode\" IS NULL)) " +
                    "AND (\"VoidedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_DraftCancellation",
                    "(\"DraftCanceledAtUtc\" IS NULL) = (\"DraftCancellationReason\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_TerminalFacts",
                    "NOT (\"VoidedAtUtc\" IS NOT NULL AND \"DraftCanceledAtUtc\" IS NOT NULL) " +
                    "AND (\"DraftCanceledAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Lineage",
                    "(\"ChangeType\" = 'Initial' AND \"VersionNumber\" = 1 " +
                    "AND \"ReplacesAgreementId\" IS NULL AND \"RenewsAgreementId\" IS NULL) OR " +
                    "(\"ChangeType\" IN ('Correction', 'Restatement') " +
                    "AND \"ReplacesAgreementId\" IS NOT NULL AND \"RenewsAgreementId\" IS NULL) OR " +
                    "(\"ChangeType\" IN ('Renewal', 'MonthToMonth') " +
                    "AND \"RenewsAgreementId\" IS NOT NULL AND \"ReplacesAgreementId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Money",
                    "\"BaseRentAmount\" >= 0 AND \"SecurityDepositObligation\" >= 0 " +
                    "AND \"LateFeeAmount\" >= 0");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_RentPolicy",
                    "\"RentDueDay\" BETWEEN 1 AND 31 AND \"GracePeriodDays\" BETWEEN 0 AND 31");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_Currency",
                    "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_LeaseAgreement_SchemaVersions",
                    "\"TermsSchemaVersion\" >= 1 AND \"DocumentTemplateVersion\" >= 1");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.LeaseAgreements)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(lm => lm.Agreements)
                .HasForeignKey(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(lm => new { lm.Id, lm.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReplacesAgreement)
                .WithMany(e => e.CorrectionsAndRestatements)
                .HasForeignKey(e => new { e.ReplacesAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RenewsAgreement)
                .WithMany(e => e.Renewals)
                .HasForeignKey(e => new { e.RenewsAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SupersededByAgreement)
                .WithMany(e => e.SupersededAgreements)
                .HasForeignKey(e => new { e.SupersededByAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DocumentTemplate)
                .WithMany()
                .HasForeignKey(e => new { e.DocumentTemplateId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.IssuedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.IssuedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ExecutedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.ExecutedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The clean-baseline migration supplies the DEFERRABLE self FKs, version trigger,
            // governing-period GiST exclusion, and contractual-column immutability trigger.
        });
    }

    private static void ConfigureLeaseAgreementSigner(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAgreementSigner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.SignerRole).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.NameSnapshot).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EmailSnapshot).IsRequired().HasMaxLength(320);
            entity.Property(e => e.IsRequired).HasDefaultValue(true);

            entity.HasIndex(e => new { e.LeaseAgreementId, e.SigningOrder }).IsUnique();

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseAgreementSigner_Role",
                    "\"SignerRole\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other')");
                table.HasCheckConstraint("CK_LeaseAgreementSigner_Order", "\"SigningOrder\" > 0");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany(e => e.Signers)
                .HasForeignKey(e => new { e.LeaseAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagementParty)
                .WithMany(e => e.AgreementSignerSnapshots)
                .HasForeignKey(e => new { e.LeaseManagementPartyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Tenant)
                .WithMany(e => e.AgreementSignerSnapshots)
                .HasForeignKey(e => new { e.TenantId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);

            // The baseline migration adds UNIQUE (LeaseAgreementId, lower(EmailSnapshot)), the
            // deferred responsible-signer validator, and the post-issue child freeze trigger.
        });
    }

    private static void ConfigureLeaseAddendum(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAddendum>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.SeriesPublicId, e.LeaseManagementId, e.PortfolioId });

            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SeriesPublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.VersionNumber).HasDefaultValue(1);
            entity.Property(e => e.AddendumNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Purpose).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.EffectiveFromOn).HasColumnType("date");
            entity.Property(e => e.EffectiveThroughOn).HasColumnType("date");
            entity.Property(e => e.SupersededEffectiveOn).HasColumnType("date");
            entity.Property(e => e.TermsPayload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.VoidReasonCode).HasMaxLength(40);
            entity.Property(e => e.VoidNote).HasMaxLength(2000);
            entity.Property(e => e.DraftCancellationReason).HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.DraftRevision).HasDefaultValue(1).IsConcurrencyToken();

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.LeaseManagementId, e.SeriesPublicId, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.AddendumNumber, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => new
            {
                e.PortfolioId,
                e.LeaseManagementId,
                e.EffectiveFromOn,
                e.EffectiveThroughOn,
            });
            entity.HasIndex(e => new { e.PortfolioId, e.BaseAgreementId, e.SeriesPublicId, e.VersionNumber })
                .IsDescending(false, false, false, true);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_LeaseAddendum_Version", "\"VersionNumber\" >= 1");
                table.HasCheckConstraint("CK_LeaseAddendum_DraftRevision", "\"DraftRevision\" >= 1");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Purpose",
                    "\"Purpose\" IN ('Financial', 'Pet', 'Occupancy', 'Rules', 'Other')");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_EffectiveDates",
                    "\"EffectiveThroughOn\" IS NULL OR \"EffectiveThroughOn\" >= \"EffectiveFromOn\"");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Supersession",
                    "(\"SupersededEffectiveOn\" IS NULL AND \"SupersededByAddendumId\" IS NULL " +
                    "AND \"SupersessionRecordedAtUtc\" IS NULL) OR " +
                    "(\"SupersededEffectiveOn\" IS NOT NULL AND \"SupersededByAddendumId\" IS NOT NULL " +
                    "AND \"SupersessionRecordedAtUtc\" IS NOT NULL " +
                    "AND \"SupersededEffectiveOn\" > \"EffectiveFromOn\")");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Issuance",
                    "(\"IssuedAtUtc\" IS NULL) = (\"IssuedArtifactId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Execution",
                    "((\"FullyExecutedAtUtc\" IS NULL) = (\"ExecutedArtifactId\" IS NULL)) " +
                    "AND (\"FullyExecutedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Void",
                    "((\"VoidedAtUtc\" IS NULL) = (\"VoidReasonCode\" IS NULL)) " +
                    "AND (\"VoidedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_DraftCancellation",
                    "(\"DraftCanceledAtUtc\" IS NULL) = (\"DraftCancellationReason\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_TerminalFacts",
                    "NOT (\"VoidedAtUtc\" IS NOT NULL AND \"DraftCanceledAtUtc\" IS NOT NULL) " +
                    "AND (\"DraftCanceledAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_Lineage",
                    "(\"VersionNumber\" = 1 AND \"ReplacesAddendumId\" IS NULL) OR " +
                    "(\"VersionNumber\" > 1 AND \"ReplacesAddendumId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendum_SchemaVersions",
                    "\"TermsSchemaVersion\" >= 1 AND \"DocumentTemplateVersion\" >= 1");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.LeaseAddenda)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(e => e.Addenda)
                .HasForeignKey(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.BaseAgreement)
                .WithMany(e => e.Addenda)
                .HasForeignKey(e => new { e.BaseAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReplacesAddendum)
                .WithMany(e => e.ReplacementVersions)
                .HasForeignKey(e => new
                {
                    e.ReplacesAddendumId,
                    e.SeriesPublicId,
                    e.LeaseManagementId,
                    e.PortfolioId,
                })
                .HasPrincipalKey(e => new { e.Id, e.SeriesPublicId, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SupersededByAddendum)
                .WithMany(e => e.SupersededVersions)
                .HasForeignKey(e => new
                {
                    e.SupersededByAddendumId,
                    e.SeriesPublicId,
                    e.LeaseManagementId,
                    e.PortfolioId,
                })
                .HasPrincipalKey(e => new { e.Id, e.SeriesPublicId, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DocumentTemplate)
                .WithMany()
                .HasForeignKey(e => new { e.DocumentTemplateId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.IssuedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.IssuedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ExecutedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.ExecutedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The clean-baseline migration supplies DEFERRABLE lineage FKs, the per-series version
            // trigger, and the contractual-column immutability/monotonic-evidence trigger.
        });
    }

    private static void ConfigureLeaseAddendumSigner(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAddendumSigner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.SignerRole).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.NameSnapshot).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EmailSnapshot).IsRequired().HasMaxLength(320);
            entity.Property(e => e.IsRequired).HasDefaultValue(true);
            entity.HasIndex(e => new { e.LeaseAddendumId, e.SigningOrder }).IsUnique();

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseAddendumSigner_Role",
                    "\"SignerRole\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other')");
                table.HasCheckConstraint("CK_LeaseAddendumSigner_Order", "\"SigningOrder\" > 0");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany(e => e.Signers)
                .HasForeignKey(e => new { e.LeaseAddendumId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagementParty)
                .WithMany(e => e.AddendumSignerSnapshots)
                .HasForeignKey(e => new { e.LeaseManagementPartyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Tenant)
                .WithMany(e => e.AddendumSignerSnapshots)
                .HasForeignKey(e => new { e.TenantId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);

            // The baseline migration adds UNIQUE (LeaseAddendumId, lower(EmailSnapshot)), the
            // deferred required-signer validator, and the post-issue child freeze trigger.
        });
    }

    private static void ConfigureLeaseAddendumFinancialEffect(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAddendumFinancialEffect>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.EffectType).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.ChargeCode).IsRequired().HasMaxLength(50);
            entity.Property(e => e.EffectiveFromOn).HasColumnType("date");
            entity.Property(e => e.EffectiveThroughOn).HasColumnType("date");
            entity.Property(e => e.DueOn).HasColumnType("date");
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);

            entity.HasIndex(e => new
            {
                e.PortfolioId,
                e.LeaseAddendumId,
                e.EffectType,
                e.EffectiveFromOn,
                e.EffectiveThroughOn,
            });
            entity.HasIndex(e => new { e.PortfolioId, e.DueOn, e.Id })
                .HasFilter("\"EffectType\" = 'OneTimeCharge'");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseAddendumFinancialEffect_Shape",
                    "(\"EffectType\" = 'RecurringRentDelta' AND \"Amount\" <> 0 " +
                    "AND \"EffectiveFromOn\" IS NOT NULL AND \"DueOn\" IS NULL) OR " +
                    "(\"EffectType\" = 'OneTimeCharge' AND \"Amount\" > 0 " +
                    "AND \"DueOn\" IS NOT NULL AND \"EffectiveFromOn\" IS NULL " +
                    "AND \"EffectiveThroughOn\" IS NULL) OR " +
                    "(\"EffectType\" = 'DepositObligationDelta' AND \"Amount\" <> 0 " +
                    "AND \"EffectiveFromOn\" IS NOT NULL AND \"DueOn\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_LeaseAddendumFinancialEffect_EffectiveDates",
                    "\"EffectiveThroughOn\" IS NULL OR \"EffectiveThroughOn\" >= \"EffectiveFromOn\"");
                table.HasCheckConstraint(
                    "CK_LeaseAddendumFinancialEffect_Currency",
                    "\"Currency\" ~ '^[A-Z]{3}$'");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany(e => e.FinancialEffects)
                .HasForeignKey(e => new { e.LeaseAddendumId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);

            // The clean-baseline migration installs the post-issue mutation rejection trigger.
        });
    }

    private static void ConfigureLeaseRenewalAddendumDecision(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseRenewalAddendumDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Decision).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => new { e.RenewalAgreementId, e.SourceAddendumSeriesPublicId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseManagementId, e.RenewalAgreementId });

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_LeaseRenewalAddendumDecision_Replacement",
                    "(\"Decision\" = 'ReissueAsAddendum' AND \"ReplacementAddendumId\" IS NOT NULL) OR " +
                    "(\"Decision\" IN ('End', 'IncorporateIntoBase') " +
                    "AND \"ReplacementAddendumId\" IS NULL)");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany(e => e.RenewalAddendumDecisions)
                .HasForeignKey(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RenewalAgreement)
                .WithMany(e => e.RenewalAddendumDecisions)
                .HasForeignKey(e => new { e.RenewalAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReplacementAddendum)
                .WithMany(e => e.ReplacementDecisions)
                .HasForeignKey(e => new { e.ReplacementAddendumId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // SourceAddendumSeriesPublicId cannot target a unique key because a series has versions.
            // The baseline's deferred validator proves the source series exists in this relationship,
            // the Agreement is Renewal/MonthToMonth, and all effective source series were considered.
        });
    }
}
