using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Reporting;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IReportsService"/>
public class ReportsService : IReportsService
{
    /// <summary>IRS 1099-NEC reporting threshold per payee per year.</summary>
    private const decimal Vendor1099Threshold = 600m;

    private const int DefaultExpirationWindowDays = 90;

    private readonly RentalCommandDbContext _db;
    private readonly IScheduleEService _scheduleE;
    private readonly TimeProvider _timeProvider;

    public ReportsService(
        RentalCommandDbContext db,
        IOwnerStatementService ownerStatements,
        IScheduleEService scheduleE,
        IPropertyDispositionService propertyDispositions,
        TimeProvider timeProvider)
    {
        _db = db;
        _ = ownerStatements;
        _scheduleE = scheduleE;
        _ = propertyDispositions;
        _timeProvider = timeProvider;
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────────────────────

    public async Task<int> GetDefaultAnnualReportYearAsync(WorkspaceReadScope scope, CancellationToken ct = default)
    {
        var businessDate = await _db.Database
            .SqlQuery<DateOnly>($"SELECT rc_business_date({scope.PortfolioId}) AS \"Value\"")
            .SingleAsync(ct);

        return businessDate.Year;
    }

    public ReportsCatalogResponse GetCatalog()
    {
        return new ReportsCatalogResponse
        {
            Categories =
            [
                new ReportCategoryGroup
                {
                    Key = "accounting",
                    Title = "Accounting",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "accounting-profit-and-loss",
                            Title = "Profit & Loss",
                            Description = "Income, expenses, and net income for the selected period.",
                            Endpoint = "/api/v1/accounting/income-statement",
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "accounting-balance-sheet",
                            Title = "Balance Sheet",
                            Description = "What the portfolio owns, owes, and has in equity as of a date.",
                            Endpoint = "/api/v1/accounting/balance-sheet",
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "accounting-trial-balance",
                            Title = "Trial Balance",
                            Description = "A check that account debits and credits balance as of a date.",
                            Endpoint = "/api/v1/accounting/trial-balance",
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "accounting-general-ledger",
                            Title = "General Ledger",
                            Description = "Review posted accounting activity by account, property, and source.",
                            Endpoint = "/api/v1/accounting/general-ledger",
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "accounting-cash-flow",
                            Title = "Cash Flow",
                            Description = "See operating cash received, cash paid, debt service, and net cash flow.",
                            Endpoint = "/api/v1/accounting/cash-flow",
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "income-expense-statement",
                            Title = "Income / Expense Statement (P&L)",
                            Description = "Profit & loss with monthly columns over the selected range.",
                            Endpoint = "/api/v1/reports/cash-flow",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "property-pnl-summary",
                            Title = "Property P&L Summary",
                            Description = "Income, expense, and net per property for the range.",
                            Endpoint = "/api/v1/reports/property-pnl",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "cash-and-operating-activity",
                            Title = "Cash and operating activity",
                            Description = "Payments and expenses in date order with a running balance.",
                            Endpoint = "/api/v1/reports/general-ledger",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyId],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "cash-flow",
                            Title = "Cash Flow",
                            Description = "Money in vs. money out by month, with net.",
                            Endpoint = "/api/v1/reports/cash-flow",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "schedule-e",
                            Title = "Schedule E",
                            Description = "Per-property rental income and deductible expenses by IRS category.",
                            Endpoint = "/api/v1/accounting/schedule-e",
                            Params = [ReportParamKeys.Year],
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "year-end-packet",
                            Title = "Year-End Packet",
                            Description = "The clean-books PDF you hand your accountant (Schedule E + P&L + cash flow + rent roll).",
                            Endpoint = "/api/v1/accounting/year-end-packet",
                            Params = [ReportParamKeys.Year],
                            External = true,
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "rent-payments",
                    Title = "Rent & Payments",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "rent-roll",
                            Title = "Rent Roll",
                            Description = "Every unit at a glance: who lives there, what it costs, and what is owed.",
                            Endpoint = "/api/v1/reports/rent-roll",
                            Params = [ReportParamKeys.AsOf, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "aged-receivables",
                            Title = "Aged receivables",
                            Description = "Who owes what and for how long.",
                            Endpoint = "/api/v1/reports/aged-receivables",
                            Params = [ReportParamKeys.AsOf, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "rent-ledger",
                            Title = "Rent Ledger",
                            Description = "Per lease over the range: charges due vs. payments received, running balance.",
                            Endpoint = "/api/v1/reports/rent-ledger",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "delinquency",
                            Title = "Delinquency / Overdue Aging",
                            Description = "Outstanding balances aged into 0-30 / 31-60 / 61-90 / 90+ buckets.",
                            Endpoint = "/api/v1/reports/delinquency",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "owners",
                    Title = "Owners",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "owner-statement",
                            Title = "Owner Statement",
                            Description = "Income, expenses, and net for each property in the selected period.",
                            Endpoint = "/api/v1/accounting/owner-statement",
                            Params = [ReportParamKeys.OwnerId, ReportParamKeys.Year, ReportParamKeys.AsOf],
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "owner-distributions",
                            Title = "Owner Distributions",
                            Description = "Net distribution per owner for the year, across all owners.",
                            Endpoint = "/api/v1/reports/owner-distributions",
                            Params = [ReportParamKeys.Year],
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "operations",
                    Title = "Operations",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "occupancy",
                            Title = "Occupancy / Vacancy",
                            Description = "Units total / occupied / vacant and occupancy % per property.",
                            Endpoint = "/api/v1/reports/occupancy",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "lease-expirations",
                            Title = "Lease Expirations / Renewals Due",
                            Description = "Leases ending within the next N days (default 90).",
                            Endpoint = "/api/v1/reports/lease-expirations",
                            Params = [ReportParamKeys.Days, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "security-deposit-register",
                            Title = "Security Deposit Register",
                            Description = "Held / deductions / returned / current balance per lease.",
                            Endpoint = "/api/v1/reports/security-deposits",
                            Params = [ReportParamKeys.PropertyIds, ReportParamKeys.Skip, ReportParamKeys.Take, ReportParamKeys.Sort],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "vendor-1099",
                            Title = "Vendor 1099 & Payments",
                            Description = "Per 1099-eligible vendor: total paid this year, W-9 on file, needs review.",
                            Endpoint = "/api/v1/reports/vendor-1099",
                            Params = [ReportParamKeys.Year],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "work-orders",
                            Title = "Work Orders / Maintenance",
                            Description = "Work orders requested in the range, with status counts and cost rollup.",
                            Endpoint = "/api/v1/reports/work-orders",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                    ],
                },
            ],
        };
    }

    // ── Rent Roll ────────────────────────────────────────────────────────────────────────────────

    public async Task<RentRollResponse> GetRentRollAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var asOf = query.AsOf ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var asOfUtc = asOf.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddTicks(-1);
        var propertyIds = (query.PropertyIds ?? [])
            .Concat(query.PropertyId is { } propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();

        var parameters = new NpgsqlParameter[]
        {
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("userId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("sessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("asOfDate", NpgsqlDbType.Date) { Value = asOf },
            new("asOfUtc", NpgsqlDbType.TimestampTz) { Value = asOfUtc },
            new("applyPropertyFilter", NpgsqlDbType.Boolean) { Value = propertyIds.Length > 0 },
            new("propertyIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = propertyIds },
        };

        var rows = await _db.Database
            .SqlQueryRaw<RentRollDatabaseRow>(RentRollSql, parameters)
            .ToListAsync(ct);
        if (rows.Count != 1)
            throw new InvalidOperationException($"The rent-roll query returned {rows.Count} summary rows instead of one.");

        var result = rows[0];
        var allRows = DeserializeReportJson<RentRollRow>(result.RowsJson);
        var properties = DeserializeReportJson<RentRollPropertyGroup>(result.PropertiesJson);
        var legacyRows = allRows.Where(row => !row.IsVacant).ToArray();

        return new RentRollResponse
        {
            GeneratedAt = _timeProvider.UtcNow(),
            AsOf = asOf,
            Rows = legacyRows,
            Properties = properties,
            PortfolioTotals = new RentRollTotals
            {
                UnitCount = result.UnitCount,
                LeaseCount = result.PortfolioLeaseCount,
                TotalBaseRent = result.PortfolioBaseRent,
                TotalMonthlyRent = result.PortfolioMonthlyRent,
                TotalSecurityDeposit = result.PortfolioSecurityDeposit,
                TotalDepositHeld = result.PortfolioDepositHeld,
                TotalCurrentBalance = result.PortfolioCurrentBalance,
            },
            LeaseCount = result.LegacyLeaseCount,
            TotalMonthlyRent = result.LegacyMonthlyRent,
            TotalSecurityDeposit = result.LegacySecurityDeposit,
        };
    }

    /// <summary>
    /// Aged receivables uses the canonical tenant-charge balance view for open amounts. The requested
    /// snapshot date controls which charge dates are included; the view supplies the same reversal and
    /// allocation math used by the tenant ledger screens.
    /// </summary>
    public async Task<AgedReceivablesResponse> GetAgedReceivablesAsync(
        WorkspaceReadScope scope,
        ReportRangeQuery query,
        CancellationToken ct = default)
    {
        var asOf = query.AsOf ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var propertyIds = (query.PropertyIds ?? [])
            .Concat(query.PropertyId is { } propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();
        var parameters = new NpgsqlParameter[]
        {
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("userId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("sessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("asOfDate", NpgsqlDbType.Date) { Value = asOf },
            new("cutoff30", NpgsqlDbType.Date) { Value = asOf.AddDays(-30) },
            new("cutoff60", NpgsqlDbType.Date) { Value = asOf.AddDays(-60) },
            new("cutoff90", NpgsqlDbType.Date) { Value = asOf.AddDays(-90) },
            new("applyPropertyFilter", NpgsqlDbType.Boolean) { Value = propertyIds.Length > 0 },
            new("propertyIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = propertyIds },
        };

        var rows = await _db.Database
            .SqlQueryRaw<AgedReceivablesDatabaseRow>(AgedReceivablesSql, parameters)
            .ToListAsync(ct);
        if (rows.Count != 1)
            throw new InvalidOperationException($"The aged-receivables query returned {rows.Count} summary rows instead of one.");

        var result = rows[0];
        var reportRows = DeserializeReportJson<AgedReceivablesRow>(result.RowsJson);
        var properties = DeserializeReportJson<AgedReceivablesPropertyGroup>(result.PropertiesJson);
        var totals = new AgedReceivablesBuckets
        {
            Current = result.Current,
            Days31To60 = result.Days31To60,
            Days61To90 = result.Days61To90,
            Over90 = result.Over90,
        };

        return new AgedReceivablesResponse
        {
            AsOf = asOf,
            Rows = reportRows,
            Properties = properties,
            Totals = totals,
            PortfolioTotals = new AgedReceivablesBuckets
            {
                Current = totals.Current,
                Days31To60 = totals.Days31To60,
                Days61To90 = totals.Days61To90,
                Over90 = totals.Over90,
            },
            TotalOutstanding = result.TotalOutstanding,
        };
    }

    internal const string RentRollSql = """
        WITH report_scopes AS MATERIALIZED (
            SELECT scope."AssignmentId",
                   scope."WorkspaceMembershipId",
                   scope."ScopeKind",
                   scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @portfolioId,
                @sessionId,
                @userId,
                @accessContextId,
                @accessRevision,
                ARRAY['reports.read']::text[],
                'Property') AS scope
        ),
        authorized_properties AS MATERIALIZED (
            SELECT property."Id", property."Name"
            FROM "Properties" AS property
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (NOT @applyPropertyFilter OR property."Id" = ANY(@propertyIds))
              AND EXISTS (
                  SELECT 1
                  FROM report_scopes AS report_scope
                  WHERE report_scope."ScopeKind" = 'AllProperties'
                     OR (report_scope."ScopeKind" = 'SelectedProperties'
                         AND report_scope."PropertyId" = property."Id")
              )
        ),
        unit_scope AS MATERIALIZED (
            SELECT property."Id" AS "PropertyId",
                   property."Name" AS "PropertyName",
                   unit."Id" AS "UnitId",
                   unit."UnitNumber"
            FROM authorized_properties AS property
            JOIN "Units" AS unit
              ON unit."PortfolioId" = @portfolioId
             AND unit."PropertyId" = property."Id"
             AND unit."DeletedAt" IS NULL
        ),
        management_ranked AS (
            SELECT management.*,
                   row_number() OVER (
                       PARTITION BY management."UnitId"
                       ORDER BY management."PossessionGivenAtUtc" DESC, management."Id" DESC
                   ) AS "ManagementRank"
            FROM "LeaseManagements" AS management
            JOIN authorized_properties AS property
              ON property."Id" = management."PropertyId"
            WHERE management."PortfolioId" = @portfolioId
              AND (management."CanceledAtUtc" IS NULL OR management."CanceledAtUtc" > @asOfUtc)
              AND management."PossessionGivenAtUtc" IS NOT NULL
              AND management."PossessionGivenAtUtc" <= @asOfUtc
              AND (management."PossessionReturnedAtUtc" IS NULL
                   OR management."PossessionReturnedAtUtc" > @asOfUtc)
        ),
        current_management AS MATERIALIZED (
            SELECT *
            FROM management_ranked
            WHERE "ManagementRank" = 1
        ),
        agreement_ranked AS (
            SELECT agreement.*,
                   row_number() OVER (
                       PARTITION BY agreement."LeaseManagementId"
                       ORDER BY agreement."GoverningFromOn" DESC,
                                agreement."VersionNumber" DESC,
                                agreement."Id" DESC
                   ) AS "AgreementRank"
            FROM "LeaseAgreements" AS agreement
            JOIN current_management AS management
              ON management."PortfolioId" = agreement."PortfolioId"
             AND management."Id" = agreement."LeaseManagementId"
            WHERE agreement."PortfolioId" = @portfolioId
              AND agreement."FullyExecutedAtUtc" <= @asOfUtc
              AND (agreement."VoidedAtUtc" IS NULL OR agreement."VoidedAtUtc" > @asOfUtc)
              AND (agreement."DraftCanceledAtUtc" IS NULL OR agreement."DraftCanceledAtUtc" > @asOfUtc)
              AND agreement."GoverningFromOn" <= @asOfDate
              AND (agreement."SupersededEffectiveOn" IS NULL
                   OR agreement."SupersededEffectiveOn" > @asOfDate)
        ),
        current_agreement AS MATERIALIZED (
            SELECT *
            FROM agreement_ranked
            WHERE "AgreementRank" = 1
        ),
        tenant_names AS MATERIALIZED (
            SELECT party."PortfolioId",
                   party."LeaseManagementId",
                   min(party."TenantId") FILTER (WHERE party."Role" = 'PrimaryTenant') AS "PrimaryTenantId",
                   min(concat_ws(' ', tenant."FirstName", tenant."LastName"))
                       FILTER (WHERE party."Role" = 'PrimaryTenant') AS "PrimaryName",
                   jsonb_agg(
                       DISTINCT trim(concat_ws(' ', tenant."FirstName", tenant."LastName"))
                       ORDER BY trim(concat_ws(' ', tenant."FirstName", tenant."LastName"))) AS "NamesJson"
            FROM "LeaseManagementParties" AS party
            JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
             AND tenant."DeletedAt" IS NULL
            WHERE party."PortfolioId" = @portfolioId
              AND party."Role" IN ('PrimaryTenant', 'CoTenant', 'Occupant')
              AND party."EffectiveFrom" <= @asOfDate
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= @asOfDate)
            GROUP BY party."PortfolioId", party."LeaseManagementId"
        ),
        account_balances AS MATERIALIZED (
            SELECT balance."PortfolioId",
                   balance."TenantAccountId",
                   COALESCE(sum(balance."OpenAmount"), 0::numeric) AS "CurrentBalance"
            FROM "vw_tenant_charge_balances" AS balance
            WHERE balance."PortfolioId" = @portfolioId
              AND balance."OpenAmount" > 0::numeric
              AND balance."EffectiveOn" <= @asOfDate
            GROUP BY balance."PortfolioId", balance."TenantAccountId"
        ),
        deposit_reversals AS MATERIALIZED (
            SELECT reversal."PortfolioId",
                   reversal."SecurityDepositAccountId",
                   reversal."ReversesEntryId",
                   sum(reversal."Amount") AS "ReversedAmount"
            FROM "SecurityDepositEntries" AS reversal
            WHERE reversal."PortfolioId" = @portfolioId
              AND reversal."EntryType" = 'Reversal'
              AND reversal."EffectiveOn" <= @asOfDate
            GROUP BY reversal."PortfolioId", reversal."SecurityDepositAccountId", reversal."ReversesEntryId"
        ),
        deposit_balances AS MATERIALIZED (
            SELECT deposit_account."PortfolioId",
                   deposit_account."TenantAccountId",
                   COALESCE(sum(
                       CASE WHEN entry."Direction" = 'Increase' THEN 1 ELSE -1 END *
                       GREATEST(entry."Amount" - COALESCE(reversal."ReversedAmount", 0::numeric), 0::numeric)
                   ), 0::numeric) AS "DepositHeld"
            FROM "SecurityDepositAccounts" AS deposit_account
            JOIN "SecurityDepositEntries" AS entry
              ON entry."PortfolioId" = deposit_account."PortfolioId"
             AND entry."SecurityDepositAccountId" = deposit_account."Id"
            LEFT JOIN deposit_reversals AS reversal
              ON reversal."PortfolioId" = entry."PortfolioId"
             AND reversal."SecurityDepositAccountId" = entry."SecurityDepositAccountId"
             AND reversal."ReversesEntryId" = entry."Id"
            WHERE deposit_account."PortfolioId" = @portfolioId
              AND entry."EntryType" <> 'Reversal'
              AND entry."EffectiveOn" <= @asOfDate
            GROUP BY deposit_account."PortfolioId", deposit_account."TenantAccountId"
        ),
        addendum_effects AS MATERIALIZED (
            SELECT addendum."PortfolioId",
                   addendum."LeaseManagementId",
                   COALESCE(sum(effect."Amount") FILTER (WHERE effect."EffectType" = 'RecurringRentDelta'), 0::numeric)
                       AS "RecurringRentDelta",
                   COALESCE(sum(effect."Amount") FILTER (WHERE effect."EffectType" = 'DepositObligationDelta'), 0::numeric)
                       AS "DepositObligationDelta"
            FROM "LeaseAddenda" AS addendum
            JOIN "LeaseAddendumFinancialEffects" AS effect
              ON effect."PortfolioId" = addendum."PortfolioId"
             AND effect."LeaseAddendumId" = addendum."Id"
            WHERE addendum."PortfolioId" = @portfolioId
              AND addendum."FullyExecutedAtUtc" <= @asOfUtc
              AND (addendum."VoidedAtUtc" IS NULL OR addendum."VoidedAtUtc" > @asOfUtc)
              AND (addendum."DraftCanceledAtUtc" IS NULL OR addendum."DraftCanceledAtUtc" > @asOfUtc)
              AND addendum."EffectiveFromOn" <= @asOfDate
              AND (addendum."EffectiveThroughOn" IS NULL OR addendum."EffectiveThroughOn" >= @asOfDate)
              AND (addendum."SupersededEffectiveOn" IS NULL OR addendum."SupersededEffectiveOn" > @asOfDate)
              AND (effect."EffectiveFromOn" IS NULL OR effect."EffectiveFromOn" <= @asOfDate)
              AND (effect."EffectiveThroughOn" IS NULL OR effect."EffectiveThroughOn" >= @asOfDate)
            GROUP BY addendum."PortfolioId", addendum."LeaseManagementId"
        ),
        row_values AS MATERIALIZED (
            SELECT unit."PropertyId",
                   unit."PropertyName",
                   unit."UnitId",
                   unit."UnitNumber",
                   COALESCE(management."Id", 0) AS "LeaseManagementId",
                   COALESCE(management."RelationshipNumber", '') AS "RelationshipNumber",
                   COALESCE(account."Id", 0) AS "TenantAccountId",
                   COALESCE(agreement."Id", 0) AS "AgreementId",
                   COALESCE(agreement."AgreementNumber", '') AS "AgreementNumber",
                   names."PrimaryTenantId" AS "TenantId",
                   COALESCE(names."PrimaryName", CASE WHEN management."Id" IS NULL THEN '' ELSE 'Tenant' END) AS "TenantName",
                   COALESCE(names."NamesJson", '[]'::jsonb) AS "TenantNamesJson",
                   COALESCE(agreement."BaseRentAmount", 0::numeric)
                       + COALESCE(effects."RecurringRentDelta", 0::numeric) AS "MonthlyRent",
                   COALESCE(agreement."BaseRentAmount", 0::numeric) AS "BaseRent",
                   COALESCE(agreement."SecurityDepositObligation", 0::numeric)
                       + COALESCE(effects."DepositObligationDelta", 0::numeric) AS "SecurityDeposit",
                   COALESCE(deposit."DepositHeld", 0::numeric) AS "DepositHeld",
                   COALESCE(balance."CurrentBalance", 0::numeric) AS "CurrentBalance",
                   COALESCE(agreement."TermStartOn", @asOfDate) AS "StartOn",
                   agreement."TermEndOn" AS "EndOn",
                   CASE
                       WHEN management."Id" IS NULL THEN 'Vacant'
                       WHEN agreement."Id" IS NULL THEN 'Possession without agreement'
                       WHEN agreement."TermEndOn" IS NOT NULL AND agreement."TermEndOn" < @asOfDate THEN 'Expired'
                       WHEN agreement."TermEndOn" IS NOT NULL AND agreement."TermEndOn" <= @asOfDate + 60 THEN 'Ending'
                       ELSE 'Active'
                   END AS "StatusName",
                   (management."Id" IS NULL) AS "IsVacant"
            FROM unit_scope AS unit
            LEFT JOIN current_management AS management
              ON management."PortfolioId" = @portfolioId
             AND management."UnitId" = unit."UnitId"
            LEFT JOIN current_agreement AS agreement
              ON agreement."PortfolioId" = @portfolioId
             AND agreement."LeaseManagementId" = management."Id"
            LEFT JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = @portfolioId
             AND account."LeaseManagementId" = management."Id"
            LEFT JOIN tenant_names AS names
              ON names."PortfolioId" = @portfolioId
             AND names."LeaseManagementId" = management."Id"
            LEFT JOIN account_balances AS balance
              ON balance."PortfolioId" = @portfolioId
             AND balance."TenantAccountId" = account."Id"
            LEFT JOIN deposit_balances AS deposit
              ON deposit."PortfolioId" = @portfolioId
             AND deposit."TenantAccountId" = account."Id"
            LEFT JOIN addendum_effects AS effects
              ON effects."PortfolioId" = @portfolioId
             AND effects."LeaseManagementId" = management."Id"
        ),
        row_json AS (
            SELECT row_values.*,
                   jsonb_build_object(
                       'leaseManagementId', row_values."LeaseManagementId",
                       'tenantAccountId', row_values."TenantAccountId",
                       'agreementId', row_values."AgreementId",
                       'relationshipNumber', row_values."RelationshipNumber",
                       'agreementNumber', row_values."AgreementNumber",
                       'propertyId', row_values."PropertyId",
                       'propertyName', row_values."PropertyName",
                       'unitId', row_values."UnitId",
                       'unitNumber', row_values."UnitNumber",
                       'tenantId', row_values."TenantId",
                       'tenantName', row_values."TenantName",
                       'tenantNames', row_values."TenantNamesJson",
                       'monthlyRent', row_values."MonthlyRent",
                       'baseRent', row_values."BaseRent",
                       'securityDeposit', row_values."SecurityDeposit",
                       'depositHeld', row_values."DepositHeld",
                       'currentBalance', row_values."CurrentBalance",
                       'startOn', row_values."StartOn",
                       'endOn', row_values."EndOn",
                       'statusName', row_values."StatusName",
                       'isVacant', row_values."IsVacant"
                   ) AS "RowJson"
            FROM row_values
        ),
        property_rollups AS (
            SELECT row_json."PropertyId",
                   row_json."PropertyName",
                   jsonb_agg(row_json."RowJson" ORDER BY lower(row_json."UnitNumber"), row_json."UnitId") AS "RowsJson",
                   count(*)::int AS "UnitCount",
                   count(*) FILTER (WHERE NOT row_json."IsVacant")::int AS "LeaseCount",
                   COALESCE(sum(row_json."BaseRent"), 0::numeric) AS "TotalBaseRent",
                   COALESCE(sum(row_json."MonthlyRent"), 0::numeric) AS "TotalMonthlyRent",
                   COALESCE(sum(row_json."SecurityDeposit"), 0::numeric) AS "TotalSecurityDeposit",
                   COALESCE(sum(row_json."DepositHeld"), 0::numeric) AS "TotalDepositHeld",
                   COALESCE(sum(row_json."CurrentBalance"), 0::numeric) AS "TotalCurrentBalance"
            FROM row_json
            GROUP BY row_json."PropertyId", row_json."PropertyName"
        )
        SELECT COALESCE(
                   (SELECT jsonb_agg(
                       jsonb_build_object(
                           'propertyId', rollup."PropertyId",
                           'propertyName', rollup."PropertyName",
                           'rows', rollup."RowsJson",
                           'unitCount', rollup."UnitCount",
                           'leaseCount', rollup."LeaseCount",
                           'totalBaseRent', rollup."TotalBaseRent",
                           'totalMonthlyRent', rollup."TotalMonthlyRent",
                           'totalSecurityDeposit', rollup."TotalSecurityDeposit",
                           'totalDepositHeld', rollup."TotalDepositHeld",
                           'totalCurrentBalance', rollup."TotalCurrentBalance"
                       ) ORDER BY lower(rollup."PropertyName"), rollup."PropertyId")
                    FROM property_rollups AS rollup), '[]'::jsonb)::text AS "PropertiesJson",
               COALESCE((SELECT jsonb_agg(row_json."RowJson" ORDER BY lower(row_json."PropertyName"), lower(row_json."UnitNumber"), row_json."UnitId")
                         FROM row_json WHERE NOT row_json."IsVacant"), '[]'::jsonb)::text AS "RowsJson",
               (SELECT count(*)::int FROM row_values) AS "UnitCount",
               (SELECT count(*)::int FROM row_values WHERE NOT row_values."IsVacant") AS "PortfolioLeaseCount",
               COALESCE((SELECT sum(row_values."BaseRent") FROM row_values), 0::numeric) AS "PortfolioBaseRent",
               COALESCE((SELECT sum(row_values."MonthlyRent") FROM row_values), 0::numeric) AS "PortfolioMonthlyRent",
               COALESCE((SELECT sum(row_values."SecurityDeposit") FROM row_values), 0::numeric) AS "PortfolioSecurityDeposit",
               COALESCE((SELECT sum(row_values."DepositHeld") FROM row_values), 0::numeric) AS "PortfolioDepositHeld",
               COALESCE((SELECT sum(row_values."CurrentBalance") FROM row_values), 0::numeric) AS "PortfolioCurrentBalance",
               (SELECT count(*)::int FROM row_values WHERE NOT row_values."IsVacant") AS "LegacyLeaseCount",
               COALESCE((SELECT sum(row_values."MonthlyRent") FROM row_values WHERE NOT row_values."IsVacant"), 0::numeric) AS "LegacyMonthlyRent",
               COALESCE((SELECT sum(row_values."SecurityDeposit") FROM row_values WHERE NOT row_values."IsVacant"), 0::numeric) AS "LegacySecurityDeposit"
        """;

    internal const string AgedReceivablesSql = """
        WITH report_scopes AS MATERIALIZED (
            SELECT scope."AssignmentId",
                   scope."WorkspaceMembershipId",
                   scope."ScopeKind",
                   scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @portfolioId,
                @sessionId,
                @userId,
                @accessContextId,
                @accessRevision,
                ARRAY['reports.read']::text[],
                'Property') AS scope
        ),
        authorized_properties AS MATERIALIZED (
            SELECT property."Id", property."Name"
            FROM "Properties" AS property
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (NOT @applyPropertyFilter OR property."Id" = ANY(@propertyIds))
              AND EXISTS (
                  SELECT 1
                  FROM report_scopes AS report_scope
                  WHERE report_scope."ScopeKind" = 'AllProperties'
                     OR (report_scope."ScopeKind" = 'SelectedProperties'
                         AND report_scope."PropertyId" = property."Id")
              )
        ),
        tenant_names AS MATERIALIZED (
            SELECT party."PortfolioId",
                   party."LeaseManagementId",
                   min(party."TenantId") FILTER (WHERE party."Role" = 'PrimaryTenant') AS "PrimaryTenantId",
                   min(concat_ws(' ', tenant."FirstName", tenant."LastName"))
                       FILTER (WHERE party."Role" = 'PrimaryTenant') AS "PrimaryName",
                   jsonb_agg(
                       DISTINCT trim(concat_ws(' ', tenant."FirstName", tenant."LastName"))
                       ORDER BY trim(concat_ws(' ', tenant."FirstName", tenant."LastName"))) AS "NamesJson"
            FROM "LeaseManagementParties" AS party
            JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
             AND tenant."DeletedAt" IS NULL
            WHERE party."PortfolioId" = @portfolioId
              AND party."Role" IN ('PrimaryTenant', 'CoTenant', 'Occupant')
              AND party."EffectiveFrom" <= @asOfDate
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= @asOfDate)
            GROUP BY party."PortfolioId", party."LeaseManagementId"
        ),
        charge_rows AS MATERIALIZED (
            SELECT balance."TenantAccountId",
                   balance."TenantLedgerEntryId",
                   balance."EffectiveOn",
                   balance."OpenAmount",
                   account."LeaseManagementId",
                   management."PropertyId",
                   property."Name" AS "PropertyName",
                   management."UnitId",
                   unit."UnitNumber",
                   names."PrimaryTenantId" AS "TenantId",
                   COALESCE(names."PrimaryName", 'Tenant') AS "TenantName",
                   COALESCE(names."NamesJson", jsonb_build_array(COALESCE(names."PrimaryName", 'Tenant'))) AS "TenantNamesJson"
            FROM "vw_tenant_charge_balances" AS balance
            JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = balance."PortfolioId"
             AND account."Id" = balance."TenantAccountId"
            JOIN "LeaseManagements" AS management
              ON management."PortfolioId" = account."PortfolioId"
             AND management."Id" = account."LeaseManagementId"
            JOIN authorized_properties AS property
              ON property."Id" = management."PropertyId"
            JOIN "Units" AS unit
              ON unit."PortfolioId" = management."PortfolioId"
             AND unit."Id" = management."UnitId"
            LEFT JOIN tenant_names AS names
              ON names."PortfolioId" = management."PortfolioId"
             AND names."LeaseManagementId" = management."Id"
            WHERE balance."PortfolioId" = @portfolioId
              AND balance."OpenAmount" > 0::numeric
              AND balance."EffectiveOn" <= @asOfDate
        ),
        account_rollups AS MATERIALIZED (
            SELECT charge."TenantAccountId",
                   max(charge."LeaseManagementId") AS "LeaseManagementId",
                   max(charge."PropertyId") AS "PropertyId",
                   max(charge."PropertyName") AS "PropertyName",
                   max(charge."UnitId") AS "UnitId",
                   max(charge."UnitNumber") AS "UnitNumber",
                   max(charge."TenantId") AS "TenantId",
                   max(charge."TenantName") AS "TenantName",
                   (array_agg(charge."TenantNamesJson" ORDER BY charge."TenantLedgerEntryId"))[1] AS "TenantNamesJson",
                   COALESCE(sum(CASE WHEN charge."EffectiveOn" >= @cutoff30 THEN charge."OpenAmount" ELSE 0::numeric END), 0::numeric) AS "Current",
                   COALESCE(sum(CASE WHEN charge."EffectiveOn" < @cutoff30 AND charge."EffectiveOn" >= @cutoff60 THEN charge."OpenAmount" ELSE 0::numeric END), 0::numeric) AS "Days31To60",
                   COALESCE(sum(CASE WHEN charge."EffectiveOn" < @cutoff60 AND charge."EffectiveOn" >= @cutoff90 THEN charge."OpenAmount" ELSE 0::numeric END), 0::numeric) AS "Days61To90",
                   COALESCE(sum(CASE WHEN charge."EffectiveOn" < @cutoff90 THEN charge."OpenAmount" ELSE 0::numeric END), 0::numeric) AS "Over90",
                   COALESCE(sum(charge."OpenAmount"), 0::numeric) AS "Total",
                   min(charge."EffectiveOn") AS "OldestChargeDate"
            FROM charge_rows AS charge
            GROUP BY charge."TenantAccountId"
        ),
        row_json AS (
            SELECT account_rollups.*,
                   jsonb_build_object(
                       'tenantAccountId', account_rollups."TenantAccountId",
                       'leaseManagementId', account_rollups."LeaseManagementId",
                       'propertyId', account_rollups."PropertyId",
                       'propertyName', account_rollups."PropertyName",
                       'unitId', account_rollups."UnitId",
                       'unitNumber', account_rollups."UnitNumber",
                       'tenantId', account_rollups."TenantId",
                       'tenantName', account_rollups."TenantName",
                       'tenantNames', account_rollups."TenantNamesJson",
                       'buckets', jsonb_build_object(
                           'current', account_rollups."Current",
                           'days31To60', account_rollups."Days31To60",
                           'days61To90', account_rollups."Days61To90",
                           'over90', account_rollups."Over90"
                       ),
                       'total', account_rollups."Total",
                       'oldestChargeDate', account_rollups."OldestChargeDate",
                       'oldestChargeAgeDays', GREATEST(0, @asOfDate - account_rollups."OldestChargeDate")
                   ) AS "RowJson"
            FROM account_rollups
        ),
        property_rollups AS (
            SELECT row_json."PropertyId",
                   row_json."PropertyName",
                   jsonb_agg(row_json."RowJson" ORDER BY row_json."OldestChargeDate", row_json."TenantName", row_json."TenantAccountId") AS "RowsJson",
                   COALESCE(sum(row_json."Current"), 0::numeric) AS "Current",
                   COALESCE(sum(row_json."Days31To60"), 0::numeric) AS "Days31To60",
                   COALESCE(sum(row_json."Days61To90"), 0::numeric) AS "Days61To90",
                   COALESCE(sum(row_json."Over90"), 0::numeric) AS "Over90",
                   COALESCE(sum(row_json."Total"), 0::numeric) AS "TotalOutstanding"
            FROM row_json
            GROUP BY row_json."PropertyId", row_json."PropertyName"
        )
        SELECT COALESCE(
                   (SELECT jsonb_agg(
                       jsonb_build_object(
                           'propertyId', rollup."PropertyId",
                           'propertyName', rollup."PropertyName",
                           'rows', rollup."RowsJson",
                           'buckets', jsonb_build_object(
                               'current', rollup."Current",
                               'days31To60', rollup."Days31To60",
                               'days61To90', rollup."Days61To90",
                               'over90', rollup."Over90"
                           ),
                           'totalOutstanding', rollup."TotalOutstanding"
                       ) ORDER BY lower(rollup."PropertyName"), rollup."PropertyId")
                    FROM property_rollups AS rollup), '[]'::jsonb)::text AS "PropertiesJson",
               COALESCE((SELECT jsonb_agg(row_json."RowJson" ORDER BY row_json."OldestChargeDate", row_json."TenantName", row_json."TenantAccountId")
                         FROM row_json), '[]'::jsonb)::text AS "RowsJson",
               COALESCE((SELECT sum(row_json."Current") FROM row_json), 0::numeric) AS "Current",
               COALESCE((SELECT sum(row_json."Days31To60") FROM row_json), 0::numeric) AS "Days31To60",
               COALESCE((SELECT sum(row_json."Days61To90") FROM row_json), 0::numeric) AS "Days61To90",
               COALESCE((SELECT sum(row_json."Over90") FROM row_json), 0::numeric) AS "Over90",
               COALESCE((SELECT sum(row_json."Total") FROM row_json), 0::numeric) AS "TotalOutstanding"
        """;

    // ── Rent Ledger (accrual, per lease over a range) ──────────────────────────────────────────────

    public async Task<RentLedgerResponse> GetRentLedgerAsync(
        LeaseManagementReadContext access,
        ReportRangeQuery query,
        CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var portfolioId = access.PortfolioId;
        var fromOn = DateOnly.FromDateTime(from);
        var toOn = DateOnly.FromDateTime(to);
        var requestedPropertyIds = (query.PropertyIds ?? [])
            .Concat(query.PropertyId is int propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();
        var parameters = new NpgsqlParameter[]
        {
            new("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
            new("userId", NpgsqlDbType.Integer) { Value = access.UserId },
            new("sessionId", NpgsqlDbType.Uuid) { Value = access.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = access.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = access.AccessRevision },
            new("fromOn", NpgsqlDbType.Date) { Value = fromOn },
            new("toOn", NpgsqlDbType.Date) { Value = toOn },
            new("applyPropertyFilter", NpgsqlDbType.Boolean) { Value = requestedPropertyIds.Length > 0 },
            new("propertyIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = requestedPropertyIds },
        };
        var rows = await _db.Database.SqlQueryRaw<RentLedgerDatabaseRow>(RentLedgerSql, parameters)
            .ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"The rent-ledger query returned {rows.Count} summary rows instead of one.");
        }
        var row = rows[0];

        return new RentLedgerResponse
        {
            From = from,
            To = to,
            Leases = DeserializeRentLedgerLeases(row.LeasesJson),
            TotalCharged = row.TotalCharged,
            TotalCredits = row.TotalCredits,
            TotalBalance = row.TotalBalance,
        };
    }

    internal static IReadOnlyList<RentLedgerLease> DeserializeRentLedgerLeases(string json) =>
        JsonSerializer.Deserialize<RentLedgerLease[]>(json, RentLedgerJsonOptions)
        ?? throw new InvalidOperationException("PostgreSQL returned an invalid rent-ledger JSON aggregate.");

    private static readonly JsonSerializerOptions RentLedgerJsonOptions = new(JsonSerializerDefaults.Web);

    internal const string RentLedgerSql = """
        WITH report_scopes AS MATERIALIZED (
            SELECT scope."AssignmentId",
                   scope."WorkspaceMembershipId",
                   scope."ScopeKind",
                   scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @portfolioId,
                @sessionId,
                @userId,
                @accessContextId,
                @accessRevision,
                ARRAY['reports.read']::text[],
                'Property') AS scope
        ),
        balance_scopes AS MATERIALIZED (
            SELECT scope."AssignmentId",
                   scope."WorkspaceMembershipId",
                   scope."ScopeKind",
                   scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @portfolioId,
                @sessionId,
                @userId,
                @accessContextId,
                @accessRevision,
                ARRAY['money.balances.read']::text[],
                'Property') AS scope
        ),
        authorized_properties AS MATERIALIZED (
            SELECT property."Id"
            FROM "Properties" AS property
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (NOT @applyPropertyFilter OR property."Id" = ANY(@propertyIds))
              AND EXISTS (
                  SELECT 1
                  FROM report_scopes AS report_scope
                  JOIN balance_scopes AS balance_scope
                    ON balance_scope."AssignmentId" = report_scope."AssignmentId"
                   AND balance_scope."WorkspaceMembershipId" = report_scope."WorkspaceMembershipId"
                   AND balance_scope."ScopeKind" = report_scope."ScopeKind"
                   AND COALESCE(balance_scope."PropertyId", -1) =
                       COALESCE(report_scope."PropertyId", -1)
                  WHERE report_scope."ScopeKind" = 'AllProperties'
                     OR (report_scope."ScopeKind" = 'SelectedProperties'
                         AND report_scope."PropertyId" = property."Id")
              )
        ),
        authorized_managements AS MATERIALIZED (
            SELECT management."Id",
                   management."PortfolioId",
                   management."RelationshipNumber",
                   management."PropertyId",
                   management."UnitId"
            FROM "LeaseManagements" AS management
            JOIN authorized_properties AS property
              ON property."Id" = management."PropertyId"
            WHERE management."PortfolioId" = @portfolioId
        ),
        reversal_totals AS MATERIALIZED (
            SELECT reversal."PortfolioId",
                   reversal."TenantAccountId",
                   reversal."ReversesEntryId",
                   SUM(reversal."Amount") AS "ReversedAmount"
            FROM "TenantLedgerEntries" AS reversal
            WHERE reversal."PortfolioId" = @portfolioId
              AND reversal."EntryType" = 'Reversal'
              AND reversal."EffectiveOn" <= @toOn
            GROUP BY reversal."PortfolioId",
                     reversal."TenantAccountId",
                     reversal."ReversesEntryId"
        ),
        effective_entries AS MATERIALIZED (
            SELECT entry."PortfolioId",
                   entry."TenantAccountId",
                   entry."Id",
                   entry."EffectiveOn",
                   entry."Direction",
                   entry."EntryType",
                   GREATEST(
                       entry."Amount" - COALESCE(reversal_totals."ReversedAmount", 0::numeric),
                       0::numeric) AS "NetAmount"
            FROM "TenantLedgerEntries" AS entry
            LEFT JOIN reversal_totals
              ON reversal_totals."PortfolioId" = entry."PortfolioId"
             AND reversal_totals."TenantAccountId" = entry."TenantAccountId"
             AND reversal_totals."ReversesEntryId" = entry."Id"
            WHERE entry."PortfolioId" = @portfolioId
              AND entry."EntryType" <> 'Reversal'
              AND entry."EffectiveOn" >= @fromOn
              AND entry."EffectiveOn" <= @toOn
        ),
        activity AS MATERIALIZED (
            SELECT management."Id" AS "LeaseManagementId",
                   management."RelationshipNumber",
                   management."PropertyId",
                   property."Name" AS "PropertyName",
                   unit."UnitNumber",
                   COALESCE(lifecycle."CurrentPrimaryTenantName", 'Tenant') AS "TenantName",
                   entry."Id" AS "EntryId",
                   entry."EffectiveOn",
                   CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END AS "EntryKind",
                   entry."EntryType",
                   entry."NetAmount" AS "Amount",
                   SUM(CASE WHEN entry."Direction" = 'Debit' THEN entry."NetAmount" ELSE -entry."NetAmount" END)
                       OVER (PARTITION BY management."Id"
                             ORDER BY entry."EffectiveOn",
                                      CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END,
                                      entry."Id"
                             ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS "RunningBalance"
            FROM effective_entries AS entry
            JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = entry."PortfolioId"
             AND account."Id" = entry."TenantAccountId"
            JOIN authorized_managements AS management
              ON management."PortfolioId" = account."PortfolioId"
             AND management."Id" = account."LeaseManagementId"
            JOIN "Properties" AS property
              ON property."PortfolioId" = management."PortfolioId"
             AND property."Id" = management."PropertyId"
            JOIN "Units" AS unit
              ON unit."PortfolioId" = management."PortfolioId"
             AND unit."Id" = management."UnitId"
            JOIN "vw_lease_management_lifecycle" AS lifecycle
              ON lifecycle."PortfolioId" = management."PortfolioId"
             AND lifecycle."LeaseManagementId" = management."Id"
            WHERE entry."PortfolioId" = @portfolioId
              AND management."PortfolioId" = @portfolioId
              AND entry."NetAmount" > 0::numeric
              AND (NOT @applyPropertyFilter OR management."PropertyId" = ANY(@propertyIds))
        ),
        relationship_ledgers AS MATERIALIZED (
            SELECT activity."LeaseManagementId",
                   activity."RelationshipNumber",
                   activity."PropertyId",
                   activity."PropertyName",
                   activity."UnitNumber",
                   activity."TenantName",
                   COALESCE(SUM(activity."Amount") FILTER (WHERE activity."EntryKind" = 0), 0::numeric)
                       AS "TotalCharged",
                   COALESCE(SUM(activity."Amount") FILTER (WHERE activity."EntryKind" = 1), 0::numeric)
                       AS "TotalCredits",
                   jsonb_agg(
                       jsonb_build_object(
                           'date', to_char(activity."EffectiveOn", 'YYYY-MM-DD') || 'T00:00:00Z',
                           'type', CASE
                               WHEN activity."EntryKind" = 0 THEN 'Charge'
                               WHEN activity."EntryType" = 'PaymentReceipt' THEN 'Receipt'
                               ELSE 'Credit'
                           END,
                           'description', activity."EntryType",
                           'charge', CASE WHEN activity."EntryKind" = 0 THEN activity."Amount" ELSE 0::numeric END,
                           'credit', CASE WHEN activity."EntryKind" = 1 THEN activity."Amount" ELSE 0::numeric END,
                           'balance', activity."RunningBalance"
                       )
                       ORDER BY activity."EffectiveOn", activity."EntryKind", activity."EntryId") AS "Entries"
            FROM activity
            GROUP BY activity."LeaseManagementId",
                     activity."RelationshipNumber",
                     activity."PropertyId",
                     activity."PropertyName",
                     activity."UnitNumber",
                     activity."TenantName"
        )
        SELECT COALESCE(
                   jsonb_agg(
                       jsonb_build_object(
                           'leaseManagementId', relationship_ledgers."LeaseManagementId",
                           'relationshipNumber', relationship_ledgers."RelationshipNumber",
                           'propertyId', relationship_ledgers."PropertyId",
                           'propertyName', relationship_ledgers."PropertyName",
                           'unitNumber', relationship_ledgers."UnitNumber",
                           'tenantName', relationship_ledgers."TenantName",
                           'entries', relationship_ledgers."Entries",
                           'totalCharged', relationship_ledgers."TotalCharged",
                           'totalCredits', relationship_ledgers."TotalCredits",
                           'balance', relationship_ledgers."TotalCharged" - relationship_ledgers."TotalCredits"
                       )
                       ORDER BY lower(relationship_ledgers."PropertyName"),
                                relationship_ledgers."PropertyName",
                                lower(relationship_ledgers."UnitNumber"),
                                relationship_ledgers."UnitNumber",
                                relationship_ledgers."LeaseManagementId"),
                   '[]'::jsonb)::text AS "LeasesJson",
               COALESCE(SUM(relationship_ledgers."TotalCharged"), 0::numeric) AS "TotalCharged",
               COALESCE(SUM(relationship_ledgers."TotalCredits"), 0::numeric) AS "TotalCredits",
               COALESCE(
                   SUM(relationship_ledgers."TotalCharged" - relationship_ledgers."TotalCredits"),
                   0::numeric) AS "TotalBalance"
        FROM relationship_ledgers
        """;

    // ── Delinquency / Overdue Aging ────────────────────────────────────────────────────────────────

    public async Task<DelinquencyResponse> GetDelinquencyAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var asOf = _timeProvider.UtcNow();
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        var owed =
            from balance in _db.TenantChargeBalanceProjections.AsNoTracking()
            join entry in _db.TenantLedgerEntries.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.TenantLedgerEntryId }
                equals new { entry.PortfolioId, entry.Id }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                on new { management.PortfolioId, management.UnitId }
                equals new { occupancy.PortfolioId, occupancy.UnitId }
            where balance.PortfolioId == portfolioId
                && balance.IsPastDue
                && balance.OpenAmount > 0m
                && occupancy.IsOccupied
                && occupancy.CurrentLeaseManagementId == management.Id
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            select new
            {
                Balance = balance,
                Entry = entry,
                Management = management,
                Lifecycle = lifecycle,
            };

        // Aging by DueDate, computed SQL-side: each bucket is a conditional SUM gated on the DueDate's
        // age via fixed cutoff dates, and the owed amount is Partial-aware (a Partial owes only its
        // unpaid remainder; Scheduled/Late owe in full). One grouped round-trip per behind lease — no
        // payment rows are pulled back to bucket/total in memory. Cutoffs mirror AddToBucket's age
        // boundaries (0-30 / 31-60 / 61-90 / 90+); a Late payment whose DueDate is in the future ages to
        // 0 days (DueDate >= current cutoff) and lands in Current, matching AgeInDays' floor-at-0.
        var current = DateOnly.FromDateTime(asOf.AddDays(-30));
        var d60 = DateOnly.FromDateTime(asOf.AddDays(-60));
        var d90 = DateOnly.FromDateTime(asOf.AddDays(-90));

        var grouped = await owed
            .GroupBy(row => new
            {
                LeaseManagementId = row.Management.Id,
                RelationshipNumber = row.Management.RelationshipNumber,
                row.Management.PropertyId,
                PropertyName = row.Management.Property!.Name,
                UnitNumber = row.Management.Unit!.UnitNumber,
                TenantId = _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == row.Management.PortfolioId
                        && party.Id == row.Lifecycle.CurrentPrimaryPartyId)
                    .Select(party => (int?)party.TenantId)
                    .FirstOrDefault(),
                TenantName = row.Lifecycle.CurrentPrimaryTenantName,
            })
            .Select(g => new
            {
                g.Key,
                Current = g.Sum(row => row.Entry.DueOn >= current ? row.Balance.OpenAmount : 0m),
                Days31To60 = g.Sum(row => row.Entry.DueOn < current && row.Entry.DueOn >= d60 ? row.Balance.OpenAmount : 0m),
                Days61To90 = g.Sum(row => row.Entry.DueOn < d60 && row.Entry.DueOn >= d90 ? row.Balance.OpenAmount : 0m),
                Over90 = g.Sum(row => row.Entry.DueOn < d90 ? row.Balance.OpenAmount : 0m),
                Total = g.Sum(row => row.Balance.OpenAmount),
                OldestDueDate = g.Min(row => row.Entry.DueOn),
            })
            .OrderBy(g => g.OldestDueDate)
            .ThenByDescending(g => g.Total)
            .ToListAsync(ct);

        var totals = await owed
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Current = g.Sum(row => row.Entry.DueOn >= current ? row.Balance.OpenAmount : 0m),
                Days31To60 = g.Sum(row => row.Entry.DueOn < current && row.Entry.DueOn >= d60 ? row.Balance.OpenAmount : 0m),
                Days61To90 = g.Sum(row => row.Entry.DueOn < d60 && row.Entry.DueOn >= d90 ? row.Balance.OpenAmount : 0m),
                Over90 = g.Sum(row => row.Entry.DueOn < d90 ? row.Balance.OpenAmount : 0m),
                TotalOutstanding = g.Sum(row => row.Balance.OpenAmount),
            })
            .SingleOrDefaultAsync(ct);

        var rows = grouped
            .Select(g =>
            {
                var buckets = new DelinquencyBuckets
                {
                    Current = g.Current,
                    Days31To60 = g.Days31To60,
                    Days61To90 = g.Days61To90,
                    Over90 = g.Over90,
                };
                return new DelinquencyRow
                {
                    // Delinquency is lease-scoped (ForCurrentLeaseAttention requires a live lease).
                    LeaseManagementId = g.Key.LeaseManagementId,
                    RelationshipNumber = g.Key.RelationshipNumber,
                    PropertyId = g.Key.PropertyId,
                    PropertyName = g.Key.PropertyName,
                    UnitNumber = g.Key.UnitNumber,
                    TenantId = g.Key.TenantId ?? 0,
                    TenantName = g.Key.TenantName ?? "Tenant",
                    Buckets = buckets,
                    Total = g.Total,
                    // Oldest age from the single per-lease Min(DueDate) — derived from an aggregate, not
                    // by scanning rows.
                    OldestOverdueDays = AgeInDays(
                        g.OldestDueDate!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), asOf),
                };
            })
            .ToList();

        var totalBuckets = new DelinquencyBuckets
        {
            Current = totals?.Current ?? 0m,
            Days31To60 = totals?.Days31To60 ?? 0m,
            Days61To90 = totals?.Days61To90 ?? 0m,
            Over90 = totals?.Over90 ?? 0m,
        };

        return new DelinquencyResponse
        {
            AsOf = asOf,
            Rows = rows,
            Totals = totalBuckets,
            TotalOutstanding = totals?.TotalOutstanding ?? 0m,
        };
    }

    /// <summary>Whole days between a past due date and the as-of instant (floored at 0).</summary>
    internal static int AgeInDays(DateTime dueDate, DateTime asOf)
    {
        var days = (int)Math.Floor((asOf - dueDate).TotalDays);
        return days < 0 ? 0 : days;
    }

    /// <summary>Places an amount into the aging bucket for its age in days (0-30 / 31-60 / 61-90 / 90+).</summary>
    internal static void AddToBucket(DelinquencyBuckets buckets, int days, decimal amount)
    {
        if (days <= 30) buckets.Current += amount;
        else if (days <= 60) buckets.Days31To60 += amount;
        else if (days <= 90) buckets.Days61To90 += amount;
        else buckets.Over90 += amount;
    }

    // ── Cash Flow (income vs expense by month) ─────────────────────────────────────────────────────

    public async Task<CashFlowResponse> GetCashFlowAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var requestedPropertyIds = BuildRequestedPropertyIds(query);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var incomeQuery = FinancialReportProjections.BuildAuthorizedCashFlowIncomeProjection(
            _db,
            scope,
            CapabilityKeys.ReportsRead,
            utcNow,
            from,
            to,
            requestedPropertyIds);
        var expenseQuery = FinancialReportProjections.BuildAuthorizedCashFlowExpenseProjection(
            _db,
            scope,
            CapabilityKeys.ReportsRead,
            utcNow,
            from,
            to,
            requestedPropertyIds);

        var monthCount = CountMonths(from, to);
        var anchor = FinancialReportProjections.BuildAuthorizedCashFlowAnchor(
            _db,
            scope,
            CapabilityKeys.ReportsRead,
            utcNow,
            requestedPropertyIds);
        var monthRows = await FinancialReportProjections.BuildCashFlowMonthProjection(
                anchor,
                incomeQuery,
                expenseQuery,
                from.Year,
                from.Month,
                monthCount)
            .ToListAsync(ct);

        var totalIncome = await incomeQuery
            .SumAsync(row => (decimal?)row.Amount, ct) ?? 0m;

        var totalExpense = await expenseQuery
            .SumAsync(expense => (decimal?)expense.Amount, ct) ?? 0m;

        var months = monthRows
            .Select(m =>
            {
                return new CashFlowMonth
                {
                    Year = m.Year,
                    Month = m.Month,
                    MonthKey = $"{m.Year:D4}-{m.Month:D2}",
                    Label = $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m.Month)} {m.Year}",
                    Income = m.Income,
                    Expense = m.Expense,
                    Net = m.Net,
                };
            })
            .ToList();

        return new CashFlowResponse
        {
            From = from,
            To = to,
            Months = months,
            TotalCount = monthCount,
            Skip = 0,
            Take = monthCount,
            Sort = "month",
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            TotalNet = totalIncome - totalExpense,
        };
    }

    private static int[] BuildRequestedPropertyIds(ReportRangeQuery query) =>
        (query.PropertyIds ?? [])
            .Concat(query.PropertyId is { } propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();

    internal static int CountMonths(DateTime from, DateTime to) =>
        ((to.Year - from.Year) * 12) + to.Month - from.Month + 1;

    /// <summary>Yields the (year, month) of every calendar month the [from, to] range touches, inclusive.</summary>
    internal static IEnumerable<(int Year, int Month)> EnumerateMonths(DateTime from, DateTime to)
    {
        var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= end)
        {
            yield return (cursor.Year, cursor.Month);
            cursor = cursor.AddMonths(1);
        }
    }

    // ── True cash flow (rent − opex − debt service), escrow-aware, DB-side (§9/§18) ─────────────────

    public async Task<CashFlowSummaryResponse> GetTrueCashFlowAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);
        var hasPropertyFilter = query.PropertyId.HasValue || (query.PropertyIds?.Count > 0);
        var authorizedPropertyIds = await authorizedProperties
            .Select(property => property.Id)
            .ToArrayAsync(ct);
        var includePortfolioExpenses = !hasPropertyFilter &&
            await BuildAllPropertiesAuthorityQuery(scope, CapabilityKeys.ReportsRead).AnyAsync(ct);

        var parameters = new NpgsqlParameter[]
        {
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("propertyIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = authorizedPropertyIds },
            new("fromAt", NpgsqlDbType.TimestampTz) { Value = from },
            new("toAt", NpgsqlDbType.TimestampTz) { Value = to },
            new("fromDate", NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(from) },
            new("toDate", NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(to) },
            new("includePortfolioExpenses", NpgsqlDbType.Boolean) { Value = includePortfolioExpenses },
        };
        var databaseRows = await _db.Database
            .SqlQueryRaw<TrueCashFlowDatabaseRow>(TrueCashFlowSql, parameters)
            .ToListAsync(ct);
        if (databaseRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"The true-cash-flow query returned {databaseRows.Count} summary rows instead of one.");
        }

        var databaseRow = databaseRows[0];
        return new CashFlowSummaryResponse
        {
            From = from,
            To = to,
            Properties = JsonSerializer.Deserialize<PropertyCashFlow[]>(
                databaseRow.PropertiesJson, TrueCashFlowJsonOptions) ?? [],
            Months = JsonSerializer.Deserialize<MonthlyCashFlow[]>(
                databaseRow.MonthsJson, TrueCashFlowJsonOptions) ?? [],
            TotalIncome = databaseRow.TotalIncome,
            TotalOperatingExpenses = databaseRow.TotalOperatingExpenses,
            TotalNoi = databaseRow.TotalIncome - databaseRow.TotalOperatingExpenses,
            TotalDebtService = databaseRow.TotalDebtService,
            TotalCashFlow = databaseRow.TotalIncome - databaseRow.TotalOperatingExpenses - databaseRow.TotalDebtService,
        };
    }

    private static readonly JsonSerializerOptions TrueCashFlowJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One PostgreSQL statement owns the true-cash-flow report shape. The only client-side work is
    /// deserializing the two JSON aggregates; all filtering, joins, detail/monthly/property/portfolio
    /// aggregation, escrow exclusion, and latest-correction selection stay inside this SQL statement.
    /// </summary>
    internal const string TrueCashFlowSql = """
        WITH authorized_properties AS MATERIALIZED (
            SELECT property."Id" AS property_id, property."Name" AS property_name
            FROM "Properties" property
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND property."Id" = ANY(@propertyIds)
        ), tenant_income AS MATERIALIZED (
            SELECT management."PropertyId" AS property_id,
                   receipt."EffectiveOn" AS effective_on,
                   allocation."Amount" AS amount
            FROM "TenantLedgerAllocations" allocation
            JOIN "TenantLedgerEntries" receipt
              ON receipt."PortfolioId" = allocation."PortfolioId"
             AND receipt."TenantAccountId" = allocation."TenantAccountId"
             AND receipt."Id" = allocation."CreditEntryId"
            JOIN "TenantLedgerEntries" charge
              ON charge."PortfolioId" = allocation."PortfolioId"
             AND charge."TenantAccountId" = allocation."TenantAccountId"
             AND charge."Id" = allocation."DebitEntryId"
            JOIN "TenantAccounts" account
              ON account."PortfolioId" = allocation."PortfolioId"
             AND account."Id" = allocation."TenantAccountId"
            JOIN "LeaseManagements" management
              ON management."PortfolioId" = account."PortfolioId"
             AND management."Id" = account."LeaseManagementId"
            WHERE allocation."PortfolioId" = @portfolioId
              AND receipt."EntryType" = 'PaymentReceipt'
              AND receipt."EffectiveOn" >= @fromDate
              AND receipt."EffectiveOn" <= @toDate
              AND charge."EntryType" <> 'DepositCharge'
        ), application_income AS MATERIALIZED (
            SELECT entry."PropertyId" AS property_id,
                   entry."EffectiveOn" AS effective_on,
                   CASE WHEN entry."Direction" = 'Increase'
                        THEN entry."Amount" ELSE -entry."Amount" END AS amount
            FROM "ApplicationFinancialEntries" entry
            WHERE entry."PortfolioId" = @portfolioId
              AND entry."EffectiveOn" >= @fromDate
              AND entry."EffectiveOn" <= @toDate
        ), income_facts AS MATERIALIZED (
            SELECT property_id, effective_on, amount FROM tenant_income
            UNION ALL
            SELECT property_id, effective_on, amount FROM application_income
        ), expense_facts AS MATERIALIZED (
            SELECT expense."Id" AS expense_id,
                   CASE allocation."TargetKind"
                       WHEN 'Property' THEN allocation."PropertyId"
                       WHEN 'Unit' THEN unit."PropertyId"
                       ELSE expense."PropertyId"
                   END AS property_id,
                   expense."Category" AS category,
                   COALESCE(expense."PaidAt", expense."IncurredAt") AS effective_at,
                   allocation."Amount" AS amount
            FROM "ExpenseAllocations" allocation
            JOIN "Expenses" expense
              ON expense."PortfolioId" = allocation."PortfolioId"
             AND expense."Id" = allocation."ExpenseId"
             AND expense."DeletedAt" IS NULL
            LEFT JOIN "Units" unit
              ON unit."PortfolioId" = allocation."PortfolioId"
             AND unit."Id" = allocation."UnitId"
             AND unit."DeletedAt" IS NULL
            WHERE allocation."PortfolioId" = @portfolioId
            UNION ALL
            SELECT expense."Id" AS expense_id,
                   expense."PropertyId" AS property_id,
                   expense."Category" AS category,
                   COALESCE(expense."PaidAt", expense."IncurredAt") AS effective_at,
                   expense."Amount" AS amount
            FROM "Expenses" expense
            WHERE expense."PortfolioId" = @portfolioId
              AND expense."DeletedAt" IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ExpenseAllocations" allocation
                  WHERE allocation."PortfolioId" = expense."PortfolioId"
                    AND allocation."ExpenseId" = expense."Id")
        ), operating_expense_facts AS MATERIALIZED (
            SELECT fact.*
            FROM expense_facts fact
            WHERE fact.effective_at >= @fromAt
              AND fact.effective_at <= @toAt
              AND NOT EXISTS (
                  SELECT 1
                  FROM "Loans" loan
                  WHERE fact.property_id IS NOT NULL
                    AND loan."PortfolioId" = @portfolioId
                    AND loan."PropertyId" = fact.property_id
                    AND loan."DeletedAt" IS NULL
                    AND loan."Status" = 0
                    AND ((fact.category = 10 AND loan."EscrowCoversTaxes")
                      OR (fact.category = 4 AND loan."EscrowCoversInsurance")))
        ), effective_loan_payments AS MATERIALIZED (
            SELECT loan."PropertyId" AS property_id,
                   loan."Lender" AS lender,
                   COALESCE(correction."PaidDate", payment."PaidDate") AS paid_date,
                   COALESCE(correction."Status", payment."Status") AS status,
                   COALESCE(correction."TotalAmount", payment."TotalAmount") AS total_amount
            FROM "LoanPayments" payment
            JOIN "Loans" loan
              ON loan."Id" = payment."LoanId"
             AND loan."PortfolioId" = payment."PortfolioId"
             AND loan."DeletedAt" IS NULL
            LEFT JOIN LATERAL (
                SELECT candidate."PaidDate", candidate."Status", candidate."TotalAmount"
                FROM "LoanPaymentCorrections" candidate
                WHERE candidate."LoanPaymentId" = payment."Id"
                  AND candidate."PortfolioId" = payment."PortfolioId"
                ORDER BY candidate."Id" DESC
                LIMIT 1
            ) correction ON TRUE
            WHERE payment."PortfolioId" = @portfolioId
        ), property_income_totals AS (
            SELECT income.property_id, SUM(income.amount) AS income
            FROM income_facts income
            JOIN authorized_properties property ON property.property_id = income.property_id
            GROUP BY income.property_id
        ), property_expense_totals AS (
            SELECT expense.property_id, SUM(expense.amount) AS operating_expenses
            FROM operating_expense_facts expense
            JOIN authorized_properties property ON property.property_id = expense.property_id
            GROUP BY expense.property_id
        ), property_debt_totals AS (
            SELECT payment.property_id, SUM(payment.total_amount) AS debt_service
            FROM effective_loan_payments payment
            JOIN authorized_properties property ON property.property_id = payment.property_id
            WHERE payment.status = 1
              AND payment.paid_date IS NOT NULL
              AND payment.paid_date >= @fromAt
              AND payment.paid_date <= @toAt
            GROUP BY payment.property_id
        ), property_totals AS MATERIALIZED (
            SELECT property.property_id,
                   property.property_name,
                   COALESCE(income.income, 0) AS income,
                   COALESCE(expense.operating_expenses, 0) AS operating_expenses,
                   COALESCE(debt.debt_service, 0) AS debt_service,
                   COALESCE(income.income, 0) - COALESCE(expense.operating_expenses, 0) AS noi,
                   COALESCE(income.income, 0) - COALESCE(expense.operating_expenses, 0)
                       - COALESCE(debt.debt_service, 0) AS cash_flow
            FROM authorized_properties property
            LEFT JOIN property_income_totals income ON income.property_id = property.property_id
            LEFT JOIN property_expense_totals expense ON expense.property_id = property.property_id
            LEFT JOIN property_debt_totals debt ON debt.property_id = property.property_id
            WHERE COALESCE(income.income, 0) <> 0
               OR COALESCE(expense.operating_expenses, 0) <> 0
               OR COALESCE(debt.debt_service, 0) <> 0
        ), expense_detail_totals AS (
            SELECT expense.property_id,
                   CASE expense.category
                       WHEN 0 THEN 'Advertising'
                       WHEN 1 THEN 'AutoTravel'
                       WHEN 2 THEN 'CleaningMaintenance'
                       WHEN 3 THEN 'Commissions'
                       WHEN 4 THEN 'Insurance'
                       WHEN 5 THEN 'LegalProfessional'
                       WHEN 6 THEN 'ManagementFees'
                       WHEN 7 THEN 'MortgageInterest'
                       WHEN 8 THEN 'Repairs'
                       WHEN 9 THEN 'Supplies'
                       WHEN 10 THEN 'Taxes'
                       WHEN 11 THEN 'Utilities'
                       WHEN 12 THEN 'Depreciation'
                       ELSE 'Other'
                   END AS label,
                   SUM(expense.amount) AS amount
            FROM operating_expense_facts expense
            JOIN authorized_properties property ON property.property_id = expense.property_id
            GROUP BY expense.property_id, expense.category
        ), expense_details AS (
            SELECT property_id,
                   jsonb_agg(jsonb_build_object('Label', label, 'Amount', amount)
                             ORDER BY label) AS details
            FROM expense_detail_totals
            GROUP BY property_id
        ), debt_detail_totals AS (
            SELECT payment.property_id, payment.lender AS label, SUM(payment.total_amount) AS amount
            FROM effective_loan_payments payment
            JOIN authorized_properties property ON property.property_id = payment.property_id
            WHERE payment.status = 1
              AND payment.paid_date IS NOT NULL
              AND payment.paid_date >= @fromAt
              AND payment.paid_date <= @toAt
            GROUP BY payment.property_id, payment.lender
        ), debt_details AS (
            SELECT property_id,
                   jsonb_agg(jsonb_build_object('Label', label, 'Amount', amount)
                             ORDER BY label) AS details
            FROM debt_detail_totals
            GROUP BY property_id
        ), month_series AS (
            SELECT generate_series(
                       date_trunc('month', @fromAt),
                       date_trunc('month', @toAt),
                       interval '1 month') AS month_start
        ), monthly_income_totals AS (
            SELECT date_trunc('month', income.effective_on::timestamp) AS month_start,
                   SUM(income.amount) AS income
            FROM income_facts income
            JOIN authorized_properties property ON property.property_id = income.property_id
            GROUP BY date_trunc('month', income.effective_on::timestamp)
        ), monthly_expense_totals AS (
            SELECT date_trunc('month', expense.effective_at) AS month_start,
                   SUM(expense.amount) AS operating_expenses
            FROM operating_expense_facts expense
            JOIN authorized_properties property ON property.property_id = expense.property_id
            GROUP BY date_trunc('month', expense.effective_at)
        ), monthly_debt_totals AS (
            SELECT date_trunc('month', payment.paid_date) AS month_start,
                   SUM(payment.total_amount) AS debt_service
            FROM effective_loan_payments payment
            JOIN authorized_properties property ON property.property_id = payment.property_id
            WHERE payment.status = 1
              AND payment.paid_date IS NOT NULL
              AND payment.paid_date >= @fromAt
              AND payment.paid_date <= @toAt
            GROUP BY date_trunc('month', payment.paid_date)
        ), monthly_rows AS (
            SELECT month.month_start,
                   COALESCE(income.income, 0) AS income,
                   COALESCE(expense.operating_expenses, 0) AS operating_expenses,
                   COALESCE(debt.debt_service, 0) AS debt_service
            FROM month_series month
            LEFT JOIN monthly_income_totals income ON income.month_start = month.month_start
            LEFT JOIN monthly_expense_totals expense ON expense.month_start = month.month_start
            LEFT JOIN monthly_debt_totals debt ON debt.month_start = month.month_start
        ), portfolio_totals AS (
            SELECT COALESCE(SUM(property.income), 0) AS total_income,
                   COALESCE(SUM(property.debt_service), 0) AS total_debt_service
            FROM property_totals property
        ), portfolio_expense_totals AS (
            SELECT COALESCE(SUM(expense.amount), 0) AS total_operating_expenses
            FROM operating_expense_facts expense
            WHERE (expense.property_id IS NOT NULL
                   AND EXISTS (
                       SELECT 1 FROM authorized_properties property
                       WHERE property.property_id = expense.property_id))
               OR (expense.property_id IS NULL AND @includePortfolioExpenses)
        )
        SELECT COALESCE((
                   SELECT jsonb_agg(jsonb_build_object(
                              'PropertyId', property.property_id,
                              'PropertyName', property.property_name,
                              'Income', property.income,
                              'OperatingExpenses', property.operating_expenses,
                              'Noi', property.noi,
                              'DebtService', property.debt_service,
                              'CashFlow', property.cash_flow,
                              'OperatingExpenseDetails', COALESCE(expense.details, '[]'::jsonb),
                              'DebtServiceDetails', COALESCE(debt.details, '[]'::jsonb))
                            ORDER BY property.property_name, property.property_id)
                   FROM property_totals property
                   LEFT JOIN expense_details expense ON expense.property_id = property.property_id
                   LEFT JOIN debt_details debt ON debt.property_id = property.property_id
               ), '[]'::jsonb)::text AS "PropertiesJson",
               COALESCE((
                   SELECT jsonb_agg(jsonb_build_object(
                              'Month', to_char(month.month_start, 'YYYY-MM'),
                              'Income', month.income,
                              'OperatingExpenses', month.operating_expenses,
                              'DebtService', month.debt_service,
                              'CashFlow', month.income - month.operating_expenses - month.debt_service)
                            ORDER BY month.month_start)
                   FROM monthly_rows month
               ), '[]'::jsonb)::text AS "MonthsJson",
               totals.total_income AS "TotalIncome",
               expenses.total_operating_expenses AS "TotalOperatingExpenses",
               totals.total_debt_service AS "TotalDebtService"
        FROM portfolio_totals totals
        CROSS JOIN portfolio_expense_totals expenses;
        """;

    // ── Year-end view: cash flow vs taxable income + rent roll (§11/§18) ────────────────────────────

    public async Task<YearEndViewResponse> GetYearEndAsync(WorkspaceReadScope scope, int year, int? propertyId = null, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var yearRange = new ReportRangeQuery
        {
            From = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            PropertyIds = propertyId.HasValue ? [propertyId.Value] : null,
        };

        // Block 1: true cash flow (rent − opex − debt service, escrow-aware, no depreciation).
        var cashFlow = await GetTrueCashFlowAsync(scope, yearRange, ct);

        // Block 2: taxable income / Schedule E (interest + depreciation in, principal + deposits out).
        var scheduleE = await _scheduleE.GetReportAsync(scope, year, propertyId, ct);

        // Block 3: rent roll — current leases with their past-due balance (DB-side, no N+1).
        var rentRoll = await BuildRentRollAsync(scope, propertyId, ct);

        var authorizedProperties = BuildAuthorizedPropertyQuery(
            scope, yearRange, CapabilityKeys.ReportsRead);
        var dispositionStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dispositionEnd = dispositionStart.AddYears(1);
        var dispositionEntities = await _db.PropertyDispositions
            .AsNoTracking()
            .Include(disposition => disposition.Property)
            .Where(disposition =>
                disposition.PortfolioId == portfolioId &&
                disposition.ClosedOnDate >= dispositionStart &&
                disposition.ClosedOnDate < dispositionEnd &&
                authorizedProperties.Any(property => property.Id == disposition.PropertyId))
            .OrderBy(disposition => disposition.ClosedOnDate)
            .ThenBy(disposition => disposition.Id)
            .Take(ListQuery.MaxTake)
            .ToListAsync(ct);
        var dispositions = dispositionEntities
            .Select(PropertyDispositionResponse.FromEntity)
            .ToList();

        // §18 "see your accountant" caveats — surfaced so the owner never trusts a number the model
        // does not compute. Conditional on what the data suggests, so they are actionable not noise.
        var notes = new List<string>();

        if (scheduleE.Properties.Any(p => p.DepreciationIsFirstYearEstimate))
            notes.Add("A property's first-year depreciation uses the IRS mid-month estimate — confirm the placed-in-service convention with your accountant.");

        if (scheduleE.NetIncome < 0m)
            notes.Add("Taxable income is negative — this is before the passive-loss limitation (Form 8582 / the $25k allowance); your deductible loss may be limited. See your accountant.");

        // Large amounts booked to Repairs may actually be capital improvements (which must be
        // depreciated, not expensed). Flag when any single property's repairs look large.
        var hasLargeRepairs = scheduleE.Properties
            .SelectMany(p => p.ExpensesByCategory)
            .Any(c => c.Category == ScheduleECategory.Repairs.ToString() && c.Amount >= 2_500m);
        if (hasLargeRepairs)
            notes.Add("Large amounts booked to Repairs may be capital improvements (IRS $2,500 de-minimis) that must be depreciated, not expensed. Review with your accountant.");

        if (dispositions.Count > 0)
            notes.Add("Property sale/disposition estimates include sale-year depreciation, gain/loss, and unrecaptured §1250 gain. Confirm final basis and closing costs with your accountant.");
        else
            notes.Add("No property sale/disposition is recorded for this tax year. If a property was sold, add a disposition before handing this packet to your accountant.");
        notes.Add("Owner-occupied / mixed-use properties are not allocated — expenses and depreciation assume 100% rental use.");

        return new YearEndViewResponse
        {
            Year = year,
            CashFlow = cashFlow,
            ScheduleE = scheduleE,
            RentRoll = rentRoll,
            PropertyDispositions = dispositions,
            AccountantNotes = notes,
        };
    }

    /// <summary>
    /// Rent roll over canonical lease-management lifecycle and tenant-account balance projections.
    /// </summary>
    private async Task<IReadOnlyList<YearEndRentRollRow>> BuildRentRollAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(
            scope,
            new ReportRangeQuery { PropertyId = propertyId },
            CapabilityKeys.ReportsRead);
        var query =
            from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                on new { lifecycle.PortfolioId, lifecycle.UnitId }
                equals new { occupancy.PortfolioId, occupancy.UnitId }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { agreement.PortfolioId, agreement.LeaseManagementId }
            join status in _db.LeaseAgreementStatusProjections.AsNoTracking()
                on new { lifecycle.PortfolioId, AgreementId = agreement.Id }
                equals new { status.PortfolioId, status.AgreementId }
            join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                on new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                equals new { balance.PortfolioId, balance.LeaseManagementId }
            where lifecycle.PortfolioId == portfolioId
                && occupancy.IsOccupied
                && occupancy.CurrentLeaseManagementId == lifecycle.LeaseManagementId
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && agreement.FullyExecutedAtUtc != null
                && agreement.VoidedAtUtc == null
                && agreement.DraftCanceledAtUtc == null
                && agreement.GoverningFromOn <= lifecycle.BusinessDate
                && !_db.LeaseAgreements.Any(candidate =>
                    candidate.PortfolioId == agreement.PortfolioId &&
                    candidate.LeaseManagementId == agreement.LeaseManagementId &&
                    candidate.FullyExecutedAtUtc != null &&
                    candidate.VoidedAtUtc == null &&
                    candidate.DraftCanceledAtUtc == null &&
                    candidate.GoverningFromOn <= lifecycle.BusinessDate &&
                    (candidate.GoverningFromOn > agreement.GoverningFromOn ||
                     (candidate.GoverningFromOn == agreement.GoverningFromOn &&
                      candidate.VersionNumber > agreement.VersionNumber) ||
                     (candidate.GoverningFromOn == agreement.GoverningFromOn &&
                      candidate.VersionNumber == agreement.VersionNumber &&
                      candidate.Id > agreement.Id)))
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            select new { lifecycle, management, agreement, status, balance };

        var rows = await query
            .OrderBy(row => row.management.Property!.Name)
            .ThenBy(row => row.management.Unit!.UnitNumber)
            .Select(row => new
            {
                PropertyName = row.management.Property!.Name,
                UnitNumber = row.management.Unit!.UnitNumber,
                TenantName = row.lifecycle.CurrentPrimaryTenantName ?? "Tenant",
                MonthlyRent = row.agreement.BaseRentAmount,
                row.agreement.TermStartOn,
                row.agreement.TermEndOn,
                row.status.AgreementStatus,
                PastDueBalance = row.balance.PastDueAmount,
            })
            .ToListAsync(ct);

        return rows
            .Select(l => new YearEndRentRollRow
            {
                PropertyName = l.PropertyName,
                UnitNumber = l.UnitNumber,
                TenantName = l.TenantName,
                MonthlyRent = l.MonthlyRent,
                LeaseStart = l.TermStartOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                LeaseEnd = (l.TermEndOn ?? l.TermStartOn).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                LeaseStatus = l.AgreementStatus,
                PastDueBalance = l.PastDueBalance,
            })
            .ToList();
    }

    // ── General Ledger (running balance) ───────────────────────────────────────────────────────────

    public async Task<GeneralLedgerResponse> GetGeneralLedgerAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        if (_db.Database.IsNpgsql())
        {
            return await GetGeneralLedgerPostgreSqlAsync(scope, query, ct);
        }

        var portfolioId = scope.PortfolioId;
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        const int expenseEntryKind = 0;
        const int receiptEntryKind = 1;

        var receiptEntries =
            from allocation in _db.TenantLedgerAllocations.AsNoTracking()
            join receipt in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                equals new { receipt.PortfolioId, receipt.TenantAccountId, receipt.Id }
            join charge in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                equals new { charge.PortfolioId, charge.TenantAccountId, charge.Id }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where allocation.PortfolioId == portfolioId
                && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
                && charge.EntryType != TenantLedgerEntryType.DepositCharge
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            group allocation by new
            {
                receipt.Id,
                receipt.EffectiveOn,
                receipt.Description,
                management.PropertyId,
                PropertyName = management.Property!.Name,
                TenantName = lifecycle.CurrentPrimaryTenantName,
            }
            into grouped
            select new GeneralLedgerQueryRow
            {
                Date = grouped.Key.EffectiveOn,
                EntryKind = receiptEntryKind,
                Type = "Receipt",
                Id = grouped.Key.Id,
                Description = grouped.Key.Description,
                Category = nameof(TenantLedgerEntryType.PaymentReceipt),
                PropertyId = grouped.Key.PropertyId,
                PropertyName = grouped.Key.PropertyName,
                Counterparty = grouped.Key.TenantName,
                Amount = grouped.Sum(row => row.Amount),
            };

        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        expenseQuery = expenseQuery.Where(e => e.PropertyId != null &&
            authorizedProperties.Any(property => property.Id == e.PropertyId.Value));

        var expenseEntries = expenseQuery
            .Select(e => new GeneralLedgerQueryRow
            {
                Date = DateOnly.FromDateTime(e.PaidAt ?? e.IncurredAt),
                EntryKind = expenseEntryKind,
                Type = "Expense",
                Id = e.Id,
                Description = e.Description,
                Category = e.Category.ToString(),
                PropertyId = e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                Counterparty = e.Vendor != null ? e.Vendor.Name : null,
                Amount = -e.Amount,
            });

        var fromOn = DateOnly.FromDateTime(from);
        var toOn = DateOnly.FromDateTime(to);
        var ledgerQuery = receiptEntries
            .Concat(expenseEntries)
            .Where(e => e.Date >= fromOn && e.Date <= toOn);

        var entryRows = await ledgerQuery
            .Select(e => new GeneralLedgerQueryRow
            {
                Date = e.Date,
                Type = e.Type,
                Id = e.Id,
                Description = e.Description,
                Category = e.Category,
                PropertyId = e.PropertyId,
                PropertyName = e.PropertyName,
                Counterparty = e.Counterparty,
                Amount = e.Amount,
                RunningBalance = ledgerQuery
                    .Where(x => x.Date < e.Date ||
                                (x.Date == e.Date && x.EntryKind < e.EntryKind) ||
                                (x.Date == e.Date && x.EntryKind == e.EntryKind && x.Id <= e.Id))
                    .Sum(x => (decimal?)x.Amount) ?? 0m,
            })
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Type)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var entries = entryRows.Select(e => new GeneralLedgerEntry
        {
            Date = e.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Type = e.Type,
            Id = e.Id,
            Description = e.Description,
            Category = e.Category,
            PropertyId = e.PropertyId,
            PropertyName = e.PropertyName,
            Counterparty = e.Counterparty,
            Amount = e.Amount,
            RunningBalance = e.RunningBalance,
        }).ToList();

        var totals = await ledgerQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalIncome = g.Sum(e => e.Amount > 0m ? e.Amount : 0m),
                TotalExpense = g.Sum(e => e.Amount < 0m ? -e.Amount : 0m),
            })
            .SingleOrDefaultAsync(ct);

        var totalIncome = totals?.TotalIncome ?? 0m;
        var totalExpense = totals?.TotalExpense ?? 0m;

        return new GeneralLedgerResponse
        {
            From = from,
            To = to,
            Entries = entries,
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            ClosingBalance = totalIncome - totalExpense,
        };
    }

    private async Task<GeneralLedgerResponse> GetGeneralLedgerPostgreSqlAsync(
        WorkspaceReadScope scope,
        ReportRangeQuery query,
        CancellationToken ct)
    {
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var requestedPropertyIds = (query.PropertyIds ?? [])
            .Concat(query.PropertyId is int propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();
        var parameters = new NpgsqlParameter[]
        {
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("sessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("userId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("capabilityKeys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { CapabilityKeys.ReportsRead },
            },
            new("targetKind", NpgsqlDbType.Text)
            {
                Value = CapabilityAuthorizationTargetKind.Property.ToString(),
            },
            new("fromOn", NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(from) },
            new("toOn", NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(to) },
            new("applyPropertyFilter", NpgsqlDbType.Boolean)
            {
                Value = requestedPropertyIds.Length > 0,
            },
            new("propertyIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = requestedPropertyIds,
            },
        };

        var rows = await _db.Database
            .SqlQueryRaw<GeneralLedgerDatabaseRow>(GeneralLedgerPostgreSql, parameters)
            .ToListAsync(ct);

        var entries = rows.Select(row => new GeneralLedgerEntry
        {
            Date = row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Type = row.Type,
            Id = row.Id,
            Description = row.Description,
            Category = row.Category,
            PropertyId = row.PropertyId,
            PropertyName = row.PropertyName,
            Counterparty = row.Counterparty,
            Amount = row.Amount,
            RunningBalance = row.RunningBalance,
        }).ToList();
        var totals = rows.FirstOrDefault();

        return new GeneralLedgerResponse
        {
            From = from,
            To = to,
            Entries = entries,
            TotalIncome = totals?.TotalIncome ?? 0m,
            TotalExpense = totals?.TotalExpense ?? 0m,
            ClosingBalance = totals?.ClosingBalance ?? 0m,
        };
    }

    internal const string GeneralLedgerPostgreSql = """
        WITH effective_scopes AS MATERIALIZED (
            SELECT scope."ScopeKind", scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
                @portfolioId,
                @sessionId,
                @userId,
                @accessContextId,
                @accessRevision,
                @capabilityKeys,
                @targetKind) AS scope
        ),
        authorized_properties AS MATERIALIZED (
            SELECT property."Id", property."Name"
            FROM "Properties" AS property
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (
                    EXISTS (
                        SELECT 1
                        FROM effective_scopes AS scope
                        WHERE scope."ScopeKind" = 'AllProperties')
                    OR EXISTS (
                        SELECT 1
                        FROM effective_scopes AS scope
                        WHERE scope."ScopeKind" = 'SelectedProperties'
                          AND scope."PropertyId" = property."Id")
              )
              AND (NOT @applyPropertyFilter OR property."Id" = ANY(@propertyIds))
        ),
        receipt_entries AS (
            SELECT receipt."EffectiveOn" AS "Date",
                   1 AS "EntryKind",
                   'Receipt'::text AS "Type",
                   receipt."Id" AS "Id",
                   receipt."Description" AS "Description",
                   'PaymentReceipt'::text AS "Category",
                   management."PropertyId" AS "PropertyId",
                   property."Name" AS "PropertyName",
                   lifecycle."CurrentPrimaryTenantName" AS "Counterparty",
                   SUM(allocation."Amount") AS "Amount"
            FROM "TenantLedgerAllocations" AS allocation
            JOIN "TenantLedgerEntries" AS receipt
              ON receipt."PortfolioId" = allocation."PortfolioId"
             AND receipt."TenantAccountId" = allocation."TenantAccountId"
             AND receipt."Id" = allocation."CreditEntryId"
            JOIN "TenantLedgerEntries" AS charge
              ON charge."PortfolioId" = allocation."PortfolioId"
             AND charge."TenantAccountId" = allocation."TenantAccountId"
             AND charge."Id" = allocation."DebitEntryId"
            JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = allocation."PortfolioId"
             AND account."Id" = allocation."TenantAccountId"
            JOIN "LeaseManagements" AS management
              ON management."PortfolioId" = account."PortfolioId"
             AND management."Id" = account."LeaseManagementId"
            JOIN authorized_properties AS property
              ON property."Id" = management."PropertyId"
            LEFT JOIN "vw_lease_management_lifecycle" AS lifecycle
              ON lifecycle."PortfolioId" = management."PortfolioId"
             AND lifecycle."LeaseManagementId" = management."Id"
            WHERE allocation."PortfolioId" = @portfolioId
              AND receipt."EntryType" = 'PaymentReceipt'
              AND charge."EntryType" <> 'DepositCharge'
              AND receipt."EffectiveOn" >= @fromOn
              AND receipt."EffectiveOn" <= @toOn
            GROUP BY receipt."Id",
                     receipt."EffectiveOn",
                     receipt."Description",
                     management."PropertyId",
                     property."Name",
                     lifecycle."CurrentPrimaryTenantName"
        ),
        expense_entries AS (
            SELECT (COALESCE(expense."PaidAt", expense."IncurredAt"))::date AS "Date",
                   0 AS "EntryKind",
                   'Expense'::text AS "Type",
                   expense."Id" AS "Id",
                   expense."Description" AS "Description",
                   expense."Category"::text AS "Category",
                   expense."PropertyId" AS "PropertyId",
                   property."Name" AS "PropertyName",
                   vendor."Name" AS "Counterparty",
                   -expense."Amount" AS "Amount"
            FROM "Expenses" AS expense
            JOIN authorized_properties AS property
              ON property."Id" = expense."PropertyId"
            LEFT JOIN "Vendors" AS vendor
              ON vendor."PortfolioId" = expense."PortfolioId"
             AND vendor."Id" = expense."VendorId"
            WHERE expense."PortfolioId" = @portfolioId
              AND COALESCE(expense."PaidAt", expense."IncurredAt")::date >= @fromOn
              AND COALESCE(expense."PaidAt", expense."IncurredAt")::date <= @toOn
        ),
        ledger_entries AS MATERIALIZED (
            SELECT * FROM receipt_entries
            UNION ALL
            SELECT * FROM expense_entries
        )
        SELECT entry."Date",
               entry."Type",
               entry."Id",
               entry."Description",
               entry."Category",
               entry."PropertyId",
               entry."PropertyName",
               entry."Counterparty",
               entry."Amount",
               SUM(entry."Amount") OVER (
                   ORDER BY entry."Date", entry."EntryKind", entry."Id"
                   ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS "RunningBalance",
               COALESCE(SUM(entry."Amount") FILTER (WHERE entry."Amount" > 0) OVER (), 0) AS "TotalIncome",
               COALESCE(-SUM(entry."Amount") FILTER (WHERE entry."Amount" < 0) OVER (), 0) AS "TotalExpense",
               COALESCE(SUM(entry."Amount") OVER (), 0) AS "ClosingBalance"
        FROM ledger_entries AS entry
        ORDER BY entry."Date", entry."EntryKind", entry."Id";
        """;

    // ── Property P&L Summary ───────────────────────────────────────────────────────────────────────

    public async Task<PropertyProfitAndLossResponse> GetPropertyProfitAndLossAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var propertyQuery = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);
        var hasPropertyFilter = query.PropertyId.HasValue || (query.PropertyIds?.Count > 0);
        var allPropertiesReportsReadAuthority = BuildAllPropertiesAuthorityQuery(scope, CapabilityKeys.ReportsRead);

        var fromOn = DateOnly.FromDateTime(from);
        var toOn = DateOnly.FromDateTime(to);
        var incomeRows =
            from allocation in _db.TenantLedgerAllocations.AsNoTracking()
            join receipt in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
                equals new { receipt.PortfolioId, receipt.TenantAccountId, receipt.Id }
            join charge in _db.TenantLedgerEntries.AsNoTracking()
                on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
                equals new { charge.PortfolioId, charge.TenantAccountId, charge.Id }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where allocation.PortfolioId == portfolioId
                && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
                && receipt.EffectiveOn >= fromOn
                && receipt.EffectiveOn <= toOn
                && charge.EntryType != TenantLedgerEntryType.DepositCharge
            select new { PropertyId = management.PropertyId, allocation.Amount };

        var incomeByPropertyQuery = incomeRows
            .GroupBy(row => row.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(row => (decimal?)row.Amount) });

        // Expense rows stay keyed to properties. Portfolio-scope unassigned expenses are included only
        // in unfiltered portfolio totals below, so they never appear on a selected-property report.
        var expenseProjection = FinancialReportProjections.BuildExpenseAllocationProjection(_db, portfolioId)
            .Where(expense => expense.EffectiveAt >= from && expense.EffectiveAt <= to);
        var expenseByPropertyQuery = expenseProjection
            .Where(expense => expense.PropertyId != null)
            .GroupBy(expense => expense.PropertyId!.Value)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(expense => (decimal?)expense.Amount) });

        var rows = await (
                from property in propertyQuery
                join income in incomeByPropertyQuery on property.Id equals income.PropertyId into incomeJoin
                from income in incomeJoin.DefaultIfEmpty()
                join expense in expenseByPropertyQuery on property.Id equals expense.PropertyId into expenseJoin
                from expense in expenseJoin.DefaultIfEmpty()
                orderby property.Name
                select new
                {
                    property.Id,
                    property.Name,
                    Income = income.Total ?? 0m,
                    Expense = expense.Total ?? 0m,
                })
            .Select(row => new PropertyProfitAndLossRow
            {
                PropertyId = row.Id,
                PropertyName = row.Name,
                Income = row.Income,
                Expense = row.Expense,
                Net = row.Income - row.Expense,
            })
            .ToListAsync(ct);

        var propertyIds = propertyQuery.Select(p => p.Id);

        var totalIncome = await incomeRows
            .Where(row => propertyIds.Contains(row.PropertyId))
            .SumAsync(row => (decimal?)row.Amount, ct) ?? 0m;

        var totalExpense = await expenseProjection
            .Where(expense =>
                (expense.PropertyId != null && propertyIds.Contains(expense.PropertyId.Value)) ||
                (expense.PropertyId == null && !hasPropertyFilter && allPropertiesReportsReadAuthority.Any()))
            .SumAsync(expense => (decimal?)expense.Amount, ct) ?? 0m;

        return new PropertyProfitAndLossResponse
        {
            From = from,
            To = to,
            Rows = rows,
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            TotalNet = totalIncome - totalExpense,
        };
    }

    // ── Occupancy / Vacancy ────────────────────────────────────────────────────────────────────────

    public async Task<OccupancyResponse> GetOccupancyAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var propertyQuery = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        // The projection has exactly one row per live Unit. Keep both per-property and portfolio
        // aggregation in PostgreSQL; the mutable Unit.Status column is not part of this read path.
        var perPropertyQuery = propertyQuery
            .Select(p => new
            {
                p.Id,
                p.Name,
                TotalUnits = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id),
                OccupiedUnits = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.PropertyId == p.Id
                    && occupancy.IsOccupied),
            });

        var occRows = await perPropertyQuery
            .OrderBy(p => p.Name)
            .Select(p => new OccupancyRow
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                TotalUnits = p.TotalUnits,
                OccupiedUnits = p.OccupiedUnits,
                VacantUnits = p.TotalUnits - p.OccupiedUnits,
                OccupancyPercent = p.TotalUnits == 0
                    ? 0m
                    : Math.Round(p.OccupiedUnits * 100m / p.TotalUnits, 1),
            })
            .ToListAsync(ct);

        var totals = await perPropertyQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalUnits = g.Sum(p => p.TotalUnits),
                OccupiedUnits = g.Sum(p => p.OccupiedUnits),
            })
            .SingleOrDefaultAsync(ct);

        var totalUnits = totals?.TotalUnits ?? 0;
        var occupiedUnits = totals?.OccupiedUnits ?? 0;

        return new OccupancyResponse
        {
            GeneratedAt = _timeProvider.UtcNow(),
            Rows = occRows,
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            VacantUnits = totalUnits - occupiedUnits,
            OccupancyPercent = Percent(occupiedUnits, totalUnits),
        };
    }

    /// <summary>Occupancy percentage 0–100, rounded to 1 decimal. Returns 0 when there are no units.</summary>
    internal static decimal Percent(int occupied, int total) =>
        total == 0 ? 0m : Math.Round(occupied * 100m / total, 1);

    // ── Lease Expirations / Renewals Due ───────────────────────────────────────────────────────────

    public async Task<LeaseExpirationsResponse> GetLeaseExpirationsAsync(WorkspaceReadScope scope, ReportRangeQuery query, int days, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var window = days > 0 ? days : DefaultExpirationWindowDays;
        var asOf = DateOnly.FromDateTime(_timeProvider.UtcNow());
        var cutoff = asOf.AddDays(window);
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        var q =
            from status in _db.LeaseAgreementStatusProjections.AsNoTracking()
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { status.PortfolioId, Id = status.AgreementId }
                equals new { agreement.PortfolioId, agreement.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { agreement.PortfolioId, Id = agreement.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where status.PortfolioId == portfolioId
                && status.IsGoverning
                && agreement.TermEndOn != null
                && agreement.TermEndOn <= cutoff
            select new { status, agreement, management, lifecycle };

        q = q.Where(row => authorizedProperties.Any(property =>
            property.Id == row.management.PropertyId));

        var summary = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                LeaseCount = g.Count(),
                TotalMonthlyRent = g.Sum(row => row.agreement.BaseRentAmount),
            })
            .FirstOrDefaultAsync(ct);

        var leases = await q
            .OrderBy(row => row.agreement.TermEndOn)
            .Select(row => new LeaseExpirationRow
            {
                LeaseManagementId = row.management.Id,
                AgreementId = row.agreement.Id,
                RelationshipNumber = row.management.RelationshipNumber,
                AgreementNumber = row.agreement.AgreementNumber,
                PropertyId = row.management.PropertyId,
                PropertyName = row.management.Property!.Name,
                UnitNumber = row.management.Unit!.UnitNumber,
                TenantId = row.lifecycle.CurrentPrimaryTenantId,
                TenantName = row.lifecycle.CurrentPrimaryTenantName ?? "Tenant",
                MonthlyRent = row.agreement.BaseRentAmount,
                EndOn = row.agreement.TermEndOn!.Value,
                // Date arithmetic is finalized after the filtered/sorted SQL query; Npgsql does not
                // translate DateOnly.DayNumber reliably across supported provider versions.
                DaysUntilExpiry = 0,
                StatusName = row.status.AgreementStatus,
            })
            .ToListAsync(ct);

        foreach (var lease in leases)
            lease.DaysUntilExpiry = lease.EndOn.DayNumber - asOf.DayNumber;

        return new LeaseExpirationsResponse
        {
            AsOf = asOf,
            WindowDays = window,
            Rows = leases,
            LeaseCount = summary?.LeaseCount ?? 0,
            TotalMonthlyRent = summary?.TotalMonthlyRent ?? 0m,
        };
    }

    private sealed class RentRollDatabaseRow
    {
        public string PropertiesJson { get; set; } = "[]";
        public string RowsJson { get; set; } = "[]";
        public int UnitCount { get; set; }
        public int PortfolioLeaseCount { get; set; }
        public decimal PortfolioBaseRent { get; set; }
        public decimal PortfolioMonthlyRent { get; set; }
        public decimal PortfolioSecurityDeposit { get; set; }
        public decimal PortfolioDepositHeld { get; set; }
        public decimal PortfolioCurrentBalance { get; set; }
        public int LegacyLeaseCount { get; set; }
        public decimal LegacyMonthlyRent { get; set; }
        public decimal LegacySecurityDeposit { get; set; }
    }

    private sealed class AgedReceivablesDatabaseRow
    {
        public string PropertiesJson { get; set; } = "[]";
        public string RowsJson { get; set; } = "[]";
        public decimal Current { get; set; }
        public decimal Days31To60 { get; set; }
        public decimal Days61To90 { get; set; }
        public decimal Over90 { get; set; }
        public decimal TotalOutstanding { get; set; }
    }

    private static IReadOnlyList<T> DeserializeReportJson<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("PostgreSQL returned an invalid report JSON aggregate.");

    private sealed class RentLedgerDatabaseRow
    {
        public string LeasesJson { get; set; } = "[]";
        public decimal TotalCharged { get; set; }
        public decimal TotalCredits { get; set; }
        public decimal TotalBalance { get; set; }
    }

    private sealed class TrueCashFlowDatabaseRow
    {
        public string PropertiesJson { get; set; } = "[]";
        public string MonthsJson { get; set; } = "[]";
        public decimal TotalIncome { get; set; }
        public decimal TotalOperatingExpenses { get; set; }
        public decimal TotalDebtService { get; set; }
    }

    private sealed class GeneralLedgerQueryRow
    {
        public DateOnly Date { get; set; }
        public int EntryKind { get; set; }
        public string Type { get; set; } = string.Empty;
        public long Id { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public string? Counterparty { get; set; }
        public decimal Amount { get; set; }
        public decimal RunningBalance { get; set; }
    }

    private sealed class GeneralLedgerDatabaseRow
    {
        public DateOnly Date { get; set; }
        public string Type { get; set; } = string.Empty;
        public long Id { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public string? Counterparty { get; set; }
        public decimal Amount { get; set; }
        public decimal RunningBalance { get; set; }
        public decimal TotalIncome { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal ClosingBalance { get; set; }
    }

    // ── Security Deposit Register ──────────────────────────────────────────────────────────────────

    public async Task<SecurityDepositRegisterResponse> GetSecurityDepositRegisterAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);
        var skip = query.NormalizedSkip;
        var take = query.NormalizedTake;

        var rowsQuery = FinancialReportProjections.BuildSecurityDepositRegisterProjection(_db, portfolioId)
            .Where(row => authorizedProperties.Any(property =>
                property.Id == row.PropertyId));
        var orderedRows = ApplySecurityDepositSort(rowsQuery, query);
        var totalCount = await rowsQuery.CountAsync(ct);

        var rows = await orderedRows
            .Skip(skip)
            .Take(take)
            .Select(h => new SecurityDepositRegisterRow
            {
                DepositId = h.DepositId,
                LeaseManagementId = h.LeaseManagementId,
                RelationshipNumber = h.RelationshipNumber,
                PropertyId = h.PropertyId,
                PropertyName = h.PropertyName,
                UnitNumber = h.UnitNumber,
                TenantName = h.TenantName ?? string.Empty,
                Held = h.Held,
                Deductions = h.Deductions,
                Returned = h.Returned,
                CurrentBalance = h.CurrentBalance,
                Status = h.Status,
                StatusName = FormatSecurityDepositStatus(h.Status),
                HeldAt = h.HeldAt,
                ReturnedAt = h.ReturnedAt,
            })
            .ToListAsync(ct);

        var totals = await rowsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalHeld = g.Sum(x => x.Held),
                TotalDeductions = g.Sum(x => x.Deductions),
                TotalReturned = g.Sum(x => x.Returned),
                TotalCurrentBalance = g.Sum(x => x.CurrentBalance),
            })
            .FirstOrDefaultAsync(ct);

        return new SecurityDepositRegisterResponse
        {
            GeneratedAt = _timeProvider.UtcNow(),
            Rows = rows,
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
            Sort = query.NormalizedSort,
            TotalHeld = totals?.TotalHeld ?? 0m,
            TotalDeductions = totals?.TotalDeductions ?? 0m,
            TotalReturned = totals?.TotalReturned ?? 0m,
            TotalCurrentBalance = totals?.TotalCurrentBalance ?? 0m,
        };
    }

    private static IQueryable<SecurityDepositRegisterProjection> ApplySecurityDepositSort(
        IQueryable<SecurityDepositRegisterProjection> query,
        ReportRangeQuery rangeQuery)
    {
        var ordered = rangeQuery.SortField switch
        {
            "tenant" or "tenantname" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.TenantName)
                : query.OrderBy(row => row.TenantName),
            "held" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.Held)
                : query.OrderBy(row => row.Held),
            "deductions" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.Deductions)
                : query.OrderBy(row => row.Deductions),
            "returned" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.Returned)
                : query.OrderBy(row => row.Returned),
            "balance" or "currentbalance" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.CurrentBalance)
                : query.OrderBy(row => row.CurrentBalance),
            "status" => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.Status)
                : query.OrderBy(row => row.Status),
            _ => rangeQuery.SortDescending
                ? query.OrderByDescending(row => row.PropertyName)
                : query.OrderBy(row => row.PropertyName),
        };

        return ordered
            .ThenBy(row => row.UnitNumber)
            .ThenBy(row => row.DepositId);
    }

    private static string FormatSecurityDepositStatus(string status) => status switch
    {
        "NotFunded" => "Not funded",
        "PartiallyReturned" => "Partially returned",
        _ => status,
    };

    // ── Vendor 1099 & Payments ─────────────────────────────────────────────────────────────────────

    public async Task<Vendor1099Response> GetVendor1099Async(WorkspaceReadScope scope, int year, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(
            scope, new ReportRangeQuery(), CapabilityKeys.MoneyOwnerReportsRead);
        // Total paid per vendor = expenses to that vendor whose PaidAt falls in the year. Mirrors the
        // accounting reports' 1099 logic but scoped to a single tax year (the 1099 reporting period).
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = yearStart.AddYears(1);

        var vendorRowsQuery = _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId && v.Expenses.Any(e =>
                e.PaidAt >= yearStart && e.PaidAt < yearEnd &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId.Value)))
            .OrderBy(v => v.Name)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.TaxId,
                v.Is1099Eligible,
                v.W9OnFile,
                TotalPaid = v.Expenses
                    .Where(e => e.PaidAt >= yearStart && e.PaidAt < yearEnd &&
                        e.PropertyId != null &&
                        authorizedProperties.Any(property => property.Id == e.PropertyId.Value))
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            });

        var rows = await vendorRowsQuery
            .Select(v => new Vendor1099Row
            {
                VendorId = v.Id,
                VendorName = v.Name,
                TaxId = v.TaxId,
                TotalPaid = v.TotalPaid,
                Is1099Eligible = v.Is1099Eligible,
                W9OnFile = v.W9OnFile,
                NeedsW9 = v.Is1099Eligible && !v.W9OnFile,
                Needs1099Review = v.Is1099Eligible && v.TotalPaid >= Vendor1099Threshold,
            })
            .ToListAsync(ct);

        var summary = await vendorRowsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalPaid = g.Sum(v => v.TotalPaid),
                NeedsW9Count = g.Count(v => v.Is1099Eligible && !v.W9OnFile),
            })
            .FirstOrDefaultAsync(ct);

        return new Vendor1099Response
        {
            Year = year,
            Rows = rows,
            TotalPaid = summary?.TotalPaid ?? 0m,
            NeedsW9Count = summary?.NeedsW9Count ?? 0,
            Threshold = Vendor1099Threshold,
        };
    }

    // ── Owner Distributions ────────────────────────────────────────────────────────────────────────

    public async Task<OwnerDistributionsResponse> GetOwnerDistributionsAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(
            scope, new ReportRangeQuery(), CapabilityKeys.MoneyOwnerReportsRead);
        var startOn = new DateOnly(year, 1, 1);
        var endOn = startOn.AddYears(1);
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var activeOwnershipAt = _timeProvider.GetUtcNow().UtcDateTime;

        var propertyNetRows =
            from ownership in _db.PropertyOwnerships.AsNoTracking()
            join property in authorizedProperties on ownership.PropertyId equals property.Id
            where ownership.PortfolioId == portfolioId
                && ownership.EffectiveFromUtc < end
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > start)
            select new
            {
                OwnerId = ownership.OwnerEntityId,
                OwnerName = ownership.OwnerEntity!.Name,
                PropertyId = property.Id,
                RentalIncome = (
                    from allocation in _db.TenantLedgerAllocations.AsNoTracking()
                    join credit in _db.TenantLedgerEntries.AsNoTracking()
                        on new { allocation.PortfolioId, Id = allocation.CreditEntryId }
                        equals new { credit.PortfolioId, credit.Id }
                    join debit in _db.TenantLedgerEntries.AsNoTracking()
                        on new { allocation.PortfolioId, Id = allocation.DebitEntryId }
                        equals new { debit.PortfolioId, debit.Id }
                    join account in _db.TenantAccounts.AsNoTracking()
                        on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                        equals new { account.PortfolioId, account.Id }
                    join management in _db.LeaseManagements.AsNoTracking()
                        on new { account.PortfolioId, Id = account.LeaseManagementId }
                        equals new { management.PortfolioId, management.Id }
                    where allocation.PortfolioId == portfolioId
                        && management.PropertyId == property.Id
                        && credit.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && credit.EffectiveOn >= startOn
                        && credit.EffectiveOn < endOn
                        && credit.PostedAtUtc >= ownership.EffectiveFromUtc
                        && (ownership.EffectiveToUtc == null
                            || credit.PostedAtUtc < ownership.EffectiveToUtc)
                        && debit.EntryType == TenantLedgerEntryType.RentCharge
                    select (decimal?)(allocation.Amount * ownership.OwnershipSharePercent / 100m)).Sum() ?? 0m,
                Expenses = _db.Expenses
                    .Where(expense =>
                        expense.PortfolioId == portfolioId &&
                        expense.PropertyId == property.Id &&
                        expense.Status == ExpenseStatus.Paid &&
                        (expense.PaidAt ?? expense.IncurredAt) >= start &&
                        (expense.PaidAt ?? expense.IncurredAt) < end &&
                        (expense.PaidAt ?? expense.IncurredAt) >= ownership.EffectiveFromUtc &&
                        (ownership.EffectiveToUtc == null
                            || (expense.PaidAt ?? expense.IncurredAt) < ownership.EffectiveToUtc))
                    .Sum(expense =>
                        (decimal?)(expense.Amount * ownership.OwnershipSharePercent / 100m)) ?? 0m,
                ManagementFeePercent = property.ManagementFeePercent ?? 0m,
            };

        var summaries = propertyNetRows
            .GroupBy(property => new { property.OwnerId, property.OwnerName })
            .Select(group => new
            {
                group.Key.OwnerId,
                group.Key.OwnerName,
                NetToOwner = group.Sum(property =>
                    property.RentalIncome -
                    property.Expenses -
                    (property.RentalIncome * property.ManagementFeePercent / 100m)),
                TotalDistributed = _db.OwnerDistributions
                    .Where(distribution =>
                        distribution.PortfolioId == portfolioId &&
                        distribution.OwnerEntityId == group.Key.OwnerId &&
                        distribution.Status == OwnerDistributionStatus.Approved &&
                        distribution.Date >= start &&
                        distribution.Date < end &&
                        (distribution.PropertyId != null
                            ? authorizedProperties.Any(property =>
                                property.Id == distribution.PropertyId.Value)
                            : _db.PropertyOwnerships.AsNoTracking().Any(ownership =>
                                  ownership.PortfolioId == portfolioId &&
                                  ownership.OwnerEntityId == group.Key.OwnerId &&
                                  ownership.Property!.DeletedAt == null &&
                                  ownership.EffectiveFromUtc <= activeOwnershipAt &&
                                  (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > activeOwnershipAt) &&
                                  authorizedProperties.Any(property => property.Id == ownership.PropertyId))
                              && !_db.PropertyOwnerships.AsNoTracking().Any(ownership =>
                                  ownership.PortfolioId == portfolioId &&
                                  ownership.OwnerEntityId == group.Key.OwnerId &&
                                  ownership.Property!.DeletedAt == null &&
                                  ownership.EffectiveFromUtc <= activeOwnershipAt &&
                                  (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > activeOwnershipAt) &&
                                  !authorizedProperties.Any(property => property.Id == ownership.PropertyId))))
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
            });

        var rowsWithTotals = await summaries
            .OrderBy(summary => summary.OwnerName)
            .Select(summary => new
            {
                OwnerId = summary.OwnerId,
                OwnerName = summary.OwnerName,
                NetToOwner = summary.NetToOwner,
                TotalDistributed = summary.TotalDistributed,
                Undistributed = summary.NetToOwner - summary.TotalDistributed,
                PortfolioTotalNetToOwners = summaries.Sum(total => total.NetToOwner),
                PortfolioTotalDistributed = summaries.Sum(total => total.TotalDistributed),
            })
            .ToListAsync(ct);

        var totalNetToOwners = rowsWithTotals.FirstOrDefault()?.PortfolioTotalNetToOwners ?? 0m;
        var totalDistributed = rowsWithTotals.FirstOrDefault()?.PortfolioTotalDistributed ?? 0m;
        var rows = rowsWithTotals
            .Select(row => new OwnerDistributionRow
            {
                OwnerId = row.OwnerId,
                OwnerName = row.OwnerName,
                NetToOwner = row.NetToOwner,
                TotalDistributed = row.TotalDistributed,
                Undistributed = row.Undistributed,
            })
            .ToList();

        return new OwnerDistributionsResponse
        {
            Year = year,
            Rows = rows,
            TotalNetToOwners = totalNetToOwners,
            TotalDistributed = totalDistributed,
            TotalUndistributed = totalNetToOwners - totalDistributed,
        };
    }

    // ── Work Orders / Maintenance ──────────────────────────────────────────────────────────────────

    public async Task<WorkOrderReportResponse> GetWorkOrdersAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        var q = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId &&
                        w.RequestedAt >= from && w.RequestedAt <= to &&
                        authorizedProperties.Any(property => property.Id == w.PropertyId));

        var rows = await q
            .OrderByDescending(w => w.RequestedAt)
            .Select(w => new WorkOrderReportRow
            {
                WorkOrderId = w.Id,
                PropertyId = w.PropertyId,
                PropertyName = w.Property!.Name,
                UnitNumber = w.Unit != null ? w.Unit.UnitNumber : null,
                Title = w.Title,
                Category = w.Category,
                Priority = w.Priority,
                PriorityName = w.Priority.ToString(),
                Status = w.Status,
                StatusName = w.Status.ToString(),
                VendorName = w.Vendor != null ? w.Vendor.Name : null,
                RequestedAt = w.RequestedAt,
                CompletedAt = w.CompletedAt,
                ActualCost = w.ActualCost,
            })
            .ToListAsync(ct);

        var summary = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                OpenCount = g.Count(w =>
                    w.Status == WorkOrderStatus.New ||
                    w.Status == WorkOrderStatus.Scheduled ||
                    w.Status == WorkOrderStatus.InProgress ||
                    w.Status == WorkOrderStatus.WaitingParts ||
                    w.Status == WorkOrderStatus.OnHold),
                CompletedCount = g.Count(w => w.Status == WorkOrderStatus.Completed),
                TotalActualCost = g.Sum(w => w.ActualCost) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);

        return new WorkOrderReportResponse
        {
            From = from,
            To = to,
            Rows = rows,
            TotalCount = summary?.TotalCount ?? 0,
            OpenCount = summary?.OpenCount ?? 0,
            CompletedCount = summary?.CompletedCount ?? 0,
            TotalActualCost = summary?.TotalActualCost ?? 0m,
        };
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective set of property ids to filter on, validated to be in-portfolio (IDOR guard),
    /// or <c>null</c> when no property filter was supplied (meaning "all properties in the portfolio").
    /// Accepts either a single <c>propertyId</c> or a <c>propertyIds</c> list; out-of-portfolio ids are
    /// silently dropped so a caller can never read another tenant's data by id.
    /// </summary>
    private IQueryable<Property> BuildAuthorizedPropertyQuery(
        WorkspaceReadScope scope,
        ReportRangeQuery query,
        string capabilityKey)
    {
        var requested = (query.PropertyIds ?? [])
            .Concat(query.PropertyId is { } propertyId ? [propertyId] : [])
            .Distinct()
            .ToArray();
        var properties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                capabilityKey,
                _timeProvider.GetUtcNow().UtcDateTime);
        return requested.Length == 0
            ? properties
            : properties.Where(property => requested.Contains(property.Id));
    }

    private IQueryable<int> BuildAllPropertiesAuthorityQuery(
        WorkspaceReadScope scope,
        string capabilityKey) =>
        _db.AuthorizedWorkspaceAssignments(
                scope,
                [capabilityKey],
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.GetUtcNow().UtcDateTime)
            .Select(_ => 1);

    /// <summary>
    /// Resolves the [from, to] window for a range report, coercing both ends to UTC for Npgsql. Missing
    /// bounds default to a sensible window: <c>from</c> → start of the current year, <c>to</c> → now.
    /// The <c>to</c> end is inclusive (extended to end-of-day) so a same-day "from == to" still matches.
    /// </summary>
    internal static (DateTime From, DateTime To) ResolveRange(ReportRangeQuery query, DateTime now)
    {
        var from = (query.From?.ToUtc()) ?? new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toRaw = (query.To?.ToUtc()) ?? now;

        // Treat an explicit `to` as inclusive of the whole day when it is a midnight date.
        var to = query.To.HasValue && toRaw.TimeOfDay == TimeSpan.Zero
            ? toRaw.AddDays(1).AddTicks(-1)
            : toRaw;

        if (to < from)
            (from, to) = (to, from);

        return (from, to);
    }
}
