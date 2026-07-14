using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;

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
        IQueryable<StoredFile> files)
    {
        return expenses.Select(expense => new ExpenseResponse
        {
            Id = expense.Id,
            PortfolioId = expense.PortfolioId,
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
