using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Accounting;

/// <summary>EF mappings for the immutable general-ledger foundation.</summary>
internal static class AccountingFoundationModelConfiguration
{
    internal static void ConfigureAccountingFoundation(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Code).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.NormalBalance).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.SystemKey).HasMaxLength(100);
            entity.Property(e => e.ScheduleECategory).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Code }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.IsActive, e.Code });
            entity.HasIndex(e => new { e.PortfolioId, e.SystemKey })
                .IsUnique()
                .HasFilter("\"SystemKey\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.ParentAccountId });
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ParentAccount).WithMany(e => e.ChildAccounts)
                .HasForeignKey(e => new { e.ParentAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_LedgerAccounts_Code", "length(btrim(\"Code\")) > 0");
                table.HasCheckConstraint("CK_LedgerAccounts_Name", "length(btrim(\"Name\")) > 0");
                table.HasCheckConstraint("CK_LedgerAccounts_AccountType", "\"AccountType\" IN ('Asset', 'Liability', 'Equity', 'Income', 'Expense')");
                table.HasCheckConstraint("CK_LedgerAccounts_NormalBalance", "\"NormalBalance\" IN ('Debit', 'Credit')");
            });
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.EffectiveOn).HasColumnType("date");
            entity.Property(e => e.PostedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.SourceBusinessKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.IdempotencyDigest).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ActorLabel).HasMaxLength(120);
            entity.Property(e => e.AtomicReceiptId).IsRequired();
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.SourceType, e.SourceId, e.PostingRuleVersion })
                .IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.EffectiveOn, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.Currency, e.EffectiveOn, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.PostedAtUtc, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.SourceType, e.SourceId });
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ReversedJournalEntry).WithMany(e => e.ReversalEntries)
                .HasForeignKey(e => new { e.ReversesJournalEntryId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_JournalEntries_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_JournalEntries_SourceId", "\"SourceId\" > 0");
                table.HasCheckConstraint("CK_JournalEntries_SourceBusinessKey", "length(btrim(\"SourceBusinessKey\")) > 0");
                table.HasCheckConstraint("CK_JournalEntries_IdempotencyDigest", "length(\"IdempotencyDigest\") = 64");
                table.HasCheckConstraint("CK_JournalEntries_PostingRuleVersion", "\"PostingRuleVersion\" > 0");
                table.HasCheckConstraint("CK_JournalEntries_Description", "length(btrim(\"Description\")) > 0");
            });
        });

        modelBuilder.Entity<JournalLine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DebitAmount).HasPrecision(18, 2);
            entity.Property(e => e.CreditAmount).HasPrecision(18, 2);
            entity.Property(e => e.Memo).HasMaxLength(1000);
            entity.Property(e => e.SourceLineType).HasMaxLength(80);
            entity.HasIndex(e => new { e.JournalEntryId, e.Id });
            entity.HasIndex(e => new { e.LedgerAccountId, e.JournalEntryId });
            entity.HasOne(e => e.JournalEntry).WithMany(e => e.Lines)
                .HasForeignKey(e => e.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LedgerAccount).WithMany(e => e.JournalLines)
                .HasForeignKey(e => e.LedgerAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property).WithMany().HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit).WithMany().HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.TenantAccount).WithMany().HasForeignKey(e => e.TenantAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.OwnerEntity).WithMany().HasForeignKey(e => e.OwnerEntityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_JournalLines_Amounts",
                "\"DebitAmount\" >= 0 AND \"CreditAmount\" >= 0 AND ((\"DebitAmount\" > 0 AND \"CreditAmount\" = 0) OR (\"DebitAmount\" = 0 AND \"CreditAmount\" > 0))"));
        });

        modelBuilder.Entity<RecurringTenantCharge>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.EffectiveStartOn).HasColumnType("date");
            entity.Property(e => e.EffectiveEndOn).HasColumnType("date");
            entity.Property(e => e.NextRunDate).HasColumnType("date");
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.IsActive, e.NextRunDate, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.TenantAccountId, e.EffectiveStartOn });
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount).WithMany()
                .HasForeignKey(e => new { e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement).WithMany()
                .HasForeignKey(e => new { e.LeaseAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LedgerAccount).WithMany()
                .HasForeignKey(e => new { e.LedgerAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property).WithMany()
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit).WithMany()
                .HasForeignKey(e => new { e.UnitId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.SetNull);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_RecurringTenantCharges_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_RecurringTenantCharges_Amount", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_RecurringTenantCharges_DueDay", "\"MonthlyDueDay\" BETWEEN 1 AND 31");
                table.HasCheckConstraint("CK_RecurringTenantCharges_EffectivePeriod", "\"EffectiveEndOn\" IS NULL OR \"EffectiveEndOn\" >= \"EffectiveStartOn\"");
            });
        });

        modelBuilder.Entity<AccountingConversionReconciliation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.SourceTotal).HasPrecision(18, 2);
            entity.Property(e => e.PostedDebitTotal).HasPrecision(18, 2);
            entity.Property(e => e.PostedCreditTotal).HasPrecision(18, 2);
            entity.Property(e => e.ImbalanceAmount).HasPrecision(18, 2);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("clock_timestamp()");
            entity.HasIndex(e => new { e.PortfolioId, e.SourceType, e.Currency, e.PostingRuleVersion })
                .IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.IsApproved });
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ApprovedOpeningBalanceJournalEntry).WithMany()
                .HasForeignKey(e => new { e.ApprovedOpeningBalanceJournalEntryId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_AccountingConversionReconciliations_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_AccountingConversionReconciliations_PostingRuleVersion", "\"PostingRuleVersion\" > 0");
                table.HasCheckConstraint("CK_AccountingConversionReconciliations_Counts", "\"MissingMappingCount\" >= 0 AND \"UnsupportedSourceCount\" >= 0");
            });
        });
    }
}
