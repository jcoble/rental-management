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
                            Key = "general-ledger",
                            Title = "General Ledger / Account Transactions",
                            Description = "Every payment and expense in date order with a running balance.",
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
                            Description = "Current snapshot of the governing agreement for every occupied unit: tenant, rent, deposit, term, status.",
                            Endpoint = "/api/v1/reports/rent-roll",
                            Params = [ReportParamKeys.PropertyIds],
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
                            Description = "Full per-owner statement: income, expenses, management fee, net distribution.",
                            Endpoint = "/api/v1/accounting/owner-statement",
                            Params = [ReportParamKeys.OwnerId, ReportParamKeys.Year],
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
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);

        var q =
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
            join account in _db.TenantAccounts.AsNoTracking()
                on new { lifecycle.PortfolioId, LeaseManagementId = lifecycle.LeaseManagementId }
                equals new { account.PortfolioId, account.LeaseManagementId }
            join agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
                on new { lifecycle.PortfolioId, AgreementId = agreement.Id }
                equals new { agreementStatus.PortfolioId, agreementStatus.AgreementId }
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
            select new { lifecycle, management, agreement, account, agreementStatus };

        q = q.Where(row => authorizedProperties.Any(property =>
            property.Id == row.management.PropertyId));

        var totals = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                LeaseCount = g.Count(),
                TotalMonthlyRent = g.Sum(row => row.agreement.BaseRentAmount +
                    (_db.LeaseAddendumFinancialEffects
                        .Where(effect => effect.PortfolioId == row.agreement.PortfolioId
                            && effect.EffectType == LeaseAddendumFinancialEffectType.RecurringRentDelta
                            && _db.LeaseAddendumStatusProjections.Any(status =>
                                status.PortfolioId == effect.PortfolioId
                                && status.LeaseAddendumId == effect.LeaseAddendumId
                                && status.LeaseManagementId == row.management.Id
                                && status.AddendumStatus == "Active"))
                        .Sum(effect => (decimal?)effect.Amount) ?? 0m)),
                TotalSecurityDeposit = g.Sum(row => row.agreement.SecurityDepositObligation +
                    (_db.LeaseAddendumFinancialEffects
                        .Where(effect => effect.PortfolioId == row.agreement.PortfolioId
                            && effect.EffectType == LeaseAddendumFinancialEffectType.DepositObligationDelta
                            && _db.LeaseAddendumStatusProjections.Any(status =>
                                status.PortfolioId == effect.PortfolioId
                                && status.LeaseAddendumId == effect.LeaseAddendumId
                                && status.LeaseManagementId == row.management.Id
                                && status.AddendumStatus == "Active"))
                        .Sum(effect => (decimal?)effect.Amount) ?? 0m)),
            })
            .SingleOrDefaultAsync(ct);

        var rows = await q
            .OrderBy(row => row.management.Property!.Name)
            .ThenBy(row => row.management.Unit!.UnitNumber)
            .Select(row => new RentRollRow
            {
                LeaseManagementId = row.management.Id,
                TenantAccountId = row.account.Id,
                AgreementId = row.agreement.Id,
                RelationshipNumber = row.management.RelationshipNumber,
                AgreementNumber = row.agreement.AgreementNumber,
                PropertyId = row.management.PropertyId,
                PropertyName = row.management.Property!.Name,
                UnitId = row.management.UnitId,
                UnitNumber = row.management.Unit!.UnitNumber,
                TenantId = row.lifecycle.CurrentPrimaryTenantId,
                TenantName = row.lifecycle.CurrentPrimaryTenantName ?? "Tenant",
                MonthlyRent = row.agreement.BaseRentAmount +
                    (_db.LeaseAddendumFinancialEffects
                        .Where(effect => effect.PortfolioId == row.agreement.PortfolioId
                            && effect.EffectType == LeaseAddendumFinancialEffectType.RecurringRentDelta
                            && _db.LeaseAddendumStatusProjections.Any(status =>
                                status.PortfolioId == effect.PortfolioId
                                && status.LeaseAddendumId == effect.LeaseAddendumId
                                && status.LeaseManagementId == row.management.Id
                                && status.AddendumStatus == "Active"))
                        .Sum(effect => (decimal?)effect.Amount) ?? 0m),
                SecurityDeposit = row.agreement.SecurityDepositObligation +
                    (_db.LeaseAddendumFinancialEffects
                        .Where(effect => effect.PortfolioId == row.agreement.PortfolioId
                            && effect.EffectType == LeaseAddendumFinancialEffectType.DepositObligationDelta
                            && _db.LeaseAddendumStatusProjections.Any(status =>
                                status.PortfolioId == effect.PortfolioId
                                && status.LeaseAddendumId == effect.LeaseAddendumId
                                && status.LeaseManagementId == row.management.Id
                                && status.AddendumStatus == "Active"))
                        .Sum(effect => (decimal?)effect.Amount) ?? 0m),
                StartOn = row.agreement.TermStartOn,
                EndOn = row.agreement.TermEndOn,
                StatusName = row.agreementStatus.AgreementStatus,
            })
            .ToListAsync(ct);

        return new RentRollResponse
        {
            GeneratedAt = _timeProvider.UtcNow(),
            Rows = rows,
            LeaseCount = totals?.LeaseCount ?? 0,
            TotalMonthlyRent = totals?.TotalMonthlyRent ?? 0m,
            TotalSecurityDeposit = totals?.TotalSecurityDeposit ?? 0m,
        };
    }

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
            new("utcNow", NpgsqlDbType.TimestampTz) { Value = _timeProvider.UtcNow() },
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
            TotalBalance = row.TotalCharged - row.TotalCredits,
        };
    }

    internal static IReadOnlyList<RentLedgerLease> DeserializeRentLedgerLeases(string json) =>
        JsonSerializer.Deserialize<RentLedgerLease[]>(json, RentLedgerJsonOptions)
        ?? throw new InvalidOperationException("PostgreSQL returned an invalid rent-ledger JSON aggregate.");

    private static readonly JsonSerializerOptions RentLedgerJsonOptions = new(JsonSerializerDefaults.Web);

    internal const string RentLedgerSql = """
        WITH authorized_managements AS MATERIALIZED (
            SELECT management."Id",
                   management."PortfolioId",
                   management."RelationshipNumber",
                   management."PropertyId",
                   management."UnitId"
            FROM "LeaseManagements" AS management
            WHERE management."PortfolioId" = @portfolioId
              AND EXISTS (
                  SELECT 1
                  FROM "AuthSessions" AS session
                  JOIN "WorkspaceAccessContexts" AS context
                    ON context."Id" = session."ActiveAccessContextId"
                   AND context."UserId" = session."UserId"
                  JOIN "WorkspaceMemberships" AS membership
                    ON membership."AccessContextId" = context."Id"
                   AND membership."PortfolioId" = context."PortfolioId"
                  JOIN "MembershipRoleAssignments" AS assignment
                    ON assignment."WorkspaceMembershipId" = membership."Id"
                   AND assignment."PortfolioId" = membership."PortfolioId"
                  WHERE session."Id" = @sessionId
                    AND session."UserId" = @userId
                    AND session."ActiveAccessContextId" = @accessContextId
                    AND session."Status" = 'Active'
                    AND session."RevokedAtUtc" IS NULL
                    AND session."ExpiresAtUtc" > @utcNow
                    AND context."Id" = @accessContextId
                    AND context."PortfolioId" = @portfolioId
                    AND context."AccessRevision" = @accessRevision
                    AND context."Status" = 'Active'
                    AND context."SuspendedAtUtc" IS NULL
                    AND context."RevokedAtUtc" IS NULL
                    AND membership."Status" = 'Active'
                    AND membership."SuspendedAtUtc" IS NULL
                    AND membership."RevokedAtUtc" IS NULL
                    AND membership."EffectiveFromUtc" <= @utcNow
                    AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > @utcNow)
                    AND assignment."Status" = 'Active'
                    AND assignment."SuspendedAtUtc" IS NULL
                    AND assignment."RevokedAtUtc" IS NULL
                    AND assignment."EffectiveFromUtc" <= @utcNow
                    AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > @utcNow)
                    AND EXISTS (
                        SELECT 1
                        FROM "RoleProfileCapabilities" AS role_capability
                        JOIN "CapabilityDefinitions" AS capability
                          ON capability."Id" = role_capability."CapabilityDefinitionId"
                        WHERE role_capability."RoleProfileId" = assignment."RoleProfileId"
                          AND capability."Key" = 'reports.read'
                          AND capability."AuthorizationTargetKind" = 'Property')
                    AND EXISTS (
                        SELECT 1
                        FROM "RoleProfileCapabilities" AS role_capability
                        JOIN "CapabilityDefinitions" AS capability
                          ON capability."Id" = role_capability."CapabilityDefinitionId"
                        WHERE role_capability."RoleProfileId" = assignment."RoleProfileId"
                          AND capability."Key" = 'money.balances.read'
                          AND capability."AuthorizationTargetKind" = 'Property')
                    AND (assignment."ScopeKind" = 'AllProperties'
                         OR (assignment."ScopeKind" = 'SelectedProperties'
                             AND EXISTS (
                                 SELECT 1
                                 FROM "MembershipRoleAssignmentProperties" AS selected_property
                                 WHERE selected_property."MembershipRoleAssignmentId" = assignment."Id"
                                   AND selected_property."PortfolioId" = assignment."PortfolioId"
                                   AND selected_property."PropertyId" = management."PropertyId")))
              )
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
                   entry."Amount",
                   SUM(CASE WHEN entry."Direction" = 'Debit' THEN entry."Amount" ELSE -entry."Amount" END)
                       OVER (PARTITION BY management."Id"
                             ORDER BY entry."EffectiveOn",
                                      CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END,
                                      entry."Id"
                             ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS "RunningBalance"
            FROM "TenantLedgerEntries" AS entry
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
              AND entry."EffectiveOn" >= @fromOn
              AND entry."EffectiveOn" <= @toOn
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
               COALESCE(SUM(relationship_ledgers."TotalCredits"), 0::numeric) AS "TotalCredits"
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
        var portfolioId = scope.PortfolioId;
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);
        var hasPropertyFilter = query.PropertyId.HasValue || (query.PropertyIds?.Count > 0);
        var allPropertiesReportsReadAuthority = BuildAllPropertiesAuthorityQuery(scope, CapabilityKeys.ReportsRead);

        // Money in: income is ACTUAL CASH RECEIVED — Rent + LateFee that is Paid (full Amount) or
        // Partial (the collected AmountPaid). Security deposits are a liability, never income (§7/§18).
        // Cash is dated on PaidDate (falling back to DueDate). The range filter + monthly GROUP BY + SUM
        // all run SQL-side (this query was previously materialized + grouped in memory — a hard-rule
        // violation that §18 required rewriting; it is now one EF-translated aggregate).
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);
        var tenantIncomeQuery =
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
                && receipt.EffectiveOn >= fromDate
                && receipt.EffectiveOn <= toDate
                && (charge.EntryType == TenantLedgerEntryType.RentCharge
                    || charge.EntryType == TenantLedgerEntryType.LateFeeCharge)
            select new
            {
                PropertyId = (int?)management.PropertyId,
                Year = receipt.EffectiveOn.Year,
                Month = receipt.EffectiveOn.Month,
                Amount = allocation.Amount,
            };
        var applicationIncomeQuery = _db.ApplicationFinancialEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == portfolioId &&
                entry.EffectiveOn >= fromDate &&
                entry.EffectiveOn <= toDate)
            .Select(entry => new
            {
                entry.PropertyId,
                Year = entry.EffectiveOn.Year,
                Month = entry.EffectiveOn.Month,
                Amount = entry.Direction == ApplicationFinancialDirection.Increase
                    ? entry.Amount
                    : -entry.Amount,
            });

        var incomeQuery = tenantIncomeQuery.Concat(applicationIncomeQuery);

        incomeQuery = incomeQuery.Where(row => row.PropertyId != null &&
            authorizedProperties.Any(property => property.Id == row.PropertyId.Value));

        // Money out: expenses, cash dated on PaidAt (falling back to IncurredAt). A property filter only
        // matches expenses tied to a property; unassigned expenses are excluded when a filter is set.
        // Range filter + monthly GROUP BY + SUM all run SQL-side.
        var expenseQuery = FinancialReportProjections.BuildExpenseAllocationProjection(_db, portfolioId)
            .Where(expense => expense.EffectiveAt >= from && expense.EffectiveAt <= to)
            .Where(expense =>
                (expense.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == expense.PropertyId.Value)) ||
                (expense.PropertyId == null && !hasPropertyFilter && allPropertiesReportsReadAuthority.Any()));

        var monthCount = CountMonths(from, to);
        var anchor = _db.Portfolios
            .AsNoTracking()
            .Where(candidate => candidate.Id == portfolioId && authorizedProperties.Any())
            .Select(_ => 1);
        var monthRows = await FinancialReportProjections.BuildCashFlowMonthProjection(
                anchor,
                incomeQuery.Select(row => new FinancialReportIncomeProjection
                {
                    PropertyId = row.PropertyId,
                    Year = row.Year,
                    Month = row.Month,
                    Amount = row.Amount,
                }),
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
        var portfolioId = scope.PortfolioId;
        var (from, to) = ResolveRange(query, _timeProvider.UtcNow());
        var authorizedProperties = BuildAuthorizedPropertyQuery(scope, query, CapabilityKeys.ReportsRead);
        var hasPropertyFilter = query.PropertyId.HasValue || (query.PropertyIds?.Count > 0);
        var allPropertiesReportsReadAuthority = BuildAllPropertiesAuthorityQuery(scope, CapabilityKeys.ReportsRead);

        var propertyQuery = authorizedProperties;

        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);
        var tenantIncomeQuery =
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
                && receipt.EffectiveOn >= fromDate
                && receipt.EffectiveOn <= toDate
                && (charge.EntryType == TenantLedgerEntryType.RentCharge
                    || charge.EntryType == TenantLedgerEntryType.LateFeeCharge)
            select new
            {
                PropertyId = (int?)management.PropertyId,
                Amount = allocation.Amount,
            };

        var applicationIncomeQuery = _db.ApplicationFinancialEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == portfolioId &&
                entry.EffectiveOn >= fromDate &&
                entry.EffectiveOn <= toDate)
            .Select(entry => new
            {
                entry.PropertyId,
                Amount = entry.Direction == ApplicationFinancialDirection.Increase
                    ? entry.Amount
                    : -entry.Amount,
            });

        var incomeQuery = tenantIncomeQuery.Concat(applicationIncomeQuery);
        var expenseProjection = FinancialReportProjections.BuildExpenseAllocationProjection(_db, portfolioId)
            .Where(expense => expense.EffectiveAt >= from && expense.EffectiveAt <= to);

        var rowQuery = propertyQuery
            .Select(p => new
            {
                p.Id,
                p.Name,
                Income = incomeQuery
                    .Where(income => income.PropertyId == p.Id)
                    .Sum(income => (decimal?)income.Amount) ?? 0m,
                OperatingExpenses = expenseProjection
                    .Where(e =>
                        e.PropertyId == p.Id &&
                        !_db.Loans.Any(l =>
                            l.PropertyId == p.Id &&
                            l.Status == LoanStatus.Active &&
                            ((e.Category == ScheduleECategory.Taxes && l.EscrowCoversTaxes) ||
                             (e.Category == ScheduleECategory.Insurance && l.EscrowCoversInsurance))))
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
                DebtService = _db.LoanPayments
                    .Where(lp =>
                        lp.PortfolioId == portfolioId &&
                        lp.Loan != null &&
                        lp.Loan.PropertyId == p.Id &&
                        lp.DueDate >= from && lp.DueDate <= to)
                    .Sum(lp => (decimal?)lp.TotalAmount) ?? 0m,
            })
            .Where(r => r.Income != 0m || r.OperatingExpenses != 0m || r.DebtService != 0m);

        var rows = await rowQuery
            .OrderBy(r => r.Name)
            .Select(r => new PropertyCashFlow
            {
                PropertyId = r.Id,
                PropertyName = r.Name,
                Income = r.Income,
                OperatingExpenses = r.OperatingExpenses,
                Noi = r.Income - r.OperatingExpenses,
                DebtService = r.DebtService,
                CashFlow = r.Income - r.OperatingExpenses - r.DebtService,
            })
            .ToListAsync(ct);

        var propertyTotals = await rowQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalIncome = g.Sum(r => r.Income),
                TotalDebtService = g.Sum(r => r.DebtService),
            })
            .SingleOrDefaultAsync(ct);
        var totalOperatingExpenses = await expenseProjection
            .Where(e =>
                ((e.PropertyId != null && propertyQuery.Any(property => property.Id == e.PropertyId.Value)) ||
                 (e.PropertyId == null && !hasPropertyFilter && allPropertiesReportsReadAuthority.Any())) &&
                !_db.Loans.Any(l =>
                    e.PropertyId != null &&
                    l.PropertyId == e.PropertyId.Value &&
                    l.Status == LoanStatus.Active &&
                    ((e.Category == ScheduleECategory.Taxes && l.EscrowCoversTaxes) ||
                     (e.Category == ScheduleECategory.Insurance && l.EscrowCoversInsurance))))
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var totalIncome = propertyTotals?.TotalIncome ?? 0m;
        var totalDebtService = propertyTotals?.TotalDebtService ?? 0m;
        var totalNoi = totalIncome - totalOperatingExpenses;

        return new CashFlowSummaryResponse
        {
            From = from,
            To = to,
            Properties = rows,
            TotalIncome = totalIncome,
            TotalOperatingExpenses = totalOperatingExpenses,
            TotalNoi = totalNoi,
            TotalDebtService = totalDebtService,
            TotalCashFlow = totalNoi - totalDebtService,
        };
    }

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

    private sealed class RentLedgerDatabaseRow
    {
        public string LeasesJson { get; set; } = "[]";
        public decimal TotalCharged { get; set; }
        public decimal TotalCredits { get; set; }
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
                        distribution.PropertyId != null &&
                        distribution.Date >= start &&
                        distribution.Date < end &&
                        authorizedProperties.Any(property =>
                            property.Id == distribution.PropertyId.Value))
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
            });

        var rows = await summaries
            .OrderBy(summary => summary.OwnerName)
            .Select(summary => new OwnerDistributionRow
            {
                OwnerId = summary.OwnerId,
                OwnerName = summary.OwnerName,
                NetToOwner = summary.NetToOwner,
                TotalDistributed = summary.TotalDistributed,
                Undistributed = summary.NetToOwner - summary.TotalDistributed,
            })
            .ToListAsync(ct);

        var totals = await summaries
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalNetToOwners = group.Sum(summary => summary.NetToOwner),
                TotalDistributed = group.Sum(summary => summary.TotalDistributed),
            })
            .SingleOrDefaultAsync(ct);
        var totalNetToOwners = totals?.TotalNetToOwners ?? 0m;
        var totalDistributed = totals?.TotalDistributed ?? 0m;

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
