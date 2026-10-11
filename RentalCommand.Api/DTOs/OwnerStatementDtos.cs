namespace RentalCommand.Api.DTOs;

public enum OwnerStatementPeriodKind
{
    Monthly,
    Quarterly,
    YearToDate,
    Annual,
}

/// <summary>Half-open calendar period used by owner statements.</summary>
public sealed record OwnerStatementPeriod(
    OwnerStatementPeriodKind Kind,
    DateOnly StartOn,
    DateOnly EndOnExclusive,
    string Label)
{
    public DateTime StartUtc => StartOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    public DateTime EndUtc => EndOnExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    public DateOnly EndOn => EndOnExclusive.AddDays(-1);
    public int Year => StartOn.Year;

    public static OwnerStatementPeriod Resolve(
        string? period,
        DateOnly? asOf,
        DateTime nowUtc,
        int? legacyYear = null)
    {
        var requested = period?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(requested) && legacyYear is int year)
        {
            var start = new DateOnly(year, 1, 1);
            return new OwnerStatementPeriod(OwnerStatementPeriodKind.Annual, start, start.AddYears(1), year.ToString());
        }

        // Keep the pre-period API's explicit annual selector working for callers that
        // still pass `period=annual&year=YYYY` while the new UI uses a calendar period.
        if (requested is "annual" or "year" && legacyYear is int explicitYear)
        {
            var start = new DateOnly(explicitYear, 1, 1);
            return new OwnerStatementPeriod(OwnerStatementPeriodKind.Annual, start, start.AddYears(1), explicitYear.ToString());
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            // The default is the completed month immediately before today.
            var previousMonth = new DateOnly(nowUtc.Year, nowUtc.Month, 1).AddMonths(-1);
            return Monthly(previousMonth);
        }

        var anchor = asOf ?? DateOnly.FromDateTime(nowUtc);
        return requested switch
        {
            "monthly" or "month" => Monthly(anchor),
            "quarterly" or "quarter" => Quarterly(anchor),
            "year-to-date" or "year_to_date" or "ytd" => YearToDate(anchor),
            _ => throw new ArgumentException("Period must be monthly, quarterly, or year-to-date.", nameof(period)),
        };
    }

    private static OwnerStatementPeriod Monthly(DateOnly anchor)
    {
        var start = new DateOnly(anchor.Year, anchor.Month, 1);
        return new OwnerStatementPeriod(OwnerStatementPeriodKind.Monthly, start, start.AddMonths(1), start.ToString("MMMM yyyy"));
    }

    private static OwnerStatementPeriod Quarterly(DateOnly anchor)
    {
        var quarterStartMonth = ((anchor.Month - 1) / 3) * 3 + 1;
        var start = new DateOnly(anchor.Year, quarterStartMonth, 1);
        var quarter = ((quarterStartMonth - 1) / 3) + 1;
        return new OwnerStatementPeriod(OwnerStatementPeriodKind.Quarterly, start, start.AddMonths(3), $"Q{quarter} {start.Year}");
    }

    private static OwnerStatementPeriod YearToDate(DateOnly anchor)
    {
        var start = new DateOnly(anchor.Year, 1, 1);
        return new OwnerStatementPeriod(OwnerStatementPeriodKind.YearToDate, start, anchor.AddDays(1), $"{anchor.Year} year to date");
    }
}

/// <summary>A single property line in an owner statement: income, expenses, management fee, and net.</summary>
public record OwnerStatementPropertyLine(
    int PropertyId,
    string PropertyName,
    decimal RentalIncome,
    decimal Expenses,
    decimal ManagementFee,
    decimal NetToOwner,
    bool HasBalance = true);

/// <summary>
/// Full owner statement for a given owner and tax year: one line per property plus portfolio-level totals.
/// </summary>
public class OwnerStatementReport
{
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public int Year { get; set; }
    public string Period { get; set; } = "annual";
    public string PeriodLabel { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public IReadOnlyList<OwnerStatementPropertyLine> Properties { get; set; } = [];
    public decimal TotalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal TotalManagementFee { get; set; }
    public decimal TotalNetToOwner { get; set; }
    public decimal TotalDistributed { get; set; }
    public decimal Undistributed { get; set; }
    public int ZeroPropertyCount { get; set; }
    public int NonZeroPropertyCount { get; set; }
}

/// <summary>Lightweight summary for the owner picker/list: owner id, name, and year net distribution.</summary>
public record OwnerStatementSummary(
    int OwnerId,
    string OwnerName,
    decimal NetToOwner,
    decimal TotalDistributed = 0m,
    decimal Undistributed = 0m);

public sealed class OwnerStatementSummaryPageResponse
{
    public IReadOnlyList<OwnerStatementSummary> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}
