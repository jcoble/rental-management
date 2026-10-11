using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extraction schema for tenant move-out or non-renewal notices. This is an existing-rental
/// destination: confirmation records the ending disposition and keeps the uploaded notice on the
/// lease relationship; it never creates a new lease agreement.
/// </summary>
public static class LeaseEndingNoticeExtractionSchema
{
    public const string Instructions =
        "Extract a tenant move-out, non-renewal, or lease-ending notice. This is NOT a lease, renewal, " +
        "lease addendum, mortgage statement, receipt, or rental application. Use the grounding context " +
        "to identify the existing leaseManagements[].leaseManagementId and unitId when the notice names " +
        "a tenant, property, unit, or relationship. Return only facts visible in the notice. If a date is " +
        "unclear, leave it empty rather than guessing. Confirmation will record a NonRenewalMoveOut ending " +
        "workflow and attach the scanned notice to the existing lease relationship.";

    public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } =
    [
        new("lease_management_id", "integer",
            "Existing leaseManagements[].leaseManagementId from grounding context for the rental episode this notice ends."),
        new("unit_id", "integer",
            "Existing units[].unitId from grounding context for the unit named by the notice."),
        new("tenant_name", "string", "Tenant or resident name shown on the notice."),
        new("property_name", "string", "Property/building name shown on the notice."),
        new("unit_number", "string", "Unit/apartment number shown on the notice."),
        new("notice_type", "string",
            "Short type such as tenant move-out notice, non-renewal notice, or early termination notice."),
        new("notice_given_date", "date", "Date the tenant gave or signed the notice."),
        new("planned_move_out_date", "date", "Effective move-out, vacate, non-renewal, or lease-end date."),
        new("reason", "string", "Brief tenant-stated reason or relevant notice summary."),
    ];
}
