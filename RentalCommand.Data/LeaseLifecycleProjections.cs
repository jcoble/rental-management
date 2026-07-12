using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>Authoritative, database-derived occupancy facts for one live Unit.</summary>
public sealed class UnitOccupancyProjection
{
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public DateTime EffectiveNowUtc { get; set; }
    public bool IsOccupied { get; set; }
    public int? CurrentLeaseManagementId { get; set; }
    public bool HasScheduledMoveIn { get; set; }
    public DateTime? NextPlannedPossessionAtUtc { get; set; }
    public int? PlannedLeaseManagementId { get; set; }
    public bool IsInTurnover { get; set; }
    public bool IsOutOfService { get; set; }
    public bool IsOnManagementHold { get; set; }
    public bool HasGoverningAgreementWithoutPossession { get; set; }
    public bool HasPossessionWithoutGoverningAgreement { get; set; }
    public string? OccupancyExceptionCode { get; set; }
}

/// <summary>Authoritative lifecycle, household, and reconciliation facts for one LeaseManagement.</summary>
public sealed class LeaseManagementLifecycleProjection
{
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int LeaseManagementId { get; set; }
    public DateTime EffectiveNowUtc { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Lifecycle { get; set; } = string.Empty;
    public int? CurrentAgreementId { get; set; }
    public int? UpcomingAgreementId { get; set; }
    public int CurrentPartyCount { get; set; }
    public int CurrentResidentCount { get; set; }
    public int CurrentFinanciallyResponsiblePartyCount { get; set; }
    public int? CurrentPrimaryPartyId { get; set; }
    public int? CurrentPrimaryTenantId { get; set; }
    public string? CurrentPrimaryTenantName { get; set; }
    public int? TenantAccountId { get; set; }
    public bool HasMissingTenantAccount { get; set; }
    public bool HasMultipleGoverningAgreements { get; set; }
    public bool HasMultipleCurrentPrimaryTenants { get; set; }
    public bool HasAccountCloseMismatch { get; set; }
    public bool HasGoverningAgreementWithoutPossession { get; set; }
    public bool HasPossessionWithoutGoverningAgreement { get; set; }
    public bool HasReconciliationException { get; set; }
}

internal static class LeaseLifecycleProjectionModelConfiguration
{
    internal static void ConfigureLeaseLifecycleProjections(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnitOccupancyProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_unit_occupancy");
            entity.Property(row => row.OccupancyExceptionCode).HasMaxLength(80);
        });

        modelBuilder.Entity<LeaseManagementLifecycleProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_lease_management_lifecycle");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.Lifecycle).HasMaxLength(40);
            entity.Property(row => row.CurrentPrimaryTenantName).HasMaxLength(401);
        });
    }
}

/// <summary>
/// Database-visible effective clock shared by read models and future workers. Both functions use
/// PostgreSQL's wall clock while Frozen and Offset modes exactly match
/// <c>SimulationTimeProvider</c>. Each consuming view materializes one value per portfolio so a
/// statement cannot observe two effective instants. The optional simulation timezone wins over
/// the portfolio timezone only for business-date conversion.
/// </summary>
internal static class LeaseEffectiveClockSql
{
    public const string DropBusinessDate = "DROP FUNCTION IF EXISTS rc_business_date(integer);";
    public const string DropEffectiveNowUtc = "DROP FUNCTION IF EXISTS rc_effective_now_utc(integer);";

    public const string CreateEffectiveNowUtc = """
        CREATE OR REPLACE FUNCTION rc_effective_now_utc(portfolio_id integer)
        RETURNS timestamp with time zone
        LANGUAGE sql
        VOLATILE
        PARALLEL UNSAFE
        AS $function$
          SELECT CASE COALESCE(clock_state."Mode", 'Real')
            WHEN 'Frozen' THEN clock_state."SimAnchorUtc"
            WHEN 'Offset' THEN clock_timestamp()
              + (clock_state."SimAnchorUtc" - clock_state."RealAnchorUtc")
            ELSE clock_timestamp()
          END
          FROM (SELECT 1) AS singleton
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1;
        $function$;
        """;

    public const string CreateBusinessDate = """
        CREATE OR REPLACE FUNCTION rc_business_date(portfolio_id integer)
        RETURNS date
        LANGUAGE sql
        VOLATILE
        PARALLEL UNSAFE
        AS $function$
          WITH effective_time AS MATERIALIZED (
            SELECT rc_effective_now_utc(portfolio_id) AS "NowUtc"
          )
          SELECT (effective_time."NowUtc" AT TIME ZONE
                  COALESCE(NULLIF(clock_state."TimeZoneId", ''), portfolio."TimeZone"))::date
          FROM "Portfolios" AS portfolio
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1
          CROSS JOIN effective_time
          WHERE portfolio."Id" = portfolio_id;
        $function$;
        """;
}

internal static class UnitOccupancyViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_unit_occupancy\";";
    public const string Create =
        "CREATE VIEW \"vw_unit_occupancy\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
        WITH effective_portfolio_time AS MATERIALIZED (
          SELECT portfolio."Id" AS "PortfolioId",
                 rc_effective_now_utc(portfolio."Id") AS "NowUtc"
          FROM "Portfolios" AS portfolio
          WHERE portfolio."DeletedAt" IS NULL
        )
        SELECT unit."PortfolioId",
               unit."PropertyId",
               unit."Id" AS "UnitId",
               effective_time."NowUtc" AS "EffectiveNowUtc",
               (current_possession."LeaseManagementId" IS NOT NULL) AS "IsOccupied",
               current_possession."LeaseManagementId" AS "CurrentLeaseManagementId",
               (planned_possession."LeaseManagementId" IS NOT NULL) AS "HasScheduledMoveIn",
               planned_possession."PlannedPossessionAtUtc" AS "NextPlannedPossessionAtUtc",
               planned_possession."LeaseManagementId" AS "PlannedLeaseManagementId",
               operational_flags."IsInTurnover",
               operational_flags."IsOutOfService",
               operational_flags."IsOnManagementHold",
               agreement_reconciliation."HasGoverningAgreementWithoutPossession",
               (current_possession."LeaseManagementId" IS NOT NULL
                 AND NOT current_possession."HasGoverningAgreement")
                 AS "HasPossessionWithoutGoverningAgreement",
               CASE
                 WHEN agreement_reconciliation."HasGoverningAgreementWithoutPossession"
                      AND current_possession."LeaseManagementId" IS NOT NULL
                      AND NOT current_possession."HasGoverningAgreement"
                   THEN 'GoverningAgreementAndPossessionMismatch'
                 WHEN agreement_reconciliation."HasGoverningAgreementWithoutPossession"
                   THEN 'GoverningAgreementWithoutPossession'
                 WHEN current_possession."LeaseManagementId" IS NOT NULL
                      AND NOT current_possession."HasGoverningAgreement"
                   THEN 'PossessionWithoutGoverningAgreement'
                 ELSE NULL
               END AS "OccupancyExceptionCode"
        FROM "Units" AS unit
        JOIN "Properties" AS property
          ON property."Id" = unit."PropertyId"
         AND property."PortfolioId" = unit."PortfolioId"
         AND property."DeletedAt" IS NULL
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = unit."PortfolioId"
        LEFT JOIN LATERAL (
          SELECT management."Id" AS "LeaseManagementId",
                 EXISTS (
                   SELECT 1
                   FROM "vw_lease_agreement_status" AS agreement_status
                   WHERE agreement_status."PortfolioId" = management."PortfolioId"
                     AND agreement_status."LeaseManagementId" = management."Id"
                     AND agreement_status."IsGoverning"
                 ) AS "HasGoverningAgreement"
          FROM "LeaseManagements" AS management
          WHERE management."PortfolioId" = unit."PortfolioId"
            AND management."PropertyId" = unit."PropertyId"
            AND management."UnitId" = unit."Id"
            AND management."CanceledAtUtc" IS NULL
            AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
            AND (management."PossessionReturnedAtUtc" IS NULL
                 OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")
          ORDER BY management."PossessionGivenAtUtc" DESC, management."Id" DESC
          LIMIT 1
        ) AS current_possession ON TRUE
        LEFT JOIN LATERAL (
          SELECT management."Id" AS "LeaseManagementId",
                 management."PlannedPossessionAtUtc"
          FROM "LeaseManagements" AS management
          WHERE management."PortfolioId" = unit."PortfolioId"
            AND management."PropertyId" = unit."PropertyId"
            AND management."UnitId" = unit."Id"
            AND management."CanceledAtUtc" IS NULL
            AND management."PossessionGivenAtUtc" IS NULL
            AND management."PlannedPossessionAtUtc" IS NOT NULL
          ORDER BY management."PlannedPossessionAtUtc", management."Id"
          LIMIT 1
        ) AS planned_possession ON TRUE
        CROSS JOIN LATERAL (
          SELECT EXISTS (
                   SELECT 1 FROM "UnitOperationalPeriods" AS period
                   WHERE period."PortfolioId" = unit."PortfolioId"
                     AND period."PropertyId" = unit."PropertyId"
                     AND period."UnitId" = unit."Id"
                     AND period."Type" = 'Turnover'
                     AND period."StartedAtUtc" <= effective_time."NowUtc"
                     AND (period."EndedAtUtc" IS NULL OR period."EndedAtUtc" > effective_time."NowUtc")
                 ) AS "IsInTurnover",
                 EXISTS (
                   SELECT 1 FROM "UnitOperationalPeriods" AS period
                   WHERE period."PortfolioId" = unit."PortfolioId"
                     AND period."PropertyId" = unit."PropertyId"
                     AND period."UnitId" = unit."Id"
                     AND period."Type" = 'OutOfService'
                     AND period."StartedAtUtc" <= effective_time."NowUtc"
                     AND (period."EndedAtUtc" IS NULL OR period."EndedAtUtc" > effective_time."NowUtc")
                 ) AS "IsOutOfService",
                 EXISTS (
                   SELECT 1 FROM "UnitOperationalPeriods" AS period
                   WHERE period."PortfolioId" = unit."PortfolioId"
                     AND period."PropertyId" = unit."PropertyId"
                     AND period."UnitId" = unit."Id"
                     AND period."Type" = 'ManagementHold'
                     AND period."StartedAtUtc" <= effective_time."NowUtc"
                     AND (period."EndedAtUtc" IS NULL OR period."EndedAtUtc" > effective_time."NowUtc")
                 ) AS "IsOnManagementHold"
        ) AS operational_flags
        CROSS JOIN LATERAL (
          SELECT EXISTS (
            SELECT 1
            FROM "LeaseManagements" AS management
            JOIN "vw_lease_agreement_status" AS agreement_status
              ON agreement_status."PortfolioId" = management."PortfolioId"
             AND agreement_status."LeaseManagementId" = management."Id"
             AND agreement_status."IsGoverning"
            WHERE management."PortfolioId" = unit."PortfolioId"
              AND management."PropertyId" = unit."PropertyId"
              AND management."UnitId" = unit."Id"
              AND management."CanceledAtUtc" IS NULL
              AND NOT (
                management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                AND (management."PossessionReturnedAtUtc" IS NULL
                     OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")
              )
          ) AS "HasGoverningAgreementWithoutPossession"
        ) AS agreement_reconciliation
        WHERE unit."DeletedAt" IS NULL;
        """;
}

internal static class LeaseManagementLifecycleViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_lease_management_lifecycle\";";
    public const string Create =
        "CREATE VIEW \"vw_lease_management_lifecycle\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
        WITH effective_portfolio_time AS MATERIALIZED (
          SELECT portfolio."Id" AS "PortfolioId",
                 effective_time."NowUtc",
                 (effective_time."NowUtc" AT TIME ZONE
                    COALESCE(NULLIF(clock_state."TimeZoneId", ''), portfolio."TimeZone"))::date
                   AS "BusinessDate"
          FROM "Portfolios" AS portfolio
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1
          CROSS JOIN LATERAL (
            SELECT rc_effective_now_utc(portfolio."Id") AS "NowUtc"
          ) AS effective_time
          WHERE portfolio."DeletedAt" IS NULL
        )
        SELECT management."PortfolioId",
               management."PropertyId",
               management."UnitId",
               management."Id" AS "LeaseManagementId",
               effective_time."NowUtc" AS "EffectiveNowUtc",
               effective_time."BusinessDate",
               CASE
                 WHEN management."CanceledAtUtc" IS NOT NULL THEN 'Canceled'
                 WHEN management."AccountClosedAtUtc" IS NOT NULL THEN 'Closed'
                 WHEN management."PossessionReturnedAtUtc" IS NOT NULL
                      AND management."PossessionReturnedAtUtc" <= effective_time."NowUtc"
                   THEN 'AccountingCloseout'
                 WHEN management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                      AND (management."PossessionReturnedAtUtc" IS NULL
                           OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")
                      AND (management."NoticeGivenAtUtc" IS NOT NULL
                           OR management."PlannedMoveOutAtUtc" IS NOT NULL)
                   THEN 'Ending'
                 WHEN management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                      AND (management."PossessionReturnedAtUtc" IS NULL
                           OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")
                   THEN 'Occupied'
                 WHEN (management."PlannedPossessionAtUtc" IS NOT NULL
                       AND management."PossessionGivenAtUtc" IS NULL)
                      OR upcoming_agreement."AgreementId" IS NOT NULL
                   THEN 'Upcoming'
                 ELSE 'Preparing'
               END AS "Lifecycle",
               current_agreement."AgreementId" AS "CurrentAgreementId",
               upcoming_agreement."AgreementId" AS "UpcomingAgreementId",
               household."CurrentPartyCount",
               household."CurrentResidentCount",
               household."CurrentFinanciallyResponsiblePartyCount",
               household."CurrentPrimaryPartyId",
               household."CurrentPrimaryTenantId",
               household."CurrentPrimaryTenantName",
               account."Id" AS "TenantAccountId",
               (account."Id" IS NULL) AS "HasMissingTenantAccount",
               (current_agreement."AgreementCount" > 1) AS "HasMultipleGoverningAgreements",
               (household."CurrentPrimaryCount" > 1) AS "HasMultipleCurrentPrimaryTenants",
               ((management."AccountClosedAtUtc" IS NULL) <>
                 (account."ClosedAtUtc" IS NULL)) AS "HasAccountCloseMismatch",
               (current_agreement."AgreementId" IS NOT NULL
                 AND NOT (management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                   AND (management."PossessionReturnedAtUtc" IS NULL
                        OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")))
                 AS "HasGoverningAgreementWithoutPossession",
               (current_agreement."AgreementId" IS NULL
                 AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                 AND (management."PossessionReturnedAtUtc" IS NULL
                      OR management."PossessionReturnedAtUtc" > effective_time."NowUtc"))
                 AS "HasPossessionWithoutGoverningAgreement",
               ((account."Id" IS NULL)
                 OR current_agreement."AgreementCount" > 1
                 OR household."CurrentPrimaryCount" > 1
                 OR ((management."AccountClosedAtUtc" IS NULL) <> (account."ClosedAtUtc" IS NULL))
                 OR (current_agreement."AgreementId" IS NOT NULL
                   AND NOT (management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                     AND (management."PossessionReturnedAtUtc" IS NULL
                          OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")))
                 OR (current_agreement."AgreementId" IS NULL
                   AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                   AND (management."PossessionReturnedAtUtc" IS NULL
                        OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")))
                 AS "HasReconciliationException"
        FROM "LeaseManagements" AS management
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = management."PortfolioId"
        CROSS JOIN LATERAL (
          SELECT selected."AgreementId",
                 counts."AgreementCount"
          FROM (
            SELECT count(*)::int AS "AgreementCount"
            FROM "vw_lease_agreement_status" AS agreement_status
            WHERE agreement_status."PortfolioId" = management."PortfolioId"
              AND agreement_status."LeaseManagementId" = management."Id"
              AND agreement_status."IsGoverning"
          ) AS counts
          LEFT JOIN LATERAL (
            SELECT agreement_status."AgreementId"
            FROM "vw_lease_agreement_status" AS agreement_status
            WHERE agreement_status."PortfolioId" = management."PortfolioId"
              AND agreement_status."LeaseManagementId" = management."Id"
              AND agreement_status."IsGoverning"
            ORDER BY agreement_status."GoverningFromOn" DESC,
                     agreement_status."AgreementId" DESC
            LIMIT 1
          ) AS selected ON TRUE
        ) AS current_agreement
        LEFT JOIN LATERAL (
          SELECT agreement_status."AgreementId"
          FROM "vw_lease_agreement_status" AS agreement_status
          WHERE agreement_status."PortfolioId" = management."PortfolioId"
            AND agreement_status."LeaseManagementId" = management."Id"
            AND agreement_status."AgreementStatus" = 'Upcoming'
          ORDER BY agreement_status."GoverningFromOn",
                   agreement_status."AgreementId"
          LIMIT 1
        ) AS upcoming_agreement ON TRUE
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS "CurrentPartyCount",
                 count(*) FILTER (WHERE party."Role" IN ('PrimaryTenant', 'CoTenant', 'Occupant'))::int
                   AS "CurrentResidentCount",
                 count(*) FILTER (WHERE party."Role" IN ('PrimaryTenant', 'CoTenant', 'Guarantor'))::int
                   AS "CurrentFinanciallyResponsiblePartyCount",
                 count(*) FILTER (WHERE party."Role" = 'PrimaryTenant')::int
                   AS "CurrentPrimaryCount",
                 max(party."Id") FILTER (WHERE party."Role" = 'PrimaryTenant')
                   AS "CurrentPrimaryPartyId",
                 max(party."TenantId") FILTER (WHERE party."Role" = 'PrimaryTenant')
                   AS "CurrentPrimaryTenantId",
                 max(concat_ws(' ', tenant."FirstName", tenant."LastName"))
                   FILTER (WHERE party."Role" = 'PrimaryTenant') AS "CurrentPrimaryTenantName"
          FROM "LeaseManagementParties" AS party
          JOIN "Tenants" AS tenant
            ON tenant."Id" = party."TenantId"
           AND tenant."PortfolioId" = party."PortfolioId"
          WHERE party."PortfolioId" = management."PortfolioId"
            AND party."LeaseManagementId" = management."Id"
            AND party."EffectiveFrom" <= effective_time."BusinessDate"
            AND (party."EffectiveThrough" IS NULL
                 OR party."EffectiveThrough" >= effective_time."BusinessDate")
        ) AS household
        LEFT JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = management."PortfolioId"
         AND account."LeaseManagementId" = management."Id";
        """;
}
