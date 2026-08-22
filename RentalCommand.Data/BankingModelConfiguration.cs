using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class BankingModelConfiguration
{
    internal static void ConfigureBanking(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(40);
            entity.Property(e => e.InstitutionName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountMask).HasMaxLength(20);
            entity.Property(e => e.AccountType).HasMaxLength(80);
            entity.Property(e => e.AccountSubtype).HasMaxLength(80);
            entity.Property(e => e.ExternalItemIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.ExternalAccountIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.ExternalItemIdHash).HasMaxLength(64);
            entity.Property(e => e.ExternalAccountIdHash).HasMaxLength(64);
            entity.Property(e => e.ExternalAccessTokenCipherText).HasMaxLength(4000);
            entity.Property(e => e.SyncCursorCipherText).HasMaxLength(4000);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.PortfolioId, e.Provider, e.ExternalItemIdHash, e.ExternalAccountIdHash });
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankStatement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OpeningBalance).HasPrecision(18, 2);
            entity.Property(e => e.ClosingBalance).HasPrecision(18, 2);
            entity.Property(e => e.StatementMovement).HasPrecision(18, 2);
            entity.Property(e => e.IsoCurrencyCode).IsRequired().HasMaxLength(8);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.BankConnectionId, e.PeriodStart, e.PeriodEnd }).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.BankConnection)
                .WithMany(e => e.Statements)
                .HasForeignKey(e => new { e.BankConnectionId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaidTokenExchangeAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ClientOperationId).IsRequired().HasMaxLength(160);
            entity.Property(e => e.RequestHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.PublicTokenHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.InstitutionName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountMask).HasMaxLength(20);
            entity.Property(e => e.AccountType).HasMaxLength(80);
            entity.Property(e => e.AccountSubtype).HasMaxLength(80);
            entity.Property(e => e.ExternalAccountIdCipherText).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.ExternalAccountIdHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.Property(e => e.ProviderRequestIdentity).HasMaxLength(200);
            entity.Property(e => e.ExternalItemIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.ExternalItemIdHash).HasMaxLength(64);
            entity.Property(e => e.ExternalAccessTokenCipherText).HasMaxLength(4000);
            entity.HasIndex(e => new { e.PortfolioId, e.ClientOperationId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Status, e.PreparedAtUtc });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.BankConnection)
                .WithMany()
                .HasForeignKey(e => e.BankConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderTransactionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.MerchantName).HasMaxLength(200);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.IsoCurrencyCode).IsRequired().HasMaxLength(8);
            entity.Property(e => e.Category).HasMaxLength(200);
            entity.Property(e => e.MatchStatus).IsRequired().HasMaxLength(40);
            entity.Property(e => e.MatchConfidence).HasPrecision(5, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.RawData).HasColumnType("jsonb");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.BankConnectionId);
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId, e.MatchStatus, e.PostedAt });
            entity.HasIndex(e => e.PostedAt);
            entity.HasIndex(e => e.MatchStatus);
            entity.HasIndex(e => new { e.BankConnectionId, e.ProviderTransactionId }).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.BankConnection)
                .WithMany(c => c.Transactions)
                .HasForeignKey(e => e.BankConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => new { e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedTenantAccountId, e.MatchedTenantLedgerEntryId })
                .HasFilter("\"MatchedTenantLedgerEntryId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedTenantLedgerEntryId })
                .IsUnique()
                .HasFilter("\"MatchedTenantLedgerEntryId\" IS NOT NULL");
            entity.HasOne(e => e.MatchedTenantAccount)
                .WithMany()
                .HasForeignKey(e => new { e.MatchedTenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.MatchedTenantLedgerEntry)
                .WithMany()
                .HasForeignKey(e => new
                {
                    e.MatchedTenantLedgerEntryId,
                    e.MatchedTenantAccountId,
                    e.PortfolioId,
                })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.MatchedExpense)
                .WithMany()
                .HasForeignKey(e => e.MatchedExpenseId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedExpenseId })
                .IsUnique()
                .HasFilter("\"MatchedExpenseId\" IS NOT NULL");
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedLoanPaymentId })
                .IsUnique()
                .HasFilter("\"MatchedLoanPaymentId\" IS NOT NULL");
            entity.HasOne(e => e.MatchedLoanPayment)
                .WithMany()
                .HasForeignKey(e => e.MatchedLoanPaymentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedOwnerDistributionId })
                .IsUnique()
                .HasFilter("\"MatchedOwnerDistributionId\" IS NOT NULL");
            entity.HasOne(e => e.MatchedOwnerDistribution)
                .WithMany()
                .HasForeignKey(e => e.MatchedOwnerDistributionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.PortfolioId, e.MatchedBankTransactionId })
                .IsUnique()
                .HasFilter("\"MatchedBankTransactionId\" IS NOT NULL");
            entity.HasOne(e => e.MatchedBankTransaction)
                .WithMany()
                .HasForeignKey(e => e.MatchedBankTransactionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}

