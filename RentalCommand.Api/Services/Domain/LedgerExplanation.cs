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
    /// <summary>
    /// Explanation for a payment ledger entry.
    /// </summary>
    /// <param name="paymentType">Rent, SecurityDeposit, LateFee, Utility, Other.</param>
    /// <param name="status">The payment's collection status.</param>
    /// <param name="amount">The payment amount (always positive here).</param>
    /// <param name="dueDate">When the charge was due.</param>
    /// <param name="paidDate">When it was paid, if paid.</param>
    /// <param name="method">How it was paid (check, card, etc.), if known.</param>
    /// <param name="daysPastDueWhenLate">
    /// For late fees, how many days rent was past due when the fee was added, if derivable; otherwise null.
    /// </param>
    public static string ForPayment(
        PaymentType paymentType,
        PaymentStatus status,
        decimal amount,
        DateTime dueDate,
        DateTime? paidDate,
        string? method,
        int? daysPastDueWhenLate = null)
    {
        var money = Money(amount);

        // A collected payment reads as a receipt, regardless of what it paid for.
        if (status == PaymentStatus.Paid)
        {
            var when = (paidDate ?? dueDate);
            var byMethod = string.IsNullOrWhiteSpace(method) ? "" : $" by {method.Trim().ToLowerInvariant()}";
            return paymentType switch
            {
                PaymentType.SecurityDeposit => $"Security deposit of {money} received{byMethod} on {ShortDate(when)}.",
                PaymentType.LateFee => $"Late fee of {money} paid{byMethod} on {ShortDate(when)}.",
                PaymentType.Utility => $"Utility payment of {money} received{byMethod} on {ShortDate(when)}.",
                _ => $"Payment of {money} received{byMethod} on {ShortDate(when)}.",
            };
        }

        if (status == PaymentStatus.Refunded)
            return $"{money} refunded to the tenant.";
        if (status == PaymentStatus.Waived)
            return $"{Label(paymentType)} of {money} was waived (forgiven, not owed).";
        if (status == PaymentStatus.Failed)
            return $"A {money} payment attempt failed and did not go through.";

        // Otherwise this is a charge that is still owed (Scheduled / Partial / Late).
        var owedSuffix = status switch
        {
            PaymentStatus.Partial => " (partially paid, balance still owed)",
            PaymentStatus.Late => " — now past due",
            _ => "",
        };

        return paymentType switch
        {
            PaymentType.Rent =>
                $"Rent for {MonthYear(dueDate)} — {money} due {ShortDate(dueDate)}{owedSuffix}.",
            PaymentType.SecurityDeposit =>
                $"Security deposit of {money} due {ShortDate(dueDate)}{owedSuffix}.",
            PaymentType.LateFee =>
                daysPastDueWhenLate is int d && d > 0
                    ? $"Late fee of {money} — added because rent was {d} day{(d == 1 ? "" : "s")} past due."
                    : $"Late fee of {money} — added because rent was paid late.",
            PaymentType.Utility =>
                $"Utility charge of {money} due {ShortDate(dueDate)}{owedSuffix}.",
            _ =>
                $"Charge of {money} due {ShortDate(dueDate)}{owedSuffix}.",
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

    private static string Label(PaymentType type) => type switch
    {
        PaymentType.Rent => "Rent",
        PaymentType.SecurityDeposit => "Security deposit",
        PaymentType.LateFee => "Late fee",
        PaymentType.Utility => "Utility charge",
        _ => "Charge",
    };

    private static string CategoryLabel(ScheduleECategory category) =>
        category == ScheduleECategory.Other ? "expenses" : category.ToString().ToLowerInvariant();

    private static string Money(decimal value) => value.ToString("$#,0.##;$-#,0.##;$0");

    private static string ShortDate(DateTime date) => date.ToString("MMM d");

    private static string LongDate(DateTime date) => date.ToString("MMM d, yyyy");

    private static string MonthYear(DateTime date) => date.ToString("MMMM yyyy");
}
