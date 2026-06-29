namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The fixed catalog of merge tokens available to notice templates, and which apply to each type.
/// The editor shows these so a landlord only inserts tokens that will actually fill.
/// </summary>
public static class NoticeMergeFields
{
    public const string TenantName = "tenant_name";
    public const string PropertyAddress = "property_address";
    public const string UnitNumber = "unit_number";
    public const string LeaseStartDate = "lease_start_date";
    public const string LeaseEndDate = "lease_end_date";
    public const string RentAmount = "rent_amount";
    public const string RentDueDate = "rent_due_date";
    public const string OverdueAmount = "overdue_amount";
    public const string LateFeeAmount = "late_fee_amount";
    public const string LandlordName = "landlord_name";
    public const string PortfolioName = "portfolio_name";

    private static readonly string[] Common =
        [TenantName, PropertyAddress, UnitNumber, LandlordName, PortfolioName];

    public static IReadOnlyList<string> ForType(string noticeType) => noticeType switch
    {
        "RentReminder" => [.. Common, RentAmount, RentDueDate],
        "RenewalOffer" => [.. Common, LeaseStartDate, LeaseEndDate, RentAmount],
        "MonthToMonthConversion" => [.. Common, LeaseEndDate, RentAmount],
        "MoveOutReminder" => [.. Common, LeaseEndDate],
        "LateRentNotice" => [.. Common, OverdueAmount, RentDueDate, LateFeeAmount],
        _ => Common,
    };

    public static IReadOnlyList<string> All =>
        [TenantName, PropertyAddress, UnitNumber, LeaseStartDate, LeaseEndDate,
         RentAmount, RentDueDate, OverdueAmount, LateFeeAmount, LandlordName, PortfolioName];
}
