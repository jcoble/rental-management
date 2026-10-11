using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

/// <summary>Attaches the locked default chart rows that are missing for one portfolio.</summary>
public sealed class ChartOfAccountsSeedService
{
    private const long StartupSweepLockKey = 59486;
    private static readonly IReadOnlyList<DefaultAccount> Defaults =
    [
        new("1000", "Operating Cash", AccountType.Asset, "operating-cash"),
        new("1010", "Undeposited Funds", AccountType.Asset, "undeposited-funds"),
        new("1020", "Security Deposit Trust Cash", AccountType.Asset, "security-deposit-trust-cash"),
        new("1100", "Tenant Accounts Receivable", AccountType.Asset, "tenant-accounts-receivable"),
        new("1200", "Mortgage Escrow Asset", AccountType.Asset, "mortgage-escrow-asset"),
        new("1500", "Buildings and Improvements", AccountType.Asset, "buildings-and-improvements"),
        new("1510", "Land", AccountType.Asset, "land"),
        new("1590", "Accumulated Depreciation", AccountType.Asset, "accumulated-depreciation", NormalBalance.Credit),
        new("2000", "Accounts Payable", AccountType.Liability, "accounts-payable"),
        new("2100", "Tenant Security Deposits Payable", AccountType.Liability, "tenant-security-deposits-payable"),
        new("2200", "Mortgage Payable", AccountType.Liability, "mortgage-payable"),
        new("3000", "Owner Contributions", AccountType.Equity, "owner-contributions"),
        new("3100", "Owner Distributions", AccountType.Equity, "owner-distributions", NormalBalance.Debit),
        new("3200", "Retained Earnings", AccountType.Equity, "retained-earnings"),
        new("3300", "Opening Balances", AccountType.Equity, "opening-balances"),
        new("4000", "Rental Income", AccountType.Income, "rental-income"),
        new("4010", "Pet Income", AccountType.Income, "pet-income"),
        new("4020", "Parking Income", AccountType.Income, "parking-income"),
        new("4030", "Late Fee Income", AccountType.Income, "late-fee-income"),
        new("4040", "Utility Reimbursement Income", AccountType.Income, "utility-reimbursement-income"),
        new("4090", "Other Rental Income", AccountType.Income, "other-rental-income"),
        new("5000", "Repairs and Maintenance", AccountType.Expense, "repairs-and-maintenance"),
        new("5010", "Utilities", AccountType.Expense, "utilities"),
        new("5020", "Insurance", AccountType.Expense, "insurance"),
        new("5030", "Property Taxes", AccountType.Expense, "property-taxes"),
        new("5040", "Management Fees", AccountType.Expense, "management-fees"),
        new("5050", "Mortgage Interest", AccountType.Expense, "mortgage-interest"),
        new("5060", "Depreciation Expense", AccountType.Expense, "depreciation-expense"),
        new("5090", "Other Operating Expense", AccountType.Expense, "other-operating-expense"),
    ];

    private readonly RentalCommandDbContext _db;

    public ChartOfAccountsSeedService(RentalCommandDbContext db) => _db = db;

    /// <summary>
    /// Attaches missing defaults and returns the complete default rows. The caller's transaction
    /// owns persistence; this method deliberately does not call SaveChanges.
    /// </summary>
    public async Task<IReadOnlyList<LedgerAccount>> SeedAsync(int portfolioId, CancellationToken ct = default)
    {
        if (portfolioId <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId));

        var existingCodes = await _db.LedgerAccounts
            .Where(account => account.PortfolioId == portfolioId)
            .Select(account => account.Code)
            .ToListAsync(ct);
        var existing = existingCodes.ToHashSet(StringComparer.Ordinal);
        foreach (var tracked in _db.ChangeTracker.Entries<LedgerAccount>()
                     .Where(entry => entry.State == EntityState.Added && entry.Entity.PortfolioId == portfolioId))
            existing.Add(tracked.Entity.Code);
        var seeded = new List<LedgerAccount>(Defaults.Count);
        foreach (var definition in Defaults)
        {
            if (existing.Contains(definition.Code))
                continue;

            var account = new LedgerAccount
            {
                PortfolioId = portfolioId,
                Code = definition.Code,
                Name = definition.Name,
                AccountType = definition.Type,
                NormalBalance = definition.Balance ?? NormalBalanceFor(definition.Type),
                SystemKey = definition.SystemKey,
                IsSystem = true,
                IsActive = true,
            };
            _db.LedgerAccounts.Add(account);
            seeded.Add(account);
        }

        return seeded;
    }

    /// <summary>
    /// Backfills the locked chart for every pre-accounting portfolio in one PostgreSQL statement.
    /// The migration process calls this after schema migration; a transaction advisory lock makes
    /// concurrent migration containers serialize, and the unique portfolio/code index makes exact
    /// replay a no-op.
    /// </summary>
    public async Task<int> SeedAllWithLockAsync(CancellationToken ct = default)
    {
        if (!_db.Database.IsNpgsql())
            throw new NotSupportedException("The portfolio chart startup sweep requires PostgreSQL.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            $"SELECT pg_advisory_xact_lock({StartupSweepLockKey})", ct);

        var parameters = new List<object>(Defaults.Count * 5);
        var rows = new List<string>(Defaults.Count);
        for (var index = 0; index < Defaults.Count; index++)
        {
            var definition = Defaults[index];
            rows.Add($"(@code{index}, @name{index}, @type{index}, @balance{index}, @systemKey{index})");
            parameters.Add(new NpgsqlParameter($"code{index}", definition.Code));
            parameters.Add(new NpgsqlParameter($"name{index}", definition.Name));
            parameters.Add(new NpgsqlParameter($"type{index}", definition.Type.ToString()));
            parameters.Add(new NpgsqlParameter(
                $"balance{index}",
                (definition.Balance ?? NormalBalanceFor(definition.Type)).ToString()));
            parameters.Add(new NpgsqlParameter($"systemKey{index}", definition.SystemKey));
        }

        var inserted = await _db.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO "LedgerAccounts"
                ("PortfolioId", "Code", "Name", "AccountType", "NormalBalance", "SystemKey", "IsSystem", "IsActive")
            SELECT portfolio."Id", defaults."Code", defaults."Name", defaults."AccountType",
                   defaults."NormalBalance", defaults."SystemKey", TRUE, TRUE
            FROM "Portfolios" AS portfolio
            CROSS JOIN (VALUES {{string.Join(", ", rows)}})
                AS defaults("Code", "Name", "AccountType", "NormalBalance", "SystemKey")
            WHERE portfolio."DeletedAt" IS NULL
            ON CONFLICT ("PortfolioId", "Code") DO NOTHING
            """, parameters, ct);

        // Pre-accounting portfolios have authoritative operational receivable and deposit
        // subledgers but no corresponding GL history. Capture those positions once at the
        // portfolio's accounting go-live. Cash is intentionally omitted: historical cash cannot
        // be reconstructed reliably from the operational records, so inventing it would make the
        // opening books look more complete while making them less truthful.
        //
        // Balance discovery, grouping, journal creation, and line creation remain one SQL-side
        // statement. Per-source business keys are the durable portfolio-scoped migration marker;
        // a chart-only prior startup therefore adds only the missing opening journals.
        await _db.Database.ExecuteSqlRawAsync("""
            WITH opening_candidates AS MATERIALIZED (
                SELECT balance."PortfolioId",
                       'tenant-account'::text AS source_kind,
                       balance."TenantAccountId"::bigint AS source_id,
                       'opening-balance:portfolio:' || balance."PortfolioId" ||
                           ':tenant-account:' || balance."TenantAccountId" AS business_key,
                       balance."BusinessDate" AS effective_on,
                       balance."Currency",
                       balance."ReceivableBalance" AS amount,
                       management."PropertyId",
                       management."UnitId",
                       balance."TenantAccountId"
                FROM "vw_tenant_account_balances" AS balance
                JOIN "TenantAccounts" AS tenant_account
                  ON tenant_account."PortfolioId" = balance."PortfolioId"
                 AND tenant_account."Id" = balance."TenantAccountId"
                JOIN "LeaseManagements" AS management
                  ON management."PortfolioId" = tenant_account."PortfolioId"
                 AND management."Id" = tenant_account."LeaseManagementId"
                WHERE balance."ReceivableBalance" <> 0

                UNION ALL

                SELECT balance."PortfolioId",
                       'security-deposit'::text,
                       balance."SecurityDepositAccountId"::bigint,
                       'opening-balance:portfolio:' || balance."PortfolioId" ||
                           ':security-deposit:' || balance."SecurityDepositAccountId",
                       balance."BusinessDate",
                       balance."Currency",
                       balance."HeldBalance",
                       management."PropertyId",
                       management."UnitId",
                       balance."TenantAccountId"
                FROM "vw_security_deposit_balances" AS balance
                JOIN "TenantAccounts" AS tenant_account
                  ON tenant_account."PortfolioId" = balance."PortfolioId"
                 AND tenant_account."Id" = balance."TenantAccountId"
                JOIN "LeaseManagements" AS management
                  ON management."PortfolioId" = tenant_account."PortfolioId"
                 AND management."Id" = tenant_account."LeaseManagementId"
                WHERE balance."HeldBalance" > 0
            ),
            inserted_journals AS (
                INSERT INTO "JournalEntries"
                    ("PublicId", "PortfolioId", "EffectiveOn", "PostedAtUtc", "Currency",
                     "Description", "SourceType", "SourceId", "SourceBusinessKey",
                     "IdempotencyDigest", "PostingRuleVersion", "AttemptId", "ActorLabel",
                     "AtomicReceiptId")
                SELECT gen_random_uuid(), candidate."PortfolioId", candidate.effective_on,
                       clock_timestamp(), candidate."Currency",
                       CASE candidate.source_kind
                           WHEN 'tenant-account' THEN 'Opening tenant balance'
                           ELSE 'Opening security deposit balance'
                       END,
                       'OpeningBalance', candidate.source_id, candidate.business_key,
                       md5(candidate.business_key || ':' || candidate.amount::text) ||
                           md5(candidate.amount::text || ':' || candidate.business_key),
                       1, gen_random_uuid(), 'migration:accounting-opening-balances', gen_random_uuid()
                FROM opening_candidates AS candidate
                ON CONFLICT ("PortfolioId", "SourceType", "SourceBusinessKey", "PostingRuleVersion")
                    DO NOTHING
                RETURNING "Id", "PortfolioId", "SourceId", "SourceBusinessKey"
            ),
            journal_facts AS MATERIALIZED (
                SELECT journal."Id" AS journal_id, candidate.*
                FROM inserted_journals AS journal
                JOIN opening_candidates AS candidate
                  ON candidate."PortfolioId" = journal."PortfolioId"
                 AND candidate.source_id = journal."SourceId"
                 AND candidate.business_key = journal."SourceBusinessKey"
            ),
            accounts AS MATERIALIZED (
                SELECT account."PortfolioId", account."Id", account."SystemKey"
                FROM "LedgerAccounts" AS account
                WHERE account."SystemKey" IN
                    ('tenant-accounts-receivable', 'tenant-security-deposits-payable', 'opening-balances')
            )
            INSERT INTO "JournalLines"
                ("JournalEntryId", "LedgerAccountId", "DebitAmount", "CreditAmount", "Memo",
                 "PropertyId", "UnitId", "TenantAccountId", "SourceLineType", "SourceLineId")
            SELECT fact.journal_id, account."Id",
                   CASE
                       WHEN fact.source_kind = 'tenant-account'
                            AND fact.amount > 0
                            AND account."SystemKey" = 'tenant-accounts-receivable'
                           THEN fact.amount
                       WHEN fact.source_kind = 'tenant-account'
                            AND fact.amount < 0
                            AND account."SystemKey" = 'opening-balances'
                           THEN abs(fact.amount)
                       WHEN fact.source_kind = 'security-deposit'
                            AND account."SystemKey" = 'opening-balances'
                           THEN fact.amount
                       ELSE 0
                   END,
                   CASE
                       WHEN fact.source_kind = 'tenant-account'
                            AND fact.amount > 0
                            AND account."SystemKey" = 'opening-balances'
                           THEN fact.amount
                       WHEN fact.source_kind = 'tenant-account'
                            AND fact.amount < 0
                            AND account."SystemKey" = 'tenant-accounts-receivable'
                           THEN abs(fact.amount)
                       WHEN fact.source_kind = 'security-deposit'
                            AND account."SystemKey" = 'tenant-security-deposits-payable'
                           THEN fact.amount
                       ELSE 0
                   END,
                   CASE fact.source_kind
                       WHEN 'tenant-account' THEN 'Operational balance at accounting go-live'
                       ELSE 'Held deposit at accounting go-live'
                   END,
                   fact."PropertyId", fact."UnitId", fact."TenantAccountId",
                   CASE fact.source_kind
                       WHEN 'tenant-account' THEN 'TenantAccountOpeningBalance'
                       ELSE 'SecurityDepositOpeningBalance'
                   END,
                   fact.source_id
            FROM journal_facts AS fact
            JOIN accounts AS account ON account."PortfolioId" = fact."PortfolioId"
            WHERE (fact.source_kind = 'tenant-account' AND account."SystemKey" IN
                       ('tenant-accounts-receivable', 'opening-balances'))
               OR (fact.source_kind = 'security-deposit' AND account."SystemKey" IN
                       ('tenant-security-deposits-payable', 'opening-balances'))
            """, ct);
        await transaction.CommitAsync(ct);
        return inserted;
    }

    public static IReadOnlyList<(string Code, string Name, AccountType Type)> DefaultChart =>
        Defaults.Select(definition => (definition.Code, definition.Name, definition.Type)).ToArray();

    private static NormalBalance NormalBalanceFor(AccountType type) =>
        type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit;

    private sealed record DefaultAccount(
        string Code,
        string Name,
        AccountType Type,
        string SystemKey,
        NormalBalance? Balance = null);
}
