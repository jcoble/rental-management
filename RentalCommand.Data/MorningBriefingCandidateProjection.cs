using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>
/// Canonical, clock-neutral source facts for the Today/Morning Briefing. Consumers apply their
/// effective business date and authorization scope; the view never decides what is "today".
/// </summary>
public sealed class MorningBriefingCandidateProjection
{
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string RequiredCapability { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int SeverityOrder { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string? TitleText { get; set; }
    public string? DetailText { get; set; }
    public string? LeaseNumber { get; set; }
    public string? UnitNumber { get; set; }
    public string? TenantName { get; set; }
    public string? PropertyName { get; set; }
    public decimal Amount { get; set; }
    public DateTime? EventDateTime { get; set; }
    public DateOnly? EventDateOnly { get; set; }
    public int TypeValue { get; set; }
}

internal static class MorningBriefingCandidateProjectionConfiguration
{
    internal static void ConfigureMorningBriefingCandidateProjection(this ModelBuilder modelBuilder) =>
        modelBuilder.Entity<MorningBriefingCandidateProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_morning_briefing_candidates");
            entity.Property(row => row.Amount).HasPrecision(18, 2);
            entity.Property(row => row.EventDateOnly).HasColumnType("date");
        });
}

internal static class MorningBriefingCandidateViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_morning_briefing_candidates\";";
    public const string Create =
        "CREATE VIEW \"vw_morning_briefing_candidates\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
        SELECT work."PortfolioId", work."PropertyId", work."UnitId",
               'work.read'::text AS "RequiredCapability", 1 AS "SortOrder", 0 AS "SeverityOrder",
               'Maintenance'::text AS "Category", 'WorkOrder'::text AS "EntityType", work."Id" AS "EntityId",
               work."Title" AS "TitleText", work."Description" AS "DetailText",
               NULL::text AS "LeaseNumber", NULL::text AS "UnitNumber", NULL::text AS "TenantName",
               property."Name" AS "PropertyName", 0::numeric(18,2) AS "Amount",
               work."RequestedAt" AS "EventDateTime", NULL::date AS "EventDateOnly", 0 AS "TypeValue"
        FROM "WorkOrders" AS work
        JOIN "Properties" AS property ON property."Id" = work."PropertyId" AND property."PortfolioId" = work."PortfolioId"
        WHERE work."Priority" = 3 AND work."Status" NOT IN (4, 5, 7) AND work."DeletedAt" IS NULL
          AND property."DeletedAt" IS NULL

        UNION ALL

        SELECT charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
               'money.balances.read', 2, 1, 'RentLate', 'TenantAccount', account."Id",
               NULL, NULL, COALESCE(agreement."AgreementNumber", account."AccountNumber"), unit."UnitNumber",
               lifecycle."CurrentPrimaryTenantName", property."Name", SUM(charge."OpenAmount")::numeric(18,2),
               NULL, charge."DueOn", 0
        FROM "vw_tenant_charge_balances" AS charge
        JOIN "TenantAccounts" AS account ON account."Id" = charge."TenantAccountId" AND account."PortfolioId" = charge."PortfolioId"
        JOIN "vw_lease_management_lifecycle" AS lifecycle ON lifecycle."LeaseManagementId" = account."LeaseManagementId" AND lifecycle."PortfolioId" = account."PortfolioId"
        JOIN "Units" AS unit ON unit."Id" = lifecycle."UnitId" AND unit."PortfolioId" = lifecycle."PortfolioId"
        JOIN "Properties" AS property ON property."Id" = lifecycle."PropertyId" AND property."PortfolioId" = lifecycle."PortfolioId"
        LEFT JOIN "LeaseAgreements" AS agreement ON agreement."Id" = lifecycle."CurrentAgreementId" AND agreement."PortfolioId" = lifecycle."PortfolioId"
        WHERE charge."DueOn" IS NOT NULL AND charge."OpenAmount" > 0 AND property."DeletedAt" IS NULL
          AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
        GROUP BY charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId", account."Id",
                 agreement."AgreementNumber", account."AccountNumber", unit."UnitNumber",
                 lifecycle."CurrentPrimaryTenantName", property."Name", charge."DueOn"

        UNION ALL

        SELECT charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
               'money.balances.read', 3, 2, 'RentDue', 'TenantAccount', account."Id",
               NULL, NULL, COALESCE(agreement."AgreementNumber", account."AccountNumber"), unit."UnitNumber",
               lifecycle."CurrentPrimaryTenantName", property."Name", SUM(charge."OpenAmount")::numeric(18,2),
               NULL, charge."DueOn", 0
        FROM "vw_tenant_charge_balances" AS charge
        JOIN "TenantAccounts" AS account ON account."Id" = charge."TenantAccountId" AND account."PortfolioId" = charge."PortfolioId"
        JOIN "vw_lease_management_lifecycle" AS lifecycle ON lifecycle."LeaseManagementId" = account."LeaseManagementId" AND lifecycle."PortfolioId" = account."PortfolioId"
        JOIN "Units" AS unit ON unit."Id" = lifecycle."UnitId" AND unit."PortfolioId" = lifecycle."PortfolioId"
        JOIN "Properties" AS property ON property."Id" = lifecycle."PropertyId" AND property."PortfolioId" = lifecycle."PortfolioId"
        LEFT JOIN "LeaseAgreements" AS agreement ON agreement."Id" = lifecycle."CurrentAgreementId" AND agreement."PortfolioId" = lifecycle."PortfolioId"
        WHERE charge."EntryType" = 'RentCharge' AND charge."DueOn" IS NOT NULL AND charge."OpenAmount" > 0
          AND property."DeletedAt" IS NULL
          AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
        GROUP BY charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId", account."Id",
                 agreement."AgreementNumber", account."AccountNumber", unit."UnitNumber",
                 lifecycle."CurrentPrimaryTenantName", property."Name", charge."DueOn"

        UNION ALL

        SELECT appointment."PortfolioId", appointment."PropertyId", appointment."UnitId",
               CASE appointment."Type" WHEN 0 THEN 'leasing.showings.manage'
                    WHEN 1 THEN 'leasing.onboarding.manage' WHEN 2 THEN 'leasing.onboarding.manage'
                    WHEN 3 THEN 'work.read' WHEN 4 THEN 'work.read' ELSE 'rentals.read' END,
               4, 2, 'Appointment', 'Appointment', appointment."Id", appointment."Title", NULL,
               NULL, NULL, NULL, property."Name", 0::numeric(18,2), appointment."ScheduledStart", NULL, appointment."Type"
        FROM "Appointments" AS appointment
        JOIN "Properties" AS property ON property."Id" = appointment."PropertyId" AND property."PortfolioId" = appointment."PortfolioId"
        WHERE appointment."PropertyId" IS NOT NULL AND appointment."Status" IN (0, 1)
          AND property."DeletedAt" IS NULL

        UNION ALL

        SELECT inspection."PortfolioId", inspection."PropertyId", inspection."UnitId",
               'work.read', 5, 2, 'Inspection', 'Inspection', inspection."Id", NULL, NULL,
               NULL, NULL, NULL, property."Name", 0::numeric(18,2), inspection."ScheduledFor", NULL, inspection."Type"
        FROM "Inspections" AS inspection
        JOIN "Properties" AS property ON property."Id" = inspection."PropertyId" AND property."PortfolioId" = inspection."PortfolioId"
        WHERE inspection."Status" = 0 AND property."DeletedAt" IS NULL

        UNION ALL

        SELECT status."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
               'rentals.read', 6, 1, 'LeaseExpiring', 'LeaseAgreement', agreement."Id", NULL, NULL,
               agreement."AgreementNumber", unit."UnitNumber", lifecycle."CurrentPrimaryTenantName", property."Name",
               0::numeric(18,2), NULL, agreement."TermEndOn", 0
        FROM "vw_lease_agreement_status" AS status
        JOIN "LeaseAgreements" AS agreement ON agreement."Id" = status."AgreementId" AND agreement."PortfolioId" = status."PortfolioId"
        JOIN "vw_lease_management_lifecycle" AS lifecycle ON lifecycle."LeaseManagementId" = status."LeaseManagementId" AND lifecycle."PortfolioId" = status."PortfolioId"
        JOIN "Units" AS unit ON unit."Id" = lifecycle."UnitId" AND unit."PortfolioId" = lifecycle."PortfolioId"
        JOIN "Properties" AS property ON property."Id" = lifecycle."PropertyId" AND property."PortfolioId" = lifecycle."PortfolioId"
        WHERE status."IsGoverning" AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
          AND agreement."TermEndOn" IS NOT NULL AND property."DeletedAt" IS NULL;
        """;
}
