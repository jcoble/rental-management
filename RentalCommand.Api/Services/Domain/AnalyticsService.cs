using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAnalyticsService"/>
public sealed class AnalyticsService : IAnalyticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAuthSecurityClock _authSecurityClock;

    public AnalyticsService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAuthSecurityClock authSecurityClock)
    {
        _db = db;
        _timeProvider = timeProvider;
        _authSecurityClock = authSecurityClock;
    }

    /// <summary>
    /// Produces the complete overview from one PostgreSQL statement. The first materialized CTE is
    /// the caller's current-session, current-revision, <c>reports.read</c> property set. Every metric
    /// joins that CTE, including tenant-account money and agreement/addendum values whose property is
    /// reached through LeaseManagement. JSON aggregation creates the two nested chart collections in
    /// PostgreSQL; no rows are grouped, joined, filtered, or sorted after materialization.
    /// </summary>
    public async Task<AnalyticsOverview> GetOverviewAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var securityNow = _authSecurityClock.UtcNow();
        var today = DateOnly.FromDateTime(now);
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var trendStart = monthStart.AddMonths(-11);
        var trendStartUtc = trendStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var trendEndUtc = monthEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var day30 = today.AddDays(30);
        var day60 = today.AddDays(60);
        var day90 = today.AddDays(90);

        var row = await _db.Database.SqlQuery<AnalyticsOverviewRow>($$"""
            WITH authorized_properties AS MATERIALIZED (
              SELECT property."Id"
              FROM "Properties" AS property
              WHERE property."PortfolioId" = {{scope.PortfolioId}}
                AND property."DeletedAt" IS NULL
                AND EXISTS (
                  SELECT 1
                  FROM "AuthSessions" AS session
                  JOIN "WorkspaceAccessContexts" AS access_context
                    ON access_context."Id" = session."ActiveAccessContextId"
                   AND access_context."UserId" = session."UserId"
                  JOIN "WorkspaceMemberships" AS membership
                    ON membership."AccessContextId" = access_context."Id"
                   AND membership."PortfolioId" = access_context."PortfolioId"
                  JOIN "MembershipRoleAssignments" AS assignment
                    ON assignment."WorkspaceMembershipId" = membership."Id"
                   AND assignment."PortfolioId" = membership."PortfolioId"
                  JOIN "RoleProfileCapabilities" AS role_capability
                    ON role_capability."RoleProfileId" = assignment."RoleProfileId"
                  JOIN "CapabilityDefinitions" AS capability
                    ON capability."Id" = role_capability."CapabilityDefinitionId"
                  WHERE session."Id" = {{scope.SessionId}}
                    AND session."UserId" = {{scope.UserId}}
                    AND session."ActiveAccessContextId" = {{scope.AccessContextId}}
                    AND session."Status" = 'Active'
                    AND session."RevokedAtUtc" IS NULL
                    AND session."ExpiresAtUtc" > {{securityNow}}
                    AND access_context."Id" = {{scope.AccessContextId}}
                    AND access_context."PortfolioId" = property."PortfolioId"
                    AND access_context."AccessRevision" = {{scope.AccessRevision}}
                    AND access_context."Status" = 'Active'
                    AND access_context."SuspendedAtUtc" IS NULL
                    AND access_context."RevokedAtUtc" IS NULL
                    AND membership."Status" = 'Active'
                    AND membership."SuspendedAtUtc" IS NULL
                    AND membership."RevokedAtUtc" IS NULL
                    AND membership."EffectiveFromUtc" <= {{securityNow}}
                    AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > {{securityNow}})
                    AND assignment."Status" = 'Active'
                    AND assignment."SuspendedAtUtc" IS NULL
                    AND assignment."RevokedAtUtc" IS NULL
                    AND assignment."EffectiveFromUtc" <= {{securityNow}}
                    AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > {{securityNow}})
                    AND capability."Key" = 'reports.read'
                    AND capability."AuthorizationTargetKind" = 'Property'
                    AND (
                      assignment."ScopeKind" = 'AllProperties'
                      OR (
                        assignment."ScopeKind" = 'SelectedProperties'
                        AND EXISTS (
                          SELECT 1
                          FROM "MembershipRoleAssignmentProperties" AS selected_property
                          WHERE selected_property."MembershipRoleAssignmentId" = assignment."Id"
                            AND selected_property."PortfolioId" = property."PortfolioId"
                            AND selected_property."PropertyId" = property."Id")))
                )
            ),
            authorized_accounts AS MATERIALIZED (
              SELECT account."Id" AS "TenantAccountId",
                     relationship."PropertyId"
              FROM "TenantAccounts" AS account
              JOIN "LeaseManagements" AS relationship
                ON relationship."Id" = account."LeaseManagementId"
               AND relationship."PortfolioId" = account."PortfolioId"
              JOIN authorized_properties AS property
                ON property."Id" = relationship."PropertyId"
              WHERE account."PortfolioId" = {{scope.PortfolioId}}
            ),
            occupancy AS (
              SELECT count(*)::integer AS "TotalUnits",
                     count(*) FILTER (WHERE occupancy."IsOccupied")::integer AS "OccupiedUnits"
              FROM "vw_unit_occupancy" AS occupancy
              JOIN authorized_properties AS property ON property."Id" = occupancy."PropertyId"
              WHERE occupancy."PortfolioId" = {{scope.PortfolioId}}
            ),
            rent_charges AS MATERIALIZED (
              SELECT entry.*
              FROM "TenantLedgerEntries" AS entry
              JOIN authorized_accounts AS account ON account."TenantAccountId" = entry."TenantAccountId"
              WHERE entry."PortfolioId" = {{scope.PortfolioId}}
                AND entry."EntryType" = 'RentCharge'
            ),
            month_rent AS (
              SELECT COALESCE(sum(charge."Amount") FILTER (
                       WHERE charge."DueOn" >= {{monthStart}}
                         AND charge."DueOn" < {{monthEnd}}), 0)::numeric AS "Scheduled"
              FROM rent_charges AS charge
            ),
            overdue AS (
              SELECT count(*)::integer AS "Count",
                     COALESCE(sum(balance."OpenAmount"), 0)::numeric AS "Amount"
              FROM "vw_tenant_charge_balances" AS balance
              JOIN rent_charges AS charge ON charge."Id" = balance."TenantLedgerEntryId"
              WHERE balance."PortfolioId" = {{scope.PortfolioId}}
                AND balance."IsPastDue"
            ),
            rent_allocations AS MATERIALIZED (
              SELECT allocation."Amount",
                     credit."EffectiveOn"
              FROM "TenantLedgerAllocations" AS allocation
              JOIN authorized_accounts AS account
                ON account."TenantAccountId" = allocation."TenantAccountId"
              JOIN "TenantLedgerEntries" AS credit
                ON credit."PortfolioId" = allocation."PortfolioId"
               AND credit."TenantAccountId" = allocation."TenantAccountId"
               AND credit."Id" = allocation."CreditEntryId"
              JOIN "TenantLedgerEntries" AS debit
                ON debit."PortfolioId" = allocation."PortfolioId"
               AND debit."TenantAccountId" = allocation."TenantAccountId"
               AND debit."Id" = allocation."DebitEntryId"
              WHERE allocation."PortfolioId" = {{scope.PortfolioId}}
                AND credit."EntryType" = 'PaymentReceipt'
                AND debit."EntryType" = 'RentCharge'
            ),
            month_collected AS (
              SELECT COALESCE(sum(allocation."Amount") FILTER (
                       WHERE allocation."EffectiveOn" >= {{monthStart}}
                         AND allocation."EffectiveOn" < {{monthEnd}}), 0)::numeric AS "Collected"
              FROM rent_allocations AS allocation
            ),
            trend_months AS MATERIALIZED (
              SELECT generate_series(
                       {{trendStart}}::date,
                       {{monthStart}}::date,
                       interval '1 month')::date AS "Month"
            ),
            income_by_month AS (
              SELECT date_trunc('month', allocation."EffectiveOn"::timestamp)::date AS "Month",
                     sum(allocation."Amount")::numeric AS "Income"
              FROM rent_allocations AS allocation
              WHERE allocation."EffectiveOn" >= {{trendStart}}
                AND allocation."EffectiveOn" < {{monthEnd}}
              GROUP BY date_trunc('month', allocation."EffectiveOn"::timestamp)::date
            ),
            expenses_by_month AS (
              SELECT date_trunc('month', expense."IncurredAt" AT TIME ZONE 'UTC')::date AS "Month",
                     sum(expense."Amount")::numeric AS "Expenses"
              FROM "Expenses" AS expense
              JOIN authorized_properties AS property ON property."Id" = expense."PropertyId"
              WHERE expense."PortfolioId" = {{scope.PortfolioId}}
                AND expense."DeletedAt" IS NULL
                AND expense."IncurredAt" >= {{trendStartUtc}}
                AND expense."IncurredAt" < {{trendEndUtc}}
              GROUP BY date_trunc('month', expense."IncurredAt" AT TIME ZONE 'UTC')::date
            ),
            trend AS (
              SELECT jsonb_agg(
                       jsonb_build_object(
                         'Month', to_char(month."Month", 'YYYY-MM'),
                         'Income', round(COALESCE(income."Income", 0), 2),
                         'Expenses', round(COALESCE(expense."Expenses", 0), 2),
                         'Net', round(COALESCE(income."Income", 0) - COALESCE(expense."Expenses", 0), 2))
                       ORDER BY month."Month")::text AS "TrendJson"
              FROM trend_months AS month
              LEFT JOIN income_by_month AS income ON income."Month" = month."Month"
              LEFT JOIN expenses_by_month AS expense ON expense."Month" = month."Month"
            ),
            expiry AS (
              SELECT count(*) FILTER (
                       WHERE agreement."TermEndOn" >= {{today}} AND agreement."TermEndOn" <= {{day30}})::integer AS "D30",
                     count(*) FILTER (
                       WHERE agreement."TermEndOn" >= {{today}} AND agreement."TermEndOn" <= {{day60}})::integer AS "D60",
                     count(*) FILTER (
                       WHERE agreement."TermEndOn" >= {{today}} AND agreement."TermEndOn" <= {{day90}})::integer AS "D90"
              FROM "vw_lease_agreement_status" AS status
              JOIN "LeaseAgreements" AS agreement
                ON agreement."PortfolioId" = status."PortfolioId"
               AND agreement."Id" = status."AgreementId"
              JOIN "LeaseManagements" AS relationship
                ON relationship."PortfolioId" = agreement."PortfolioId"
               AND relationship."Id" = agreement."LeaseManagementId"
              JOIN authorized_properties AS property ON property."Id" = relationship."PropertyId"
              WHERE status."PortfolioId" = {{scope.PortfolioId}}
                AND status."IsGoverning"
                AND agreement."TermEndOn" IS NOT NULL
            ),
            work_order_groups AS (
              SELECT work_order."Priority",
                     count(*)::integer AS "Count"
              FROM "WorkOrders" AS work_order
              JOIN authorized_properties AS property ON property."Id" = work_order."PropertyId"
              WHERE work_order."PortfolioId" = {{scope.PortfolioId}}
                AND work_order."DeletedAt" IS NULL
                AND work_order."Status" NOT IN (4, 5, 7)
              GROUP BY work_order."Priority"
            ),
            open_work_orders AS (
              SELECT COALESCE(
                       jsonb_agg(
                         jsonb_build_object(
                           'Priority', CASE work_order."Priority"
                             WHEN 0 THEN 'Low'
                             WHEN 1 THEN 'Normal'
                             WHEN 2 THEN 'High'
                             WHEN 3 THEN 'Emergency'
                             ELSE work_order."Priority"::text END,
                           'Count', work_order."Count")
                         ORDER BY work_order."Priority"),
                       '[]'::jsonb)::text AS "OpenWorkOrdersJson"
              FROM work_order_groups AS work_order
            ),
            governing_agreements AS MATERIALIZED (
              SELECT agreement."LeaseManagementId",
                     agreement."BaseRentAmount"
              FROM "vw_lease_agreement_status" AS status
              JOIN "LeaseAgreements" AS agreement
                ON agreement."PortfolioId" = status."PortfolioId"
               AND agreement."Id" = status."AgreementId"
              JOIN "LeaseManagements" AS relationship
                ON relationship."PortfolioId" = agreement."PortfolioId"
               AND relationship."Id" = agreement."LeaseManagementId"
              JOIN authorized_properties AS property ON property."Id" = relationship."PropertyId"
              WHERE status."PortfolioId" = {{scope.PortfolioId}}
                AND status."IsGoverning"
            ),
            addendum_rent AS (
              SELECT status."LeaseManagementId",
                     sum(effect."Amount")::numeric AS "Amount"
              FROM "vw_lease_addendum_status" AS status
              JOIN "LeaseAddendumFinancialEffects" AS effect
                ON effect."PortfolioId" = status."PortfolioId"
               AND effect."LeaseAddendumId" = status."LeaseAddendumId"
              JOIN "LeaseManagements" AS relationship
                ON relationship."PortfolioId" = status."PortfolioId"
               AND relationship."Id" = status."LeaseManagementId"
              JOIN authorized_properties AS property ON property."Id" = relationship."PropertyId"
              WHERE status."PortfolioId" = {{scope.PortfolioId}}
                AND status."AddendumStatus" = 'Active'
                AND effect."EffectType" = 'RecurringRentDelta'
              GROUP BY status."LeaseManagementId"
            ),
            recurring_rent AS (
              SELECT COALESCE(sum(
                       agreement."BaseRentAmount" + COALESCE(addendum."Amount", 0)), 0)::numeric AS "Amount"
              FROM governing_agreements AS agreement
              LEFT JOIN addendum_rent AS addendum
                ON addendum."LeaseManagementId" = agreement."LeaseManagementId"
            )
            SELECT occupancy."TotalUnits" AS "TotalUnits",
                   occupancy."OccupiedUnits" AS "OccupiedUnits",
                   round(CASE WHEN occupancy."TotalUnits" = 0 THEN 0
                              ELSE 100.0 * occupancy."OccupiedUnits" / occupancy."TotalUnits" END, 1) AS "OccupancyRate",
                   round(month_rent."Scheduled", 2) AS "MonthRentScheduled",
                   round(month_collected."Collected", 2) AS "MonthRentCollected",
                   round(CASE WHEN month_rent."Scheduled" = 0 THEN 0
                              ELSE 100.0 * month_collected."Collected" / month_rent."Scheduled" END, 1) AS "CollectionRate",
                   overdue."Count" AS "OverdueCount",
                   round(overdue."Amount", 2) AS "OverdueAmount",
                   trend."TrendJson" AS "TrendJson",
                   expiry."D30" AS "LeasesExpiring30",
                   expiry."D60" AS "LeasesExpiring60",
                   expiry."D90" AS "LeasesExpiring90",
                   open_work_orders."OpenWorkOrdersJson" AS "OpenWorkOrdersJson",
                   round(recurring_rent."Amount", 2) AS "MonthlyRecurringRent"
            FROM occupancy
            CROSS JOIN month_rent
            CROSS JOIN month_collected
            CROSS JOIN overdue
            CROSS JOIN trend
            CROSS JOIN expiry
            CROSS JOIN open_work_orders
            CROSS JOIN recurring_rent
            """).SingleAsync(ct);

        return new AnalyticsOverview
        {
            TotalUnits = row.TotalUnits,
            OccupiedUnits = row.OccupiedUnits,
            OccupancyRate = row.OccupancyRate,
            MonthRentScheduled = row.MonthRentScheduled,
            MonthRentCollected = row.MonthRentCollected,
            CollectionRate = row.CollectionRate,
            Overdue = new CountAmount(row.OverdueCount, row.OverdueAmount),
            Trend = JsonSerializer.Deserialize<List<MonthlyPoint>>(row.TrendJson, JsonOptions) ?? [],
            LeasesExpiring30 = row.LeasesExpiring30,
            LeasesExpiring60 = row.LeasesExpiring60,
            LeasesExpiring90 = row.LeasesExpiring90,
            OpenWorkOrders = JsonSerializer.Deserialize<List<PriorityCount>>(
                row.OpenWorkOrdersJson,
                JsonOptions) ?? [],
            MonthlyRecurringRent = row.MonthlyRecurringRent,
        };
    }

    private sealed class AnalyticsOverviewRow
    {
        public int TotalUnits { get; set; }
        public int OccupiedUnits { get; set; }
        public decimal OccupancyRate { get; set; }
        public decimal MonthRentScheduled { get; set; }
        public decimal MonthRentCollected { get; set; }
        public decimal CollectionRate { get; set; }
        public int OverdueCount { get; set; }
        public decimal OverdueAmount { get; set; }
        public string TrendJson { get; set; } = "[]";
        public int LeasesExpiring30 { get; set; }
        public int LeasesExpiring60 { get; set; }
        public int LeasesExpiring90 { get; set; }
        public string OpenWorkOrdersJson { get; set; } = "[]";
        public decimal MonthlyRecurringRent { get; set; }
    }
}
