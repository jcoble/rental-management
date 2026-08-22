using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class ExpenseAssetModelConfiguration
{
    internal static void ConfigureExpensesAndAssets(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.OperationalScope).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Category).HasConversion<int>();
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Subtotal).HasPrecision(18, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(18, 2);
            entity.Property(e => e.ReceiptData).HasColumnType("jsonb");
            // Promoted scalar receipt fields (alongside the ReceiptData jsonb superset).
            entity.Property(e => e.PaymentMethod).HasMaxLength(100);
            entity.Property(e => e.CardLast4).HasMaxLength(20);
            entity.Property(e => e.DocumentKind).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.CapitalizedAssetId);
            entity.HasIndex(e => new { e.RecurringExpenseId, e.RecurringExpenseOccurrenceDate })
                .IsUnique()
                .HasFilter("\"RecurringExpenseId\" IS NOT NULL AND \"RecurringExpenseOccurrenceDate\" IS NOT NULL")
                .HasDatabaseName("UX_Expenses_RecurringExpense_Occurrence");
            entity.HasIndex(e => e.Status);
            // #2 Every financial report + the grid Expense date-range filter buckets expenses by
            // IncurredAt (accrual) and PaidAt (cash-basis / Schedule E), portfolio-scoped.
            entity.HasIndex(e => new { e.PortfolioId, e.IncurredAt })
                  .HasDatabaseName("IX_Expenses_Portfolio_IncurredAt");
            entity.HasIndex(e => new { e.PortfolioId, e.PaidAt })
                  .HasDatabaseName("IX_Expenses_Portfolio_PaidAt");
            // #3 Schedule-E + property P&L group by Category within a property over a period.
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId, e.Category, e.IncurredAt })
                  .HasDatabaseName("IX_Expenses_Portfolio_Property_Category_IncurredAt");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            // Optional link to the specific unit a (possibly non-maintenance) expense belongs to.
            // Units have no inverse Expenses collection; scope is enforced through the owning property.
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Expenses)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.WorkOrder)
                .WithMany(w => w.Expenses)
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.CapitalizedAsset)
                .WithMany()
                .HasForeignKey(e => e.CapitalizedAssetId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RecurringExpense)
                .WithMany()
                .HasForeignKey(e => e.RecurringExpenseId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.LineItems)
                .WithOne(li => li.Expense)
                .HasForeignKey(li => li.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Allocations)
                .WithOne(allocation => allocation.Expense)
                .HasForeignKey(allocation => new { allocation.ExpenseId, allocation.PortfolioId })
                .HasPrincipalKey(expense => new { expense.Id, expense.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Expenses_OperationalScope",
                """
                ("OperationalScope" = 'Portfolio' AND "PropertyId" IS NULL AND "UnitId" IS NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'Property' AND "PropertyId" IS NOT NULL AND "UnitId" IS NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'Unit' AND "PropertyId" IS NOT NULL AND "UnitId" IS NOT NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'WorkOrder' AND "PropertyId" IS NOT NULL AND "WorkOrderId" IS NOT NULL)
                """));
        });

        modelBuilder.Entity<ExpenseAllocation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TargetKind).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => new { e.ExpenseId, e.PortfolioId });
            entity.HasIndex(e => new { e.PropertyId, e.PortfolioId });
            entity.HasIndex(e => new { e.UnitId, e.PortfolioId });
            entity.HasIndex(e => new { e.OwnerEntityId, e.PortfolioId });
            entity.HasIndex(e => new { e.PortfolioId, e.ExpenseId });
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId });
            entity.HasIndex(e => new { e.PortfolioId, e.UnitId });
            entity.HasIndex(e => new { e.PortfolioId, e.OwnerEntityId });
            entity.HasOne(e => e.Expense)
                .WithMany(expense => expense.Allocations)
                .HasForeignKey(e => new { e.ExpenseId, e.PortfolioId })
                .HasPrincipalKey(expense => new { expense.Id, expense.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => new { Id = e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(property => new { property.Id, property.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => new { Id = e.UnitId, e.PortfolioId })
                .HasPrincipalKey(unit => new { unit.Id, unit.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany()
                .HasForeignKey(e => new { Id = e.OwnerEntityId, e.PortfolioId })
                .HasPrincipalKey(owner => new { owner.Id, owner.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_ExpenseAllocations_PositiveAmount", "\"Amount\" > 0");
                table.HasCheckConstraint(
                    "CK_ExpenseAllocations_TypedTarget",
                    """
                    ("TargetKind" = 'Property' AND "PropertyId" IS NOT NULL AND "UnitId" IS NULL AND "OwnerEntityId" IS NULL)
                    OR ("TargetKind" = 'Unit' AND "PropertyId" IS NULL AND "UnitId" IS NOT NULL AND "OwnerEntityId" IS NULL)
                    OR ("TargetKind" = 'OwnerEntity' AND "PropertyId" IS NULL AND "UnitId" IS NULL AND "OwnerEntityId" IS NOT NULL)
                    """);
            });
        });

        modelBuilder.Entity<ExpenseLineItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Quantity).HasPrecision(18, 2);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.ExpenseId);
        });

        modelBuilder.Entity<CapitalAsset>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CostBasis).HasPrecision(18, 2);
            entity.Property(e => e.Method).HasConversion<int>();
            entity.Property(e => e.RecoveryYears).HasPrecision(9, 2);
            entity.Property(e => e.Convention).HasConversion<int>();
            entity.Property(e => e.AccumulatedDepreciation).HasPrecision(18, 2);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.SourceExpenseId);
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId, e.InServiceDate })
                  .HasDatabaseName("IX_CapitalAssets_Portfolio_Property_InServiceDate");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.CapitalAssets)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.SourceExpense)
                .WithMany()
                .HasForeignKey(e => e.SourceExpenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PropertyDisposition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SalePrice).HasPrecision(18, 2);
            entity.Property(e => e.SellingCosts).HasPrecision(18, 2);
            entity.Property(e => e.BuyerName).HasMaxLength(200);
            entity.Property(e => e.Memo).HasMaxLength(1000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.ClosedOnDate);
            entity.HasIndex(e => new { e.PortfolioId, e.PropertyId })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("IX_PropertyDispositions_Portfolio_Property_Active");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Dispositions)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Loan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Lender).IsRequired().HasMaxLength(200);
            entity.Property(e => e.OriginalAmount).HasPrecision(18, 2);
            entity.Property(e => e.CurrentBalance).HasPrecision(18, 2);
            entity.Property(e => e.AnnualInterestRatePct).HasPrecision(9, 4);
            entity.Property(e => e.MonthlyPrincipalInterest).HasPrecision(18, 2);
            entity.Property(e => e.MonthlyEscrow).HasPrecision(18, 2);
            entity.Property(e => e.DebtServiceAutomationStartDate);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.WorkerClaimOwner).HasMaxLength(200);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.Status, e.WorkerClaimExpiresAtUtc, e.StartDate, e.Id })
                .HasDatabaseName("IX_Loans_DebtServiceClaim");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LoanPayment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeriodKey).IsRequired().HasMaxLength(7);
            entity.Property(e => e.InterestAmount).HasPrecision(18, 2);
            entity.Property(e => e.PrincipalAmount).HasPrecision(18, 2);
            entity.Property(e => e.EscrowAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.BalanceAfter).HasPrecision(18, 2);
            entity.Property(e => e.Status).HasConversion<int>();
            // Durable occurrence fence: different command receipts still cannot create the same
            // amortization period twice.
            entity.HasIndex(e => new { e.LoanId, e.PeriodKey }).IsUnique();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LoanId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Loan)
                .WithMany(l => l.Payments)
                .HasForeignKey(e => e.LoanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LoanPaymentCorrection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InterestAmount).HasPrecision(18, 2);
            entity.Property(e => e.PrincipalAmount).HasPrecision(18, 2);
            entity.Property(e => e.EscrowAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.BalanceAfter).HasPrecision(18, 2);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => new { e.LoanPaymentId, e.AttemptId }).IsUnique();
            entity.HasIndex(e => new { e.LoanPaymentId, e.Id });
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.LoanPayment)
                .WithMany(e => e.Corrections)
                .HasForeignKey(e => e.LoanPaymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringExpense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Category).HasConversion<int>();
            entity.Property(e => e.Frequency).HasConversion<int>();
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.WorkerClaimOwner).HasMaxLength(200);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            // The worker scans active, due templates across all portfolios — index the due predicate.
            entity.HasIndex(e => new { e.Active, e.NextRunDate, e.WorkerClaimExpiresAtUtc, e.Id })
                .HasDatabaseName("IX_RecurringExpenses_GenerationClaim");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

    }
}

