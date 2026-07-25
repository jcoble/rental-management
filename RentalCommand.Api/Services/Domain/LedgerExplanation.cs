using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Builds the plain-English "why" string for a ledger entry, deterministically — no LLM. The goal is
/// that a non-technical landlord (or a tenant disputing a charge) can read one sentence and understand
/// exactly what a line is and why it's there. All inputs come from the entry's own
/// type/date/amount/status, so the output is stable and always available.
/// </summary>
public static class LedgerExplanation
{
    /// <summary>Plain-English explanation for one immutable canonical tenant-ledger entry.</summary>
    public static string ForTenantLedgerEntry(
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction,
        decimal amount,
        DateOnly effectiveOn,
        DateOnly? dueOn,
        string? paymentMethodSummary,
        string? description)
    {
        var money = Money(amount);
        var effectiveDate = effectiveOn.ToDateTime(TimeOnly.MinValue);
        var dueDate = (dueOn ?? effectiveOn).ToDateTime(TimeOnly.MinValue);
        var byMethod = string.IsNullOrWhiteSpace(paymentMethodSummary)
            ? string.Empty
            : $" by {paymentMethodSummary.Trim().ToLowerInvariant()}";

        return entryType switch
        {
            TenantLedgerEntryType.OpeningBalance => ForOpeningBalance(
                direction == TenantLedgerDirection.Credit ? -amount : amount, effectiveDate),
            TenantLedgerEntryType.RentCharge =>
                $"Rent for {MonthYear(dueDate)} — {money} due {ShortDate(dueDate)}.",
            TenantLedgerEntryType.AddendumCharge =>
                $"Agreement addendum charge of {money} due {ShortDate(dueDate)}.",
            TenantLedgerEntryType.LateFeeCharge =>
                $"Late fee of {money} due {ShortDate(dueDate)}.",
            TenantLedgerEntryType.DepositCharge =>
                $"Security deposit charge of {money} due {ShortDate(dueDate)}.",
            TenantLedgerEntryType.ManualCharge =>
                $"Charge of {money} due {ShortDate(dueDate)}: {ReadableDescription(description)}.",
            TenantLedgerEntryType.PaymentReceipt =>
                $"Payment of {money} received{byMethod} on {ShortDate(effectiveDate)}.",
            TenantLedgerEntryType.Credit =>
                $"Credit of {money} posted on {ShortDate(effectiveDate)}: {ReadableDescription(description)}.",
            TenantLedgerEntryType.Adjustment =>
                $"Account adjustment of {money} posted on {ShortDate(effectiveDate)}: {ReadableDescription(description)}.",
            TenantLedgerEntryType.Refund =>
                $"Refund of {money} posted on {ShortDate(effectiveDate)}.",
            TenantLedgerEntryType.TransferIn =>
                $"Balance transfer of {money} received on {ShortDate(effectiveDate)}.",
            TenantLedgerEntryType.TransferOut =>
                $"Balance transfer of {money} sent on {ShortDate(effectiveDate)}.",
            TenantLedgerEntryType.Reversal =>
                $"Reversal of {money} posted on {ShortDate(effectiveDate)}: {ReadableDescription(description)}.",
            _ => $"{ReadableDescription(description)} — {money} posted on {ShortDate(effectiveDate)}.",
        };
    }
    /// <summary>
    /// Explanation for an expense ledger entry (money the landlord paid out — vendors, repairs, etc.).
    /// </summary>
    public static string ForExpense(
        ScheduleECategory category,
        ExpenseStatus status,
        decimal amount,
        DateTime date,
        string? vendorName,
        string? description)
    {
        var money = Money(amount);
        var who = string.IsNullOrWhiteSpace(vendorName) ? "" : $" to {vendorName.Trim()}";
        var what = string.IsNullOrWhiteSpace(description) ? CategoryLabel(category) : description.Trim();

        return status switch
        {
            ExpenseStatus.Paid => $"Paid {money}{who} on {ShortDate(date)} for {what}.",
            ExpenseStatus.Rejected => $"Rejected bill of {money}{who} for {what} (not paid).",
            ExpenseStatus.Draft => $"Draft expense of {money}{who} for {what}, not yet confirmed.",
            _ => $"Bill of {money}{who} for {what}, recorded {ShortDate(date)} and not yet paid.",
        };
    }

    /// <summary>
    /// Explanation for a lease's opening balance — the figure carried over from before the landlord
    /// started using Rental Command. A positive amount means the tenant already owed that much; a
    /// negative amount means they had a credit on the books.
    /// </summary>
    public static string ForOpeningBalance(decimal amount, DateTime asOfDate)
    {
        var money = Money(Math.Abs(amount));
        var when = LongDate(asOfDate);
        return amount < 0
            ? $"Opening credit carried over from before Rental Command — {money} in the tenant's favor as of {when}."
            : $"Opening balance carried over from before Rental Command — {money} as of {when}.";
    }

    /// <summary>
    /// Explanation for an unmatched bank-feed entry (a deposit or withdrawal not yet tied to a
    /// payment/expense).
    /// </summary>
    public static string ForBank(decimal amount, DateTime date, string? counterparty)
    {
        var money = Money(Math.Abs(amount));
        var who = string.IsNullOrWhiteSpace(counterparty) ? "" : $" ({counterparty.Trim()})";
        return amount >= 0
            ? $"Bank deposit of {money}{who} on {ShortDate(date)}, not yet matched to a payment."
            : $"Bank withdrawal of {money}{who} on {ShortDate(date)}, not yet matched to an expense.";
    }

    private static string CategoryLabel(ScheduleECategory category) =>
        category == ScheduleECategory.Other ? "expenses" : category.ToString().ToLowerInvariant();

    private static string Money(decimal value)
        => decimal.Truncate(value) == value
            ? value.ToString("$#,0;$-#,0;$0")
            : value.ToString("$#,0.00;$-#,0.00;$0.00");

    private static string ShortDate(DateTime date) => date.ToString("MMM d");

    private static string LongDate(DateTime date) => date.ToString("MMM d, yyyy");

    private static string MonthYear(DateTime date) => date.ToString("MMMM yyyy");

    private static string ReadableDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? "tenant account entry" : description.Trim().TrimEnd('.');
}
