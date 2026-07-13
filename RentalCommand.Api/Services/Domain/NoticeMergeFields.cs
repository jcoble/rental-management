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
    public const string RenewalStartDate = "renewal_start_date";
    public const string OverdueAmount = "overdue_amount";
    public const string LateFeeAmount = "late_fee_amount";
    public const string Today = "today";
    public const string LandlordName = "landlord_name";
    public const string PortfolioName = "portfolio_name";

    private static readonly string[] Common =
        [TenantName, PropertyAddress, UnitNumber, PortfolioName]; // LandlordName removed — cannot be reliably populated yet

    public static IReadOnlyList<string> ForType(string noticeType) => noticeType switch
    {
        "rent-reminder" => [.. Common, RentAmount, RentDueDate],
        "lease-renewal-offer" => [.. Common, LeaseStartDate, LeaseEndDate, RenewalStartDate, RentAmount],
        "month-to-month-offer" => [.. Common, LeaseEndDate, RentAmount],
        "lease-non-renewal" => [.. Common, LeaseEndDate],
        "late-rent-late-fee" => [.. Common, OverdueAmount, RentDueDate, LateFeeAmount, Today],
        _ => Common,
    };

    public static IReadOnlyList<string> All =>
        [TenantName, PropertyAddress, UnitNumber, LeaseStartDate, LeaseEndDate,
         RentAmount, RentDueDate, RenewalStartDate, OverdueAmount, LateFeeAmount, Today,
         LandlordName, PortfolioName];
}
