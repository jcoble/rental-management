using RentalCommand.Api.DTOs;

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

    public static readonly IReadOnlySet<string> SupportedNoticeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "rent-reminder",
        "lease-renewal-offer",
        "month-to-month-offer",
        "lease-non-renewal",
        "late-rent-late-fee",
    };

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

    private static readonly IReadOnlyDictionary<string, (string Label, string Description, string Example)> Help =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            [TenantName] = ("Tenant name", "The recipient tenant's full name.", "Jordan Lee"),
            [PropertyAddress] = ("Property address", "The street address for the rental property.", "293 Mallard Point Drive"),
            [UnitNumber] = ("Unit", "The rental unit name or number.", "Unit 2B"),
            [LeaseStartDate] = ("Lease start date", "The governing agreement's start date.", "August 1, 2026"),
            [LeaseEndDate] = ("Lease end date", "The governing agreement's end date.", "July 31, 2027"),
            [RentAmount] = ("Rent amount", "The recurring rent in the governing agreement.", "$1,250.00"),
            [RentDueDate] = ("Rent due date", "The due date for the rent charge that triggered the notice.", "August 1, 2026"),
            [RenewalStartDate] = ("Renewal start date", "The proposed first day of the renewal term.", "August 1, 2027"),
            [OverdueAmount] = ("Overdue amount", "The unpaid eligible balance at render time.", "$1,250.00"),
            [LateFeeAmount] = ("Late-fee amount", "The eligible late-fee charge included in this notice.", "$75.00"),
            [Today] = ("Current date", "The business date when the notice is rendered.", "August 8, 2026"),
            [LandlordName] = ("Landlord name", "The configured sender or management-company name.", "Mallard Property Management"),
            [PortfolioName] = ("Workspace name", "The Rental Command workspace name.", "Jesse's Portfolio"),
        };

    public static IReadOnlyList<NoticeMergeFieldHelpResponse> HelpForType(string noticeType) =>
        SupportedNoticeTypes.Contains(noticeType)
        ? ForType(noticeType)
            .Select(key => new NoticeMergeFieldHelpResponse(
                key,
                "{{" + key + "}}",
                Help[key].Label,
                Help[key].Description,
                Help[key].Example))
            .ToArray()
        : throw new KeyNotFoundException($"Unknown supplied notice template '{noticeType}'.");
}
