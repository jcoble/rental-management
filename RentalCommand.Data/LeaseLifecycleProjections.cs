using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>Authoritative, database-derived status for one base Agreement version.</summary>
public sealed class LeaseAgreementStatusProjection
{
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int AgreementId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateOnly GoverningFromOn { get; set; }
    public DateOnly? GoverningThroughExclusiveOn { get; set; }
    public string AgreementStatus { get; set; } = string.Empty;
    public bool IsGoverning { get; set; }
}

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

/// <summary>One actionable contradiction derived entirely by PostgreSQL from canonical lease facts.</summary>
public sealed class LeaseReconciliationExceptionProjection
{
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int LeaseManagementId { get; set; }
    public int? TenantAccountId { get; set; }
    public int? LeaseAgreementId { get; set; }
    public int? SignatureRequestId { get; set; }
    public DateTime EffectiveNowUtc { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string ExceptionCode { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

internal static class LeaseLifecycleProjectionModelConfiguration
{
    internal static void ConfigureLeaseLifecycleProjections(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAgreementStatusProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_lease_agreement_status");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.GoverningFromOn).HasColumnType("date");
            entity.Property(row => row.GoverningThroughExclusiveOn).HasColumnType("date");
            entity.Property(row => row.AgreementStatus).HasMaxLength(40);
        });

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

        modelBuilder.Entity<LeaseReconciliationExceptionProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_lease_reconciliation_exceptions");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.ExceptionCode).HasMaxLength(100);
            entity.Property(row => row.Detail).HasMaxLength(500);
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
          WITH statement_clock AS MATERIALIZED (
            SELECT statement_timestamp() AS "NowUtc"
          )
          SELECT CASE COALESCE(clock_state."Mode", 'Real')
            WHEN 'Frozen' THEN clock_state."SimAnchorUtc"
            WHEN 'Offset' THEN statement_clock."NowUtc"
              + (clock_state."SimAnchorUtc" - clock_state."RealAnchorUtc")
            ELSE statement_clock."NowUtc"
          END
          FROM "Portfolios" AS portfolio
          CROSS JOIN statement_clock
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1
          WHERE portfolio."Id" = portfolio_id
            AND portfolio."DeletedAt" IS NULL;
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
          WHERE portfolio."Id" = portfolio_id
            AND portfolio."DeletedAt" IS NULL;
        $function$;
        """;
}

internal static class LeaseAgreementStatusViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_lease_agreement_status\";";
    public const string Create =
        "CREATE VIEW \"vw_lease_agreement_status\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
        WITH portfolio_business_date AS MATERIALIZED (
          SELECT portfolio."Id" AS "PortfolioId",
                 rc_business_date(portfolio."Id") AS "BusinessDate"
          FROM "Portfolios" AS portfolio
          WHERE portfolio."DeletedAt" IS NULL
        )
        SELECT agreement."PortfolioId",
               agreement."LeaseManagementId",
               agreement."Id" AS "AgreementId",
               effective_date."BusinessDate",
               agreement."GoverningFromOn",
               LEAST(agreement."TermEndOn" + 1, agreement."SupersededEffectiveOn")
                 AS "GoverningThroughExclusiveOn",
               CASE
                 WHEN agreement."VoidedAtUtc" IS NOT NULL THEN 'Void'
                 WHEN agreement."DraftCanceledAtUtc" IS NOT NULL THEN 'Canceled'
                 WHEN agreement."IssuedAtUtc" IS NULL THEN 'Draft'
                 WHEN agreement."FullyExecutedAtUtc" IS NULL THEN 'AwaitingSignatures'
                 WHEN agreement."SupersededEffectiveOn" IS NOT NULL
                      AND effective_date."BusinessDate" >= agreement."SupersededEffectiveOn"
                   THEN 'Superseded'
                 WHEN effective_date."BusinessDate" < agreement."GoverningFromOn"
                   THEN 'Upcoming'
                 WHEN effective_date."BusinessDate" >= agreement."GoverningFromOn"
                      AND (agreement."TermEndOn" IS NULL
                           OR effective_date."BusinessDate" < agreement."TermEndOn" + 1)
                      AND (agreement."SupersededEffectiveOn" IS NULL
                           OR effective_date."BusinessDate" < agreement."SupersededEffectiveOn")
                   THEN 'Active'
                 ELSE 'Expired'
               END AS "AgreementStatus",
               (agreement."FullyExecutedAtUtc" IS NOT NULL
                 AND agreement."VoidedAtUtc" IS NULL
                 AND agreement."DraftCanceledAtUtc" IS NULL
                 AND effective_date."BusinessDate" >= agreement."GoverningFromOn"
                 AND (agreement."TermEndOn" IS NULL
                      OR effective_date."BusinessDate" < agreement."TermEndOn" + 1)
                 AND (agreement."SupersededEffectiveOn" IS NULL
                      OR effective_date."BusinessDate" < agreement."SupersededEffectiveOn"))
                 AS "IsGoverning"
        FROM "LeaseAgreements" AS agreement
        JOIN portfolio_business_date AS effective_date
          ON effective_date."PortfolioId" = agreement."PortfolioId";
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
                   FROM "LeaseAgreements" AS agreement
                   WHERE agreement."PortfolioId" = management."PortfolioId"
                     AND agreement."LeaseManagementId" = management."Id"
                     AND agreement."FullyExecutedAtUtc" IS NOT NULL
                     AND agreement."VoidedAtUtc" IS NULL
                     AND agreement."DraftCanceledAtUtc" IS NULL
                     AND effective_time."BusinessDate" >= agreement."GoverningFromOn"
                     AND (agreement."TermEndOn" IS NULL
                          OR effective_time."BusinessDate" < agreement."TermEndOn" + 1)
                     AND (agreement."SupersededEffectiveOn" IS NULL
                          OR effective_time."BusinessDate" < agreement."SupersededEffectiveOn")
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
            JOIN "LeaseAgreements" AS agreement
              ON agreement."PortfolioId" = management."PortfolioId"
             AND agreement."LeaseManagementId" = management."Id"
             AND agreement."FullyExecutedAtUtc" IS NOT NULL
             AND agreement."VoidedAtUtc" IS NULL
             AND agreement."DraftCanceledAtUtc" IS NULL
             AND effective_time."BusinessDate" >= agreement."GoverningFromOn"
             AND (agreement."TermEndOn" IS NULL
                  OR effective_time."BusinessDate" < agreement."TermEndOn" + 1)
             AND (agreement."SupersededEffectiveOn" IS NULL
                  OR effective_time."BusinessDate" < agreement."SupersededEffectiveOn")
            WHERE management."PortfolioId" = unit."PortfolioId"
              AND management."PropertyId" = unit."PropertyId"
              AND management."UnitId" = unit."Id"
              AND management."CanceledAtUtc" IS NULL
              AND NOT (
                management."PossessionGivenAtUtc" IS NOT NULL
                AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
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
                 AND NOT (management."PossessionGivenAtUtc" IS NOT NULL
                   AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                   AND (management."PossessionReturnedAtUtc" IS NULL
                        OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")))
                 AS "HasGoverningAgreementWithoutPossession",
               (current_agreement."AgreementId" IS NULL
                 AND management."PossessionGivenAtUtc" IS NOT NULL
                 AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                 AND (management."PossessionReturnedAtUtc" IS NULL
                      OR management."PossessionReturnedAtUtc" > effective_time."NowUtc"))
                 AS "HasPossessionWithoutGoverningAgreement",
               ((account."Id" IS NULL)
                 OR current_agreement."AgreementCount" > 1
                 OR household."CurrentPrimaryCount" > 1
                 OR ((management."AccountClosedAtUtc" IS NULL) <> (account."ClosedAtUtc" IS NULL))
                 OR (current_agreement."AgreementId" IS NOT NULL
                   AND NOT (management."PossessionGivenAtUtc" IS NOT NULL
                     AND management."PossessionGivenAtUtc" <= effective_time."NowUtc"
                     AND (management."PossessionReturnedAtUtc" IS NULL
                          OR management."PossessionReturnedAtUtc" > effective_time."NowUtc")))
                 OR (current_agreement."AgreementId" IS NULL
                   AND management."PossessionGivenAtUtc" IS NOT NULL
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
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."PortfolioId" = management."PortfolioId"
              AND agreement."LeaseManagementId" = management."Id"
              AND agreement."FullyExecutedAtUtc" IS NOT NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
              AND effective_time."BusinessDate" >= agreement."GoverningFromOn"
              AND (agreement."TermEndOn" IS NULL
                   OR effective_time."BusinessDate" < agreement."TermEndOn" + 1)
              AND (agreement."SupersededEffectiveOn" IS NULL
                   OR effective_time."BusinessDate" < agreement."SupersededEffectiveOn")
          ) AS counts
          LEFT JOIN LATERAL (
            SELECT agreement."Id" AS "AgreementId"
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."PortfolioId" = management."PortfolioId"
              AND agreement."LeaseManagementId" = management."Id"
              AND agreement."FullyExecutedAtUtc" IS NOT NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
              AND effective_time."BusinessDate" >= agreement."GoverningFromOn"
              AND (agreement."TermEndOn" IS NULL
                   OR effective_time."BusinessDate" < agreement."TermEndOn" + 1)
              AND (agreement."SupersededEffectiveOn" IS NULL
                   OR effective_time."BusinessDate" < agreement."SupersededEffectiveOn")
            ORDER BY agreement."GoverningFromOn" DESC,
                     agreement."Id" DESC
            LIMIT 1
          ) AS selected ON TRUE
        ) AS current_agreement
        LEFT JOIN LATERAL (
          SELECT agreement."Id" AS "AgreementId"
          FROM "LeaseAgreements" AS agreement
          WHERE agreement."PortfolioId" = management."PortfolioId"
            AND agreement."LeaseManagementId" = management."Id"
            AND agreement."FullyExecutedAtUtc" IS NOT NULL
            AND agreement."VoidedAtUtc" IS NULL
            AND agreement."DraftCanceledAtUtc" IS NULL
            AND effective_time."BusinessDate" < agreement."GoverningFromOn"
            AND (agreement."SupersededEffectiveOn" IS NULL
                 OR effective_time."BusinessDate" < agreement."SupersededEffectiveOn")
          ORDER BY agreement."GoverningFromOn",
                   agreement."Id"
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

internal static class LeaseReconciliationExceptionViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_lease_reconciliation_exceptions\";";
    public const string Create =
        "CREATE VIEW \"vw_lease_reconciliation_exceptions\" WITH (security_invoker = true) AS\n" + Definition;

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
        SELECT lifecycle."PortfolioId",
               lifecycle."PropertyId",
               lifecycle."UnitId",
               lifecycle."LeaseManagementId",
               lifecycle."TenantAccountId",
               lifecycle."CurrentAgreementId" AS "LeaseAgreementId",
               NULL::integer AS "SignatureRequestId",
               lifecycle."EffectiveNowUtc",
               lifecycle."BusinessDate",
               'GoverningAgreementWithoutPossession'::text AS "ExceptionCode",
               'A governing executed agreement exists without current possession.'::text AS "Detail"
        FROM "vw_lease_management_lifecycle" AS lifecycle
        WHERE lifecycle."HasGoverningAgreementWithoutPossession"

        UNION ALL

        SELECT lifecycle."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
               lifecycle."LeaseManagementId", lifecycle."TenantAccountId",
               lifecycle."CurrentAgreementId", NULL::integer,
               lifecycle."EffectiveNowUtc", lifecycle."BusinessDate",
               'PossessionWithoutGoverningAgreement'::text,
               'Current possession exists without a governing executed agreement.'::text
        FROM "vw_lease_management_lifecycle" AS lifecycle
        WHERE lifecycle."HasPossessionWithoutGoverningAgreement"

        UNION ALL

        SELECT management."PortfolioId", management."PropertyId", management."UnitId",
               management."Id", account."Id", NULL::integer, NULL::integer,
               effective_time."NowUtc", effective_time."BusinessDate",
               'ReturnedPossessionMissingTurnover'::text,
               'Possession was returned without an open turnover period.'::text
        FROM "LeaseManagements" AS management
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = management."PortfolioId"
        LEFT JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = management."PortfolioId"
         AND account."LeaseManagementId" = management."Id"
        WHERE management."CanceledAtUtc" IS NULL
          AND management."PossessionReturnedAtUtc" IS NOT NULL
          AND management."PossessionReturnedAtUtc" <= effective_time."NowUtc"
          AND NOT EXISTS (
            SELECT 1
            FROM "UnitOperationalPeriods" AS period
            WHERE period."PortfolioId" = management."PortfolioId"
              AND period."PropertyId" = management."PropertyId"
              AND period."UnitId" = management."UnitId"
              AND period."Type" = 'Turnover'
              AND period."StartedAtUtc" >= management."PossessionReturnedAtUtc"
              AND (period."EndedAtUtc" IS NULL OR period."EndedAtUtc" > effective_time."NowUtc"))

        UNION ALL

        SELECT lifecycle."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
               lifecycle."LeaseManagementId", balance."TenantAccountId",
               lifecycle."CurrentAgreementId", NULL::integer,
               lifecycle."EffectiveNowUtc", lifecycle."BusinessDate",
               'ClosedAccountWithBalance'::text,
               'A closed tenant account retains a receivable, credit, or deposit balance.'::text
        FROM "vw_lease_management_lifecycle" AS lifecycle
        JOIN "vw_tenant_account_balances" AS balance
          ON balance."PortfolioId" = lifecycle."PortfolioId"
         AND balance."TenantAccountId" = lifecycle."TenantAccountId"
        LEFT JOIN "vw_security_deposit_balances" AS deposit
          ON deposit."PortfolioId" = balance."PortfolioId"
         AND deposit."TenantAccountId" = balance."TenantAccountId"
        JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = balance."PortfolioId"
         AND account."Id" = balance."TenantAccountId"
        WHERE account."ClosedAtUtc" IS NOT NULL
          AND (balance."ReceivableBalance" <> 0 OR balance."UnappliedCredit" <> 0
               OR COALESCE(deposit."HeldBalance", 0) <> 0)

        UNION ALL

        SELECT management."PortfolioId", management."PropertyId", management."UnitId",
               management."Id", account."Id", renewal."Id", NULL::integer,
               effective_time."NowUtc", effective_time."BusinessDate",
               'FutureSuccessorMissingAddendumDisposition'::text,
               'An executed future successor is missing an explicit decision for an active addendum series.'::text
        FROM "LeaseAgreements" AS renewal
        JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = renewal."PortfolioId"
         AND management."Id" = renewal."LeaseManagementId"
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = renewal."PortfolioId"
        LEFT JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = management."PortfolioId"
         AND account."LeaseManagementId" = management."Id"
        WHERE renewal."RenewsAgreementId" IS NOT NULL
          AND renewal."FullyExecutedAtUtc" IS NOT NULL
          AND renewal."VoidedAtUtc" IS NULL
          AND renewal."DraftCanceledAtUtc" IS NULL
          AND renewal."GoverningFromOn" > effective_time."BusinessDate"
          AND EXISTS (
            SELECT 1
            FROM "LeaseAddenda" AS addendum
            WHERE addendum."PortfolioId" = renewal."PortfolioId"
              AND addendum."BaseAgreementId" = renewal."RenewsAgreementId"
              AND addendum."FullyExecutedAtUtc" IS NOT NULL
              AND addendum."VoidedAtUtc" IS NULL
              AND addendum."DraftCanceledAtUtc" IS NULL
              AND NOT EXISTS (
                SELECT 1
                FROM "LeaseRenewalAddendumDecisions" AS decision
                WHERE decision."PortfolioId" = renewal."PortfolioId"
                  AND decision."RenewalAgreementId" = renewal."Id"
                  AND decision."SourceAddendumSeriesPublicId" = addendum."SeriesPublicId"))

        UNION ALL

        SELECT management."PortfolioId", management."PropertyId", management."UnitId",
               management."Id", account."Id", request."LeaseAgreementId", request."Id",
               effective_time."NowUtc", effective_time."BusinessDate",
               'CompletedSignatureMissingExecutedArtifact'::text,
               'A completed signature packet has no executed legal artifact.'::text
        FROM "SignatureRequests" AS request
        JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = request."PortfolioId"
         AND management."Id" = COALESCE(
           (SELECT agreement."LeaseManagementId" FROM "LeaseAgreements" AS agreement
            WHERE agreement."PortfolioId" = request."PortfolioId"
              AND agreement."Id" = request."LeaseAgreementId"),
           (SELECT addendum."LeaseManagementId" FROM "LeaseAddenda" AS addendum
            WHERE addendum."PortfolioId" = request."PortfolioId"
              AND addendum."Id" = request."LeaseAddendumId"))
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = request."PortfolioId"
        LEFT JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = management."PortfolioId"
         AND account."LeaseManagementId" = management."Id"
        WHERE request."Status" = 'Completed'
          AND request."ExecutedArtifactId" IS NULL

        UNION ALL

        SELECT management."PortfolioId", management."PropertyId", management."UnitId",
               management."Id", account."Id", NULL::integer, NULL::integer,
               effective_time."NowUtc", effective_time."BusinessDate",
               'ProviderSettlementMissingLedgerReceipt'::text,
               'A settled provider payment attempt has no canonical tenant-ledger receipt.'::text
        FROM "TenantPaymentAttempts" AS attempt
        JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = attempt."PortfolioId"
         AND account."Id" = attempt."TenantAccountId"
        JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = account."PortfolioId"
         AND management."Id" = account."LeaseManagementId"
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = attempt."PortfolioId"
        WHERE attempt."ProviderObjectId" IS NOT NULL
          AND (attempt."State" = 'Succeeded' OR attempt."SettledAtUtc" IS NOT NULL)
          AND NOT EXISTS (
            SELECT 1
            FROM "TenantLedgerEntries" AS entry
            WHERE entry."PortfolioId" = attempt."PortfolioId"
              AND entry."TenantAccountId" = attempt."TenantAccountId"
              AND entry."ProviderPaymentAttemptId" = attempt."Id")

        UNION ALL

        SELECT management."PortfolioId", management."PropertyId", management."UnitId",
               management."Id", account."Id", entry."LeaseAgreementId", NULL::integer,
               effective_time."NowUtc", effective_time."BusinessDate",
               'LedgerReceiptMissingProviderSettlement'::text,
               'A provider-backed tenant-ledger receipt has no settled provider attempt.'::text
        FROM "TenantLedgerEntries" AS entry
        JOIN "TenantPaymentAttempts" AS attempt
          ON attempt."PortfolioId" = entry."PortfolioId"
         AND attempt."TenantAccountId" = entry."TenantAccountId"
         AND attempt."Id" = entry."ProviderPaymentAttemptId"
        JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = entry."PortfolioId"
         AND account."Id" = entry."TenantAccountId"
        JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = account."PortfolioId"
         AND management."Id" = account."LeaseManagementId"
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = entry."PortfolioId"
        WHERE attempt."ProviderObjectId" IS NOT NULL
          AND attempt."State" <> 'Succeeded'
          AND attempt."SettledAtUtc" IS NULL;
        """;
}
