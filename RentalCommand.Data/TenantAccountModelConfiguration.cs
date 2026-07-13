using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data;

internal static class TenantAccountModelConfiguration
{
    internal static void ConfigureTenantAccountKernel(this ModelBuilder modelBuilder)
    {
        // The clean-baseline migration applies tenant_isolation RLS to every table configured here
        // and verifies every generated relationship remains RESTRICT/NO ACTION.
        ConfigureTenantAccount(modelBuilder);
        ConfigureConditionPeriod(modelBuilder);
        ConfigurePaymentAttempt(modelBuilder);
        ConfigureLedgerEntry(modelBuilder);
        ConfigureLedgerAllocation(modelBuilder);
        ConfigureAutopayEnrollment(modelBuilder);
        ConfigureSecurityDepositAccount(modelBuilder);
        ConfigureSecurityDepositEntry(modelBuilder);
    }

    private static void ConfigureTenantAccount(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.OpenedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.CloseReasonCode).HasMaxLength(40);
            entity.Property(e => e.CloseNote).HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => e.LeaseManagementId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.AccountNumber }).IsUnique();

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantAccount_Close",
                    "(\"ClosedAtUtc\" IS NULL AND \"CloseReasonCode\" IS NULL AND \"CloseNote\" IS NULL) OR " +
                    "(\"ClosedAtUtc\" IS NOT NULL AND \"CloseReasonCode\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantAccount_CloseAfterOpen",
                    "\"ClosedAtUtc\" IS NULL OR \"ClosedAtUtc\" >= \"OpenedAtUtc\"");
                table.HasCheckConstraint("CK_TenantAccount_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseManagement)
                .WithOne(e => e.TenantAccount)
                .HasForeignKey<TenantAccount>(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey<LeaseManagement>(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: deferred trigger equates this close fact with LeaseManagement.AccountClosedAtUtc,
            // verifies zero derived receivable/deposit balances, and freezes currency after creation.
        });
    }

    private static void ConfigureConditionPeriod(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAccountConditionPeriod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Condition).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.StartedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1000);

            entity.HasIndex(e => new { e.TenantAccountId, e.Condition })
                .IsUnique()
                .HasFilter("\"EndedAtUtc\" IS NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantAccountConditionPeriod_Condition",
                    "\"Condition\" IN ('PaymentPlan', 'Collections')");
                table.HasCheckConstraint(
                    "CK_TenantAccountConditionPeriod_EndAfterStart",
                    "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" > \"StartedAtUtc\"");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany(e => e.ConditionPeriods)
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: GiST exclusion prevents overlapping ranges per condition. A trigger
            // permits only the one-time null-to-value EndedAtUtc transition and rejects delete/other update.
        });
    }

    private static void ConfigurePaymentAttempt(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantPaymentAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ProviderObjectId).HasMaxLength(200);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AttemptType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.PaymentMethodSummary).HasMaxLength(200);
            entity.Property(e => e.PayerName).HasMaxLength(200);
            entity.Property(e => e.CheckNumber).HasMaxLength(100);
            entity.Property(e => e.BankName).HasMaxLength(200);
            entity.Property(e => e.FailureCode).HasMaxLength(100);
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.PreparedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.ClaimOwner).HasMaxLength(200);
            entity.Property(e => e.AttemptCount).HasDefaultValue(0);

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderObjectId })
                .IsUnique()
                .HasFilter("\"ProviderObjectId\" IS NOT NULL");
            entity.HasIndex(e => e.RefundsPaymentAttemptId)
                .IsUnique()
                .HasFilter("\"RefundsPaymentAttemptId\" IS NOT NULL");
            entity.HasIndex(e => new
            {
                e.PortfolioId,
                e.State,
                e.NextAttemptAtUtc,
                e.ClaimExpiresAtUtc,
                e.Id,
            });

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_Type",
                    "\"AttemptType\" IN ('Charge', 'Refund', 'Verification')");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_State",
                    "\"State\" IN ('Prepared', 'Submitted', 'Succeeded', 'Failed', 'Canceled', 'Unknown')");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_Amount",
                    "(\"AttemptType\" = 'Verification' AND \"Amount\" = 0) OR " +
                    "(\"AttemptType\" IN ('Charge','Refund') AND \"Amount\" > 0)");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_RefundProvenance",
                    "(\"AttemptType\" = 'Refund') = (\"RefundsPaymentAttemptId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_RefundTerminal",
                    "\"AttemptType\" <> 'Refund' OR \"State\" = 'Succeeded'");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_RefundPayout",
                    "\"AttemptType\" <> 'Refund' OR (\"Provider\" = 'manual' " +
                    "AND NULLIF(btrim(\"ProviderObjectId\"), '') IS NOT NULL " +
                    "AND NULLIF(btrim(\"PaymentMethodSummary\"), '') IS NOT NULL)");
                table.HasCheckConstraint("CK_TenantPaymentAttempt_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_TenantPaymentAttempt_AttemptCount", "\"AttemptCount\" >= 0");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_Claim",
                    "(\"ClaimOwner\" IS NULL AND \"ClaimToken\" IS NULL AND \"ClaimExpiresAtUtc\" IS NULL) OR " +
                    "(\"ClaimOwner\" IS NOT NULL AND \"ClaimToken\" IS NOT NULL AND \"ClaimExpiresAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_Submission",
                    "\"SubmittedAtUtc\" IS NULL OR \"SubmittedAtUtc\" >= \"PreparedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_TenantPaymentAttempt_Settlement",
                    "(\"State\" = 'Succeeded' AND \"SettledAtUtc\" IS NOT NULL " +
                    "AND \"SubmittedAtUtc\" IS NOT NULL AND \"SettledAtUtc\" >= \"SubmittedAtUtc\") OR " +
                    "(\"State\" <> 'Succeeded' AND \"SettledAtUtc\" IS NULL)");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany(e => e.PaymentAttempts)
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RefundsPaymentAttempt)
                .WithMany(e => e.RefundAttempts)
                .HasForeignKey(e => new
                {
                    e.RefundsPaymentAttemptId,
                    e.TenantAccountId,
                    e.PortfolioId,
                })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: currency/account validator, token-fenced DB-clock transition function,
            // and deferred success validator requiring one matching receipt/refund ledger entry.
        });
    }

    private static void ConfigureLedgerEntry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Direction).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.EffectiveOn).HasColumnType("date");
            entity.Property(e => e.DueOn).HasColumnType("date");
            entity.Property(e => e.PostedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.BusinessKey).IsRequired().HasMaxLength(200);

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.TenantAccountId, e.BusinessKey }).IsUnique();
            entity.HasIndex(e => e.ReversesEntryId).IsUnique().HasFilter("\"ReversesEntryId\" IS NOT NULL");
            entity.HasIndex(e => e.ProviderPaymentAttemptId)
                .IsUnique()
                .HasFilter("\"ProviderPaymentAttemptId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.TenantAccountId, e.EffectiveOn, e.Id })
                .IsDescending(false, false, true, true);
            entity.HasIndex(e => new { e.PortfolioId, e.TenantAccountId, e.DueOn, e.Id })
                .HasFilter("\"Direction\" = 'Debit'");
            entity.HasIndex(e => new { e.PortfolioId, e.EntryType, e.EffectiveOn, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.TransferPublicId, e.EntryType })
                .IsUnique()
                .HasFilter("\"TransferPublicId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseAgreementId, e.EffectiveOn })
                .HasFilter("\"LeaseAgreementId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseAddendumId, e.EffectiveOn })
                .HasFilter("\"LeaseAddendumId\" IS NOT NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_Type",
                    "\"EntryType\" IN ('OpeningBalance', 'RentCharge', 'AddendumCharge', 'LateFeeCharge', " +
                    "'DepositCharge', 'ManualCharge', 'PaymentReceipt', 'Credit', 'Adjustment', 'Refund', " +
                    "'TransferIn', 'TransferOut', 'Reversal')");
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_Direction",
                    "\"Direction\" IN ('Debit','Credit') AND ((\"EntryType\" = 'OpeningBalance') OR " +
                    "(\"EntryType\" IN ('RentCharge','AddendumCharge','LateFeeCharge','DepositCharge','ManualCharge') " +
                    "AND \"Direction\" = 'Debit') OR " +
                    "(\"EntryType\" IN ('PaymentReceipt','Credit') AND \"Direction\" = 'Credit') OR " +
                    "(\"EntryType\" = 'Refund' AND \"Direction\" = 'Debit') OR " +
                    "(\"EntryType\" IN ('Adjustment','TransferIn','TransferOut','Reversal') " +
                    "AND \"Direction\" IN ('Debit','Credit')))");
                table.HasCheckConstraint("CK_TenantLedgerEntry_Amount", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_TenantLedgerEntry_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_DueDate",
                    "(\"EntryType\" IN ('RentCharge','AddendumCharge','LateFeeCharge','DepositCharge','ManualCharge') " +
                    "AND \"DueOn\" IS NOT NULL) OR " +
                    "(\"EntryType\" IN ('PaymentReceipt','Credit','Refund','Reversal') AND \"DueOn\" IS NULL) OR " +
                    "(\"EntryType\" IN ('OpeningBalance','Adjustment','TransferIn','TransferOut'))");
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_Provenance",
                    "(\"EntryType\" <> 'RentCharge' OR \"LeaseAgreementId\" IS NOT NULL) AND " +
                    "(\"EntryType\" <> 'AddendumCharge' OR \"LeaseAddendumId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_TransferProvenance",
                    "(\"EntryType\" IN ('TransferIn','TransferOut')) = (\"TransferPublicId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantLedgerEntry_ReversalReference",
                    "(\"EntryType\" = 'Reversal') = (\"ReversesEntryId\" IS NOT NULL)");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany(e => e.LedgerEntries)
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAddendumId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReversesEntry)
                .WithMany(e => e.ReversalEntries)
                .HasForeignKey(e => new { e.ReversesEntryId, e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ProviderPaymentAttempt)
                .WithOne(e => e.LedgerEntry)
                .HasForeignKey<TenantLedgerEntry>(e => new
                {
                    e.ProviderPaymentAttemptId,
                    e.TenantAccountId,
                    e.PortfolioId,
                })
                .HasPrincipalKey<TenantPaymentAttempt>(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SourceStoredFile)
                .WithMany()
                .HasForeignKey(e => new { e.SourceStoredFileId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: immutable UPDATE/DELETE trigger; deferred account/currency/legal-
            // relationship validator; exact-opposite reversal validator; successful-attempt validator.
        });
    }

    private static void ConfigureLedgerAllocation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantLedgerAllocation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId });
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.AllocatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.BusinessKey).IsRequired().HasMaxLength(200);

            entity.HasIndex(e => new { e.TenantAccountId, e.BusinessKey }).IsUnique();
            entity.HasIndex(e => e.ReversesAllocationId)
                .IsUnique()
                .HasFilter("\"ReversesAllocationId\" IS NOT NULL");
            entity.HasIndex(e => new { e.TenantAccountId, e.DebitEntryId, e.Id });

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantLedgerAllocation_Amount",
                    "(\"ReversesAllocationId\" IS NULL AND \"Amount\" > 0) OR " +
                    "(\"ReversesAllocationId\" IS NOT NULL AND \"Amount\" < 0)");
                table.HasCheckConstraint(
                    "CK_TenantLedgerAllocation_DistinctEntries",
                    "\"DebitEntryId\" <> \"CreditEntryId\"");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany(e => e.LedgerAllocations)
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DebitEntry)
                .WithMany(e => e.DebitAllocations)
                .HasForeignKey(e => new { e.DebitEntryId, e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreditEntry)
                .WithMany(e => e.CreditAllocations)
                .HasForeignKey(e => new { e.CreditEntryId, e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReversesAllocation)
                .WithMany(e => e.ReversalAllocations)
                .HasForeignKey(e => new { e.ReversesAllocationId, e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: append-only trigger and deferred validator for entry directions,
            // exact negative reversals, and net debit/credit over-allocation.
        });
    }

    private static void ConfigureAutopayEnrollment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAutopayEnrollment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ProviderCustomerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProviderPaymentMethodId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EnrolledAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.CancelReason).HasMaxLength(500);

            entity.HasIndex(e => e.TenantAccountId)
                .IsUnique()
                .HasFilter("\"CanceledAtUtc\" IS NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_TenantAutopayEnrollment_Cancellation",
                    "(\"CanceledAtUtc\" IS NULL AND \"CancelReason\" IS NULL) OR " +
                    "(\"CanceledAtUtc\" IS NOT NULL AND \"CancelReason\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_TenantAutopayEnrollment_CancelAfterEnroll",
                    "\"CanceledAtUtc\" IS NULL OR \"CanceledAtUtc\" >= \"EnrolledAtUtc\"");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany(e => e.AutopayEnrollments)
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AuthorizingParty)
                .WithMany()
                .HasForeignKey(e => new { e.AuthorizingPartyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AuthorizationArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.AuthorizationArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: deferred validator proves the authorizing party is responsible,
            // effective at enrollment, and belongs to this account's LeaseManagement.
        });
    }

    private static void ConfigureSecurityDepositAccount(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityDepositAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.HasAlternateKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId });
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");

            entity.HasIndex(e => e.TenantAccountId).IsUnique();
            entity.ToTable(table =>
                table.HasCheckConstraint("CK_SecurityDepositAccount_Currency", "\"Currency\" ~ '^[A-Z]{3}$'"));

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithOne(e => e.SecurityDepositAccount)
                .HasForeignKey<SecurityDepositAccount>(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey<TenantAccount>(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.OriginatingAgreement)
                .WithMany()
                .HasForeignKey(e => new { e.OriginatingAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: deferred validator proves account/agreement share LeaseManagement and currency.
        });
    }

    private static void ConfigureSecurityDepositEntry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityDepositEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.SecurityDepositAccountId, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Direction).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.EffectiveOn).HasColumnType("date");
            entity.Property(e => e.PostedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.BusinessKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.PayoutExternalReference).HasMaxLength(200);

            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.SecurityDepositAccountId, e.BusinessKey }).IsUnique();
            entity.HasIndex(e => e.ReversesEntryId).IsUnique().HasFilter("\"ReversesEntryId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.SecurityDepositAccountId, e.EffectiveOn, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.TransferPublicId, e.EntryType })
                .IsUnique()
                .HasFilter("\"TransferPublicId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseAgreementId, e.EffectiveOn })
                .HasFilter("\"LeaseAgreementId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseAddendumId, e.EffectiveOn })
                .HasFilter("\"LeaseAddendumId\" IS NOT NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_SecurityDepositEntry_Type",
                    "\"EntryType\" IN ('Receipt', 'Deduction', 'Refund', 'TransferIn', 'TransferOut', " +
                    "'Adjustment', 'Reversal')");
                table.HasCheckConstraint(
                    "CK_SecurityDepositEntry_Direction",
                    "(\"EntryType\" IN ('Receipt','TransferIn') AND \"Direction\" = 'Increase') OR " +
                    "(\"EntryType\" IN ('Deduction','Refund','TransferOut') AND \"Direction\" = 'Decrease') OR " +
                    "(\"EntryType\" IN ('Adjustment','Reversal') AND \"Direction\" IN ('Increase','Decrease'))");
                table.HasCheckConstraint("CK_SecurityDepositEntry_Amount", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_SecurityDepositEntry_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_SecurityDepositEntry_TransferProvenance",
                    "(\"EntryType\" IN ('TransferIn','TransferOut')) = (\"TransferPublicId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_SecurityDepositEntry_ReversalReference",
                    "(\"EntryType\" = 'Reversal') = (\"ReversesEntryId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_SecurityDepositEntry_PayoutProvenance",
                    "\"PayoutExternalReference\" IS NULL OR \"EntryType\" = 'Refund'");
            });

            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SecurityDepositAccount)
                .WithMany(e => e.Entries)
                .HasForeignKey(e => new { e.SecurityDepositAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAddendumId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReversesEntry)
                .WithMany(e => e.ReversalEntries)
                .HasForeignKey(e => new { e.ReversesEntryId, e.SecurityDepositAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.SecurityDepositAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantLedgerEntry)
                .WithMany(e => e.SecurityDepositEntries)
                .HasForeignKey(e => new { e.TenantLedgerEntryId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SourceStoredFile)
                .WithMany()
                .HasForeignKey(e => new { e.SourceStoredFileId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Clean baseline: append-only trigger and deferred validators for currency, legal/
            // ledger relationship scope, exact-opposite reversal, and nonnegative held balance.
        });
    }
}
