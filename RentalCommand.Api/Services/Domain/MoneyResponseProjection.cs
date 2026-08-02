using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// SQL-translatable response projections shared by money reads and atomic receipt snapshots.
/// Keeping the wire shape in the query prevents list/detail callers from materializing entities
/// and rebuilding related values in memory.
/// </summary>
internal static class MoneyResponseProjection
{
    public static IQueryable<ExpenseResponse> ExpenseDetails(
        IQueryable<Expense> expenses,
        IQueryable<StoredFile> files,
        IQueryable<JournalEntry> journals)
    {
        return expenses.Select(expense => new ExpenseResponse
        {
            Id = expense.Id,
            PortfolioId = expense.PortfolioId,
            OperationalScope = expense.OperationalScope,
            PropertyId = expense.PropertyId,
            UnitId = expense.UnitId,
            VendorId = expense.VendorId,
            WorkOrderId = expense.WorkOrderId,
            CapitalizedAssetId = expense.CapitalizedAssetId,
            Category = expense.Category,
            Description = expense.Description,
            Status = expense.Status,
            Amount = expense.Amount,
            IncurredAt = expense.IncurredAt,
            DueDate = expense.DueDate,
            PaidAt = expense.PaidAt,
            BillableToOwner = expense.BillableToOwner,
            Notes = expense.Notes,
            CreatedAt = expense.CreatedAt,
            UpdatedAt = expense.UpdatedAt,
            Subtotal = expense.Subtotal,
            TaxAmount = expense.TaxAmount,
            ReceiptData = expense.ReceiptData,
            PaymentMethod = expense.PaymentMethod,
            CardLast4 = expense.CardLast4,
            DocumentKind = expense.DocumentKind,
            PropertyName = expense.Property == null ? null : expense.Property.Name,
            UnitNumber = expense.Unit == null ? null : expense.Unit.UnitNumber,
            VendorName = expense.Vendor == null ? null : expense.Vendor.Name,
            AccountId = journals
                .Where(entry => entry.PortfolioId == expense.PortfolioId && entry.SourceId == expense.Id &&
                    (entry.SourceType == JournalSourceType.BillIncurred ||
                     entry.SourceType == JournalSourceType.ExpensePayment))
                .OrderBy(entry => entry.SourceType == JournalSourceType.BillIncurred ? 0 : 1)
                .ThenBy(entry => entry.Id)
                .SelectMany(entry => entry.Lines)
                .Where(line => line.LedgerAccount!.AccountType == AccountType.Expense)
                .OrderBy(line => line.LedgerAccount!.Code)
                .Select(line => (int?)line.LedgerAccountId)
                .FirstOrDefault(),
            AccountName = journals
                .Where(entry => entry.PortfolioId == expense.PortfolioId && entry.SourceId == expense.Id &&
                    (entry.SourceType == JournalSourceType.BillIncurred ||
                     entry.SourceType == JournalSourceType.ExpensePayment))
                .OrderBy(entry => entry.SourceType == JournalSourceType.BillIncurred ? 0 : 1)
                .ThenBy(entry => entry.Id)
                .SelectMany(entry => entry.Lines)
                .Where(line => line.LedgerAccount!.AccountType == AccountType.Expense)
                .OrderBy(line => line.LedgerAccount!.Code)
                .Select(line => line.LedgerAccount!.Name)
                .FirstOrDefault(),
            JournalEntryPublicId = journals
                .Where(entry => entry.PortfolioId == expense.PortfolioId && entry.SourceId == expense.Id &&
                    (entry.SourceType == JournalSourceType.BillIncurred ||
                     entry.SourceType == JournalSourceType.ExpensePayment))
                .OrderBy(entry => entry.SourceType == JournalSourceType.BillIncurred ? 0 : 1)
                .ThenBy(entry => entry.Id)
                .Select(entry => (Guid?)entry.PublicId)
                .FirstOrDefault(),
            AllocationTotal = expense.Allocations
                .Select(allocation => (decimal?)allocation.Amount)
                .Sum() ?? 0m,
            Allocations = expense.Allocations
                .OrderBy(allocation => allocation.Id)
                .Select(allocation => new ExpenseAllocationResponse
                {
                    Id = allocation.Id,
                    TargetKind = allocation.TargetKind,
                    PropertyId = allocation.PropertyId,
                    UnitId = allocation.UnitId,
                    OwnerEntityId = allocation.OwnerEntityId,
                    Amount = allocation.Amount,
                })
                .ToList(),
            HasReceipt = files.Any(file =>
                file.PortfolioId == expense.PortfolioId &&
                file.EntityType == nameof(Expense) &&
                file.EntityId == expense.Id &&
                file.DeletedAt == null),
            ReceiptIsImage = files
                .Where(file =>
                    file.PortfolioId == expense.PortfolioId &&
                    file.EntityType == nameof(Expense) &&
                    file.EntityId == expense.Id &&
                    file.DeletedAt == null)
                .OrderByDescending(file => file.UploadedAt)
                .ThenByDescending(file => file.Id)
                .Select(file => file.ContentType.StartsWith("image/"))
                .FirstOrDefault(),
            LineItems = expense.LineItems
                .OrderBy(line => line.LineNumber)
                .ThenBy(line => line.Id)
                .Select(line => new ExpenseLineItemResponse
                {
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Amount = line.Amount,
                    LineNumber = line.LineNumber,
                })
                .ToList(),
        });
    }

    public static IQueryable<ExpenseResponse> ExpenseList(
        IQueryable<Expense> expenses,
        IQueryable<StoredFile> files)
    {
        return expenses.Select(expense => new ExpenseResponse
        {
            Id = expense.Id,
            PortfolioId = expense.PortfolioId,
            OperationalScope = expense.OperationalScope,
            PropertyId = expense.PropertyId,
            UnitId = expense.UnitId,
            VendorId = expense.VendorId,
            WorkOrderId = expense.WorkOrderId,
            CapitalizedAssetId = expense.CapitalizedAssetId,
            Category = expense.Category,
            Description = expense.Description,
            Status = expense.Status,
            Amount = expense.Amount,
            IncurredAt = expense.IncurredAt,
            DueDate = expense.DueDate,
            PaidAt = expense.PaidAt,
            BillableToOwner = expense.BillableToOwner,
            Notes = expense.Notes,
            CreatedAt = expense.CreatedAt,
            UpdatedAt = expense.UpdatedAt,
            Subtotal = expense.Subtotal,
            TaxAmount = expense.TaxAmount,
            ReceiptData = expense.ReceiptData,
            PaymentMethod = expense.PaymentMethod,
            CardLast4 = expense.CardLast4,
            DocumentKind = expense.DocumentKind,
            PropertyName = expense.Property == null ? null : expense.Property.Name,
            UnitNumber = expense.Unit == null ? null : expense.Unit.UnitNumber,
            VendorName = expense.Vendor == null ? null : expense.Vendor.Name,
            AllocationTotal = expense.Allocations
                .Select(allocation => (decimal?)allocation.Amount)
                .Sum() ?? 0m,
            HasReceipt = files.Any(file =>
                file.PortfolioId == expense.PortfolioId &&
                file.EntityType == nameof(Expense) &&
                file.EntityId == expense.Id &&
                file.DeletedAt == null),
            ReceiptIsImage = files
                .Where(file =>
                    file.PortfolioId == expense.PortfolioId &&
                    file.EntityType == nameof(Expense) &&
                    file.EntityId == expense.Id &&
                    file.DeletedAt == null)
                .OrderByDescending(file => file.UploadedAt)
                .ThenByDescending(file => file.Id)
                .Select(file => file.ContentType.StartsWith("image/"))
                .FirstOrDefault(),
        });
    }

    public static IQueryable<LoanResponse> Loans(IQueryable<Loan> loans) =>
        loans.Select(loan => new LoanResponse
        {
            Id = loan.Id,
            PortfolioId = loan.PortfolioId,
            PropertyId = loan.PropertyId,
            PropertyName = loan.Property!.Name,
            Lender = loan.Lender,
            OriginalAmount = loan.OriginalAmount,
            CurrentBalance = loan.CurrentBalance,
            AnnualInterestRatePct = loan.AnnualInterestRatePct,
            TermMonths = loan.TermMonths,
            StartDate = loan.StartDate,
            DayOfMonthDue = loan.DayOfMonthDue,
            MonthlyPrincipalInterest = loan.MonthlyPrincipalInterest,
            MonthlyEscrow = loan.MonthlyEscrow,
            EscrowCoversTaxes = loan.EscrowCoversTaxes,
            EscrowCoversInsurance = loan.EscrowCoversInsurance,
            Status = loan.Status,
            Notes = loan.Notes,
            CreatedAt = loan.CreatedAt,
            UpdatedAt = loan.UpdatedAt,
        });

    public static IQueryable<RecurringExpenseResponse> RecurringExpenses(
        IQueryable<RecurringExpense> recurringExpenses) =>
        recurringExpenses.Select(expense => new RecurringExpenseResponse
        {
            Id = expense.Id,
            PortfolioId = expense.PortfolioId,
            PropertyId = expense.PropertyId,
            PropertyName = expense.Property == null ? null : expense.Property.Name,
            UnitId = expense.UnitId,
            Category = expense.Category,
            Description = expense.Description,
            Amount = expense.Amount,
            Frequency = expense.Frequency,
            StartDate = expense.StartDate,
            NextRunDate = expense.NextRunDate,
            Active = expense.Active,
            Notes = expense.Notes,
            CreatedAt = expense.CreatedAt,
            UpdatedAt = expense.UpdatedAt,
        });

}
