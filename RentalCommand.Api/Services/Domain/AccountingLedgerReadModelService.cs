using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IAccountingLedgerReadModelService
{
    Task<AccountingPage<ChartOfAccountsRow>> GetChartOfAccountsAsync(
        int portfolioId, ChartOfAccountsQuery query, CancellationToken ct = default);

    Task<AccountingPage<GeneralLedgerRow>> GetGeneralLedgerAsync(
        WorkspaceReadScope scope, GeneralLedgerQuery query, CancellationToken ct = default);

    Task<JournalDetail?> GetJournalDetailAsync(
        WorkspaceReadScope scope, Guid publicId, CancellationToken ct = default);

    Task<TrialBalanceResponse> GetTrialBalanceAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default);

    Task<FinancialStatementResponse> GetBalanceSheetAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default);

    Task<FinancialStatementResponse> GetIncomeStatementAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<SourceJournalSummary>> GetSourceJournalsAsync(
        WorkspaceReadScope scope, SourceJournalQuery query, CancellationToken ct = default);

    Task<MoneyPositionResponse> GetMoneyPositionAsync(
        WorkspaceReadScope scope, MoneyPositionQuery query, CancellationToken ct = default);

    Task<AccountingPage<TenantLedgerRow>?> GetTenantLedgerAsync(
        int portfolioId, int tenantAccountId, TenantLedgerQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<TenantMonthSummary>?> GetTenantMonthSummaryAsync(
        int portfolioId, int tenantAccountId, TenantMonthSummaryQuery query, CancellationToken ct = default);

    Task<TenantLedgerPeriodSummary?> GetTenantLedgerPeriodSummaryAsync(
        int portfolioId, int tenantAccountId, int months, CancellationToken ct = default);

    Task<AccountingPage<RecurringTenantChargeRow>> GetRecurringTenantChargesAsync(
        int portfolioId, int tenantAccountId, ListQuery query, CancellationToken ct = default);
}

/// <summary>
/// SQL-backed accounting read models. The service never materializes a source table to perform
/// filtering, aggregation, or running-balance work; those operations remain in EF-translated SQL.
/// </summary>
public sealed class AccountingLedgerReadModelService : IAccountingLedgerReadModelService
{
    private readonly RentalCommandDbContext _db;

    public AccountingLedgerReadModelService(RentalCommandDbContext db) => _db = db;

    public async Task<AccountingPage<ChartOfAccountsRow>> GetChartOfAccountsAsync(
        int portfolioId, ChartOfAccountsQuery query, CancellationToken ct = default)
    {
        var accounts = _db.LedgerAccounts
            .AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId);
        if (query.ActiveOnly is true)
        {
            accounts = accounts.Where(account => account.IsActive);
        }

        var totalCount = await accounts.CountAsync(ct);
        var skip = query.NormalizedSkip;
        var take = query.NormalizedTake;
        var rows = await accounts
            .OrderBy(account => account.Code)
            .ThenBy(account => account.Id)
            .Skip(skip)
            .Take(take)
            .Select(account => new ChartOfAccountsRow
            {
                Id = account.Id,
                PublicId = account.PublicId,
                Code = account.Code,
                Name = account.Name,
                AccountType = account.AccountType,
                NormalBalance = account.NormalBalance,
                ParentAccountId = account.ParentAccountId,
                SystemKey = account.SystemKey,
                ScheduleECategory = account.ScheduleECategory,
                IsSystem = account.IsSystem,
                IsActive = account.IsActive,
                HasPostedLines = account.JournalLines.Any(),
            })
            .ToListAsync(ct);

        return new AccountingPage<ChartOfAccountsRow>
        {
            Items = rows,
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
        };
    }

    public async Task<AccountingPage<GeneralLedgerRow>> GetGeneralLedgerAsync(
        WorkspaceReadScope scope, GeneralLedgerQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizationNowUtc = DateTime.UtcNow;
        var authorizedLines = _db.JournalLines
            .AsNoTracking()
            .WhereAccountingAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, authorizationNowUtc);
        var lines = authorizedLines;

        if (query.AccountId is int accountId)
            lines = lines.Where(line => line.LedgerAccountId == accountId);
        if (query.PropertyId is int propertyId)
            lines = lines.Where(line => line.PropertyId == propertyId);
        if (query.UnitId is int unitId)
            lines = lines.Where(line => line.UnitId == unitId);
        if (query.SourceType is JournalSourceType sourceType)
            lines = lines.Where(line => line.JournalEntry!.SourceType == sourceType);
        if (query.EffectiveFrom is DateOnly effectiveFrom)
            lines = lines.Where(line => line.JournalEntry!.EffectiveOn >= effectiveFrom);
        if (query.EffectiveTo is DateOnly effectiveTo)
            lines = lines.Where(line => line.JournalEntry!.EffectiveOn <= effectiveTo);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            lines = lines.Where(line =>
                EF.Functions.ILike(line.JournalEntry!.Description, $"%{search}%") ||
                EF.Functions.ILike(line.JournalEntry.SourceBusinessKey, $"%{search}%") ||
                EF.Functions.ILike(line.LedgerAccount!.Code, $"%{search}%") ||
                EF.Functions.ILike(line.LedgerAccount.Name, $"%{search}%"));
        }

        var totalCount = await lines.CountAsync(ct);
        var skip = query.NormalizedSkip;
        var take = query.NormalizedTake;
        var accountIsSelected = query.AccountId is not null;

        var projected = lines.Select(line => new GeneralLedgerRow
        {
            JournalEntryPublicId = line.JournalEntry!.PublicId,
            LineId = line.Id,
            EffectiveOn = line.JournalEntry.EffectiveOn,
            PostedAtUtc = line.JournalEntry.PostedAtUtc,
            SourceType = line.JournalEntry.SourceType,
            SourceId = line.JournalEntry.SourceId,
            SourceBusinessKey = line.JournalEntry.SourceBusinessKey,
            Description = line.JournalEntry.Description,
            AccountId = line.LedgerAccountId,
            AccountCode = line.LedgerAccount!.Code,
            AccountName = line.LedgerAccount.Name,
            AccountType = line.LedgerAccount.AccountType,
            NormalBalance = line.LedgerAccount.NormalBalance,
            DebitAmount = line.DebitAmount,
            CreditAmount = line.CreditAmount,
            Currency = line.JournalEntry.Currency,
            PropertyId = line.PropertyId,
            UnitId = line.UnitId,
            TenantAccountId = line.TenantAccountId,
            OwnerEntityId = line.OwnerEntityId,
            RunningBalance = accountIsSelected
                ? authorizedLines.Where(previous => previous.JournalEntry!.PortfolioId == portfolioId
                        && previous.LedgerAccountId == line.LedgerAccountId
                        && previous.JournalEntry.Currency == line.JournalEntry.Currency
                        && (previous.JournalEntry.EffectiveOn < line.JournalEntry.EffectiveOn
                            || (previous.JournalEntry.EffectiveOn == line.JournalEntry.EffectiveOn
                                && (previous.JournalEntry.PostedAtUtc < line.JournalEntry.PostedAtUtc
                                    || (previous.JournalEntry.PostedAtUtc == line.JournalEntry.PostedAtUtc
                                        && (previous.JournalEntry.Id < line.JournalEntry.Id
                                            || (previous.JournalEntry.Id == line.JournalEntry.Id
                                                && previous.Id <= line.Id)))))))
                    .Select(previous => line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? previous.DebitAmount - previous.CreditAmount
                        : previous.CreditAmount - previous.DebitAmount)
                    .Sum()
                : null,
        });

        var ordered = OrderGeneralLedger(projected, query);
        var rows = await ordered
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new AccountingPage<GeneralLedgerRow>
        {
            Items = rows,
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
        };
    }

    public async Task<JournalDetail?> GetJournalDetailAsync(
        WorkspaceReadScope scope, Guid publicId, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedLines = _db.JournalLines.AsNoTracking().WhereAccountingAuthorized(
            _db, scope, CapabilityKeys.MoneyBalancesRead, DateTime.UtcNow);
        var header = await _db.JournalEntries
            .AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == portfolioId &&
                entry.PublicId == publicId &&
                entry.Lines.Any() &&
                entry.Lines.All(line => authorizedLines.Any(authorized => authorized.Id == line.Id)))
            .Select(entry => new
            {
                entry.Id,
                entry.PublicId,
                entry.Description,
                entry.EffectiveOn,
                entry.PostedAtUtc,
                entry.SourceType,
                entry.SourceId,
                entry.SourceBusinessKey,
                entry.ActorLabel,
                entry.AttemptId,
                entry.AtomicReceiptId,
                entry.IdempotencyDigest,
                entry.Currency,
                entry.ReversesJournalEntryId,
                ReversesPublicId = entry.ReversedJournalEntry == null
                    ? (Guid?)null
                    : entry.ReversedJournalEntry.PublicId,
            })
            .SingleOrDefaultAsync(ct);
        if (header is null)
            return null;

        var lines = await _db.JournalLines
            .AsNoTracking()
            .Where(line => line.JournalEntryId == header.Id)
            .OrderBy(line => line.Id)
            .Select(line => new JournalDetailLine
            {
                Id = line.Id,
                AccountId = line.LedgerAccountId,
                AccountCode = line.LedgerAccount!.Code,
                AccountName = line.LedgerAccount.Name,
                NormalBalance = line.LedgerAccount.NormalBalance,
                DebitAmount = line.DebitAmount,
                CreditAmount = line.CreditAmount,
                Memo = line.Memo,
                PropertyId = line.PropertyId,
                UnitId = line.UnitId,
                TenantAccountId = line.TenantAccountId,
                OwnerEntityId = line.OwnerEntityId,
            })
            .ToListAsync(ct);
        var totals = await _db.JournalLines
            .AsNoTracking()
            .Where(line => line.JournalEntryId == header.Id)
            .GroupBy(_ => 1)
            .Select(group => new { Debits = group.Sum(line => line.DebitAmount), Credits = group.Sum(line => line.CreditAmount) })
            .SingleOrDefaultAsync(ct);

        var reversalIds = await _db.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.ReversesJournalEntryId == header.Id)
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.PublicId)
            .ToListAsync(ct);

        return new JournalDetail
        {
            PublicId = header.PublicId,
            Description = header.Description,
            EffectiveOn = header.EffectiveOn,
            PostedAtUtc = header.PostedAtUtc,
            SourceType = header.SourceType,
            SourceId = header.SourceId,
            SourceBusinessKey = header.SourceBusinessKey,
            Actor = header.ActorLabel,
            AttemptId = header.AttemptId,
            AtomicReceiptId = header.AtomicReceiptId,
            IdempotencyDigest = header.IdempotencyDigest,
            Currency = header.Currency,
            Lines = lines,
            TotalDebits = totals?.Debits ?? 0m,
            TotalCredits = totals?.Credits ?? 0m,
            IsBalanced = totals is not null && totals.Debits == totals.Credits,
            ReversesJournalEntryPublicId = header.ReversesPublicId,
            ReversalPublicIds = reversalIds,
            AuditLink = $"/api/v1/audits?entityType=JournalEntry&entityId={header.Id}",
            DocumentIds = await GetSourceDocumentIdsAsync(
                portfolioId, header.SourceType, header.SourceId, ct),
            BankReconciliationEvidence = await GetBankEvidenceAsync(
                portfolioId, header.SourceType, header.SourceId, ct),
        };
    }

    public async Task<IReadOnlyList<SourceJournalSummary>> GetSourceJournalsAsync(
        WorkspaceReadScope scope,
        SourceJournalQuery query,
        CancellationToken ct = default)
    {
        var authorizedLines = _db.JournalLines.AsNoTracking().WhereAccountingAuthorized(
            _db, scope, CapabilityKeys.MoneyBalancesRead, DateTime.UtcNow);
        return await _db.JournalEntries.AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == scope.PortfolioId &&
                entry.SourceType == query.SourceType &&
                entry.SourceId == query.SourceId &&
                entry.Lines.Any() &&
                entry.Lines.All(line => authorizedLines.Any(authorized => authorized.Id == line.Id)))
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.PostedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Select(entry => new SourceJournalSummary
            {
                PublicId = entry.PublicId,
                EffectiveOn = entry.EffectiveOn,
                PostedAtUtc = entry.PostedAtUtc,
                SourceType = entry.SourceType,
                Description = entry.Description,
                TotalDebits = entry.Lines.Sum(line => line.DebitAmount),
                TotalCredits = entry.Lines.Sum(line => line.CreditAmount),
                IsReversal = entry.ReversesJournalEntryId != null,
                ReversesPublicId = entry.ReversedJournalEntry == null
                    ? null
                    : entry.ReversedJournalEntry.PublicId,
            })
            .ToListAsync(ct);
    }

    public async Task<MoneyPositionResponse> GetMoneyPositionAsync(
        WorkspaceReadScope scope,
        MoneyPositionQuery query,
        CancellationToken ct = default)
    {
        var asOfUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(asOfUtc);
        var from = query.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = query.To ?? today;
        if (to < from)
            throw new ArgumentException("The money-position end date cannot precede its start date.", nameof(query));

        var authorized = _db.JournalLines.AsNoTracking().WhereAccountingAuthorized(
            _db, scope, CapabilityKeys.MoneyBalancesRead, asOfUtc);
        var throughAsOf = authorized.Where(line => line.JournalEntry!.EffectiveOn <= to);
        var position = await throughAsOf.GroupBy(_ => 1)
            .Select(group => new MoneyPositionSqlRow
            {
                TotalCashOnHand = group.Sum(line => line.LedgerAccount!.IsSystem &&
                    (line.LedgerAccount.SystemKey == "operating-cash" ||
                     line.LedgerAccount.SystemKey == "undeposited-funds" ||
                     line.LedgerAccount.SystemKey == "security-deposit-trust-cash")
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : 0m),
                TenantDepositsHeld = group.Sum(line => line.LedgerAccount!.IsSystem &&
                    line.LedgerAccount.SystemKey == "tenant-security-deposits-payable"
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : 0m),
                RentStillOwed = group.Sum(line => line.LedgerAccount!.IsSystem &&
                    line.LedgerAccount.SystemKey == "tenant-accounts-receivable"
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : 0m),
                LoanBalance = group.Sum(line => line.LedgerAccount!.IsSystem &&
                    line.LedgerAccount.SystemKey == "mortgage-payable"
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : 0m),
                Assets = group.Sum(line => line.LedgerAccount!.AccountType == AccountType.Asset
                    ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? line.DebitAmount - line.CreditAmount
                        : line.CreditAmount - line.DebitAmount
                    : 0m),
                Liabilities = group.Sum(line => line.LedgerAccount!.AccountType == AccountType.Liability
                    ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? line.DebitAmount - line.CreditAmount
                        : line.CreditAmount - line.DebitAmount
                    : 0m),
                ProfitOrLoss = group.Sum(line =>
                    line.JournalEntry!.EffectiveOn >= from &&
                    line.JournalEntry.EffectiveOn <= to &&
                    line.LedgerAccount!.AccountType == AccountType.Income
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : line.JournalEntry.EffectiveOn >= from &&
                          line.JournalEntry.EffectiveOn <= to &&
                          line.LedgerAccount!.AccountType == AccountType.Expense
                            ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                                ? -(line.DebitAmount - line.CreditAmount)
                                : -(line.CreditAmount - line.DebitAmount)
                            : 0m),
            })
            .SingleOrDefaultAsync(ct) ?? new MoneyPositionSqlRow();

        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, CapabilityKeys.MoneyBalancesRead, asOfUtc);
        var depositPosition = await (
                from balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { balance.PortfolioId, Id = balance.LeaseManagementId }
                    equals new { management.PortfolioId, management.Id }
                where balance.PortfolioId == scope.PortfolioId &&
                      authorizedProperties.Any(property =>
                          property.PortfolioId == management.PortfolioId &&
                          property.Id == management.PropertyId)
                select balance)
            .GroupBy(_ => 1)
            .Select(group => new DepositPositionSqlRow
            {
                TenantDepositsHeld = group.Sum(balance => balance.HeldBalance),
            })
            .SingleOrDefaultAsync(ct);
        var tenantDepositsHeld = depositPosition?.TenantDepositsHeld
            ?? position.TenantDepositsHeld;

        var cashSystemKeys = new[]
        {
            "operating-cash",
            "undeposited-funds",
            "security-deposit-trust-cash",
        };
        var cashByJournal = authorized
            .Where(line =>
                line.JournalEntry!.EffectiveOn >= from &&
                line.JournalEntry.EffectiveOn <= to &&
                line.LedgerAccount!.IsSystem &&
                cashSystemKeys.Contains(line.LedgerAccount.SystemKey!))
            .GroupBy(line => line.JournalEntryId)
            .Select(group => new
            {
                Net = group.Sum(line => line.DebitAmount - line.CreditAmount),
            });
        var movement = await cashByJournal.GroupBy(_ => 1)
            .Select(group => new CashMovementSqlRow
            {
                CashReceived = group.Sum(journal => journal.Net > 0m ? journal.Net : 0m),
                CashPaid = group.Sum(journal => journal.Net < 0m ? -journal.Net : 0m),
            })
            .SingleOrDefaultAsync(ct) ?? new CashMovementSqlRow();

        return new MoneyPositionResponse
        {
            AsOfUtc = asOfUtc,
            FromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ToUtc = to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc),
            TotalCashOnHand = position.TotalCashOnHand,
            TenantDepositsHeld = tenantDepositsHeld,
            CashAfterTenantDeposits = position.TotalCashOnHand - tenantDepositsHeld,
            RentStillOwed = position.RentStillOwed,
            LoanBalance = position.LoanBalance,
            BookEquity = position.Assets - position.Liabilities,
            CashReceived = movement.CashReceived,
            CashPaid = movement.CashPaid,
            NetCashMovement = movement.CashReceived - movement.CashPaid,
            ProfitOrLoss = position.ProfitOrLoss,
        };
    }

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default)
    {
        var lines = FilterStatementLines(scope, query, from: null, query.To);
        var balances = lines
            .GroupBy(line => new
            {
                line.LedgerAccountId,
                line.LedgerAccount!.Code,
                line.LedgerAccount.Name,
                line.LedgerAccount.AccountType,
                line.JournalEntry!.Currency,
            })
            .Select(group => new
            {
                group.Key.LedgerAccountId,
                group.Key.Code,
                group.Key.Name,
                group.Key.AccountType,
                group.Key.Currency,
                Net = group.Sum(line => line.DebitAmount - line.CreditAmount),
            });
        var rows = await balances.Select(balance => new TrialBalanceRow
            {
                AccountId = balance.LedgerAccountId,
                AccountCode = balance.Code,
                AccountName = balance.Name,
                AccountType = balance.AccountType,
                DebitBalance = balance.Net >= 0m ? balance.Net : 0m,
                CreditBalance = balance.Net < 0m ? -balance.Net : 0m,
                Currency = balance.Currency,
            })
            .OrderBy(row => row.AccountCode)
            .ThenBy(row => row.Currency)
            .ToListAsync(ct);
        var totals = await balances.GroupBy(_ => 1).Select(group => new
        {
            Debits = group.Sum(balance => balance.Net >= 0m ? balance.Net : 0m),
            Credits = group.Sum(balance => balance.Net < 0m ? -balance.Net : 0m),
        }).SingleOrDefaultAsync(ct);
        var totalDebits = totals?.Debits ?? 0m;
        var totalCredits = totals?.Credits ?? 0m;
        return new TrialBalanceResponse
        {
            Rows = rows,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits,
            IsBalanced = totalDebits == totalCredits,
        };
    }

    public async Task<FinancialStatementResponse> GetBalanceSheetAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default)
    {
        var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var sections = new List<StatementSection>();
        foreach (var type in new[] { AccountType.Asset, AccountType.Liability, AccountType.Equity })
        {
            var section = await LoadStatementSectionAsync(scope, query, type, null, to, ct);
            if (section.Rows.Count > 0)
                sections.Add(section);
        }

        var totals = await LoadStatementTotalsAsync(scope, query, null, to, ct);
        var currentEarnings = totals.Income - totals.Expenses;
        var liabilitiesAndEquity = totals.LiabilitiesAndEquity + currentEarnings;
        return new FinancialStatementResponse
        {
            Sections = sections,
            Totals = new StatementTotals
            {
                Total = totals.Assets,
                Assets = totals.Assets,
                LiabilitiesAndEquity = liabilitiesAndEquity,
                CurrentEarnings = currentEarnings,
                IsBalanced = totals.Assets == liabilitiesAndEquity,
            },
        };
    }

    public async Task<FinancialStatementResponse> GetIncomeStatementAsync(
        WorkspaceReadScope scope, StatementQuery query, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = query.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = query.To ?? today;
        var sections = new List<StatementSection>();
        foreach (var type in new[] { AccountType.Income, AccountType.Expense })
        {
            var section = await LoadStatementSectionAsync(scope, query, type, from, to, ct);
            if (section.Rows.Count > 0)
                sections.Add(section);
        }

        var totals = await LoadStatementTotalsAsync(scope, query, from, to, ct);
        var netIncome = totals.Income - totals.Expenses;
        return new FinancialStatementResponse
        {
            Sections = sections,
            Totals = new StatementTotals { Total = netIncome, NetIncome = netIncome },
        };
    }

    public async Task<AccountingPage<TenantLedgerRow>?> GetTenantLedgerAsync(
        int portfolioId, int tenantAccountId, TenantLedgerQuery query, CancellationToken ct = default)
    {
        var entries = _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId && entry.TenantAccountId == tenantAccountId);
        if (query.EntryType is TenantLedgerEntryType entryType)
            entries = entries.Where(entry => entry.EntryType == entryType);
        if (query.EffectiveFrom is DateOnly effectiveFrom)
            entries = entries.Where(entry => entry.EffectiveOn >= effectiveFrom);
        if (query.EffectiveTo is DateOnly effectiveTo)
            entries = entries.Where(entry => entry.EffectiveOn <= effectiveTo);

        if (query.OpenOnly is true)
            entries = entries.Where(entry => entry.Direction == TenantLedgerDirection.Debit
                && entry.Amount - _db.TenantLedgerAllocations
                    .Where(allocation => allocation.DebitEntryId == entry.Id)
                    .Sum(allocation => allocation.Amount) > 0m);
        if (query.SettledOnly is true)
            entries = entries.Where(entry => entry.Direction != TenantLedgerDirection.Debit
                || entry.Amount - _db.TenantLedgerAllocations
                    .Where(allocation => allocation.DebitEntryId == entry.Id)
                    .Sum(allocation => allocation.Amount) <= 0m);

        var totalCount = await entries.CountAsync(ct);
        var skip = query.NormalizedSkip;
        var take = query.NormalizedTake;
        var page = await OrderTenantLedger(entries, query)
            .Skip(skip)
            .Take(take)
            .Select(entry => new TenantLedgerRow
            {
                TenantLedgerEntryId = entry.Id,
                PublicId = entry.PublicId,
                SourceType = "tenant-ledger",
                SourceId = entry.Id,
                SourcePublicId = entry.PublicId,
                EffectiveOn = entry.EffectiveOn,
                PostedAtUtc = entry.PostedAtUtc,
                Type = entry.EntryType,
                Description = entry.Description,
                ChargeAmount = entry.Direction == TenantLedgerDirection.Debit
                    && entry.EntryType != TenantLedgerEntryType.PaymentReceipt
                    && entry.EntryType != TenantLedgerEntryType.Credit
                    && entry.EntryType != TenantLedgerEntryType.Adjustment
                    ? entry.Amount : 0m,
                PaymentAmount = entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.Direction == TenantLedgerDirection.Credit ? entry.Amount : 0m,
                CreditAmount = (entry.EntryType == TenantLedgerEntryType.Credit
                    || entry.EntryType == TenantLedgerEntryType.Adjustment)
                    && entry.Direction == TenantLedgerDirection.Credit ? entry.Amount : 0m,
                RunningAmountOwed = _db.TenantLedgerEntries
                    .Where(previous => previous.PortfolioId == portfolioId
                        && previous.TenantAccountId == tenantAccountId
                        && previous.Currency == entry.Currency
                        && (previous.EffectiveOn < entry.EffectiveOn
                            || (previous.EffectiveOn == entry.EffectiveOn
                                && (previous.PostedAtUtc < entry.PostedAtUtc
                                    || (previous.PostedAtUtc == entry.PostedAtUtc && previous.Id <= entry.Id)))))
                    .Select(previous => previous.Direction == TenantLedgerDirection.Debit
                        ? previous.Amount : -previous.Amount)
                    .Sum(),
                DueOn = entry.DueOn,
                OpenAmount = entry.Direction == TenantLedgerDirection.Debit
                    ? entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id)
                        .Sum(allocation => allocation.Amount)
                    : 0m,
                Status = entry.Direction == TenantLedgerDirection.Debit
                    ? (entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id)
                        .Sum(allocation => allocation.Amount) > 0m ? "Open" : "Settled")
                    : "Settled",
                PaymentMethod = entry.ProviderPaymentAttempt!.PaymentMethodSummary,
                Reference = entry.ProviderPaymentAttempt.ProviderObjectId
                    ?? entry.ProviderPaymentAttempt.CheckNumber,
                AccountLabel = entry.EntryType.ToString(),
                SourceDocumentContext = entry.SourceStoredFile!.FileName,
                RelatedTenantLedgerEntryId = entry.RelatedTenantLedgerEntryId,
                RelatedEntryDescription = entry.RelatedTenantLedgerEntry!.Description,
                CategoryName = (
                    from journal in _db.JournalEntries
                    join line in _db.JournalLines
                        on journal.Id equals line.JournalEntryId
                    join account in _db.LedgerAccounts
                        on line.LedgerAccountId equals account.Id
                    where journal.PortfolioId == portfolioId
                        && journal.SourceId == entry.Id
                        && (journal.SourceType == JournalSourceType.TenantCharge
                            || journal.SourceType == JournalSourceType.TenantConcession)
                        && account.AccountType == AccountType.Income
                    orderby journal.Id descending, line.Id
                    select account.Name)
                    .FirstOrDefault(),
                ServicePeriodStartOn = entry.ServicePeriodStartOn,
                ServicePeriodEndOn = entry.ServicePeriodEndOn,
                ReversesEntryId = entry.ReversesEntryId,
                JournalEntryPublicId = _db.JournalEntries
                    .Where(journal => journal.PortfolioId == portfolioId && journal.SourceId == entry.Id)
                    .OrderByDescending(journal => journal.Id)
                    .Select(journal => (Guid?)journal.PublicId)
                    .FirstOrDefault(),
                Currency = entry.Currency,
            })
            .ToListAsync(ct);

        var entryIds = page.Select(row => row.TenantLedgerEntryId).ToArray();
        if (entryIds.Length > 0)
        {
            var allocations = await _db.TenantLedgerAllocations
                .AsNoTracking()
                .Where(allocation => allocation.PortfolioId == portfolioId
                    && (entryIds.Contains(allocation.DebitEntryId) || entryIds.Contains(allocation.CreditEntryId)))
                .Select(allocation => new
                {
                    allocation.DebitEntryId,
                    allocation.CreditEntryId,
                    allocation.Amount,
                    Debit = new
                    {
                        allocation.DebitEntry!.PublicId,
                        allocation.DebitEntry.Description,
                        allocation.DebitEntry.EffectiveOn,
                    },
                    Credit = new
                    {
                        allocation.CreditEntry!.PublicId,
                        allocation.CreditEntry.Description,
                        allocation.CreditEntry.EffectiveOn,
                    },
                })
                .ToListAsync(ct);
            foreach (var row in page)
            {
                row.Allocations = allocations
                    .Where(allocation => allocation.DebitEntryId == row.TenantLedgerEntryId)
                    .Select(allocation => new AllocationRef
                    {
                        TargetSourceId = allocation.CreditEntryId,
                        TargetPublicId = allocation.Credit.PublicId,
                        TargetDescription = allocation.Credit.Description,
                        Amount = allocation.Amount,
                        EffectiveOn = allocation.Credit.EffectiveOn,
                    })
                    .Concat(allocations
                        .Where(allocation => allocation.CreditEntryId == row.TenantLedgerEntryId)
                        .Select(allocation => new AllocationRef
                        {
                            TargetSourceId = allocation.DebitEntryId,
                            TargetPublicId = allocation.Debit.PublicId,
                            TargetDescription = allocation.Debit.Description,
                            Amount = allocation.Amount,
                            EffectiveOn = allocation.Debit.EffectiveOn,
                        }))
                    .ToArray();
            }
        }

        return new AccountingPage<TenantLedgerRow>
        {
            Items = page,
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
        };
    }

    public async Task<IReadOnlyList<TenantMonthSummary>?> GetTenantMonthSummaryAsync(
        int portfolioId, int tenantAccountId, TenantMonthSummaryQuery query, CancellationToken ct = default)
    {
        var from = query.From;
        var to = query.To;
        var summaries = await _db.Database.SqlQuery<TenantMonthSummary>($$"""
            WITH filtered AS (
                SELECT
                    "EffectiveOn",
                    "Currency",
                    CASE WHEN "Direction" = 'Debit'
                              AND "EntryType" NOT IN ('Credit', 'Adjustment')
                         THEN "Amount" ELSE 0 END AS "ChargeAmount",
                    CASE WHEN "EntryType" = 'PaymentReceipt' AND "Direction" = 'Credit'
                         THEN "Amount" ELSE 0 END AS "PaymentAmount",
                    CASE WHEN "EntryType" IN ('Credit', 'Adjustment') AND "Direction" = 'Credit'
                         THEN "Amount" ELSE 0 END AS "CreditAmount"
                FROM "TenantLedgerEntries"
                WHERE "PortfolioId" = {{portfolioId}}
                  AND "TenantAccountId" = {{tenantAccountId}}
                  AND ({{from}} IS NULL OR "EffectiveOn" >= {{from}})
                  AND ({{to}} IS NULL OR "EffectiveOn" <= {{to}})
            ), monthly AS (
                SELECT
                    EXTRACT(YEAR FROM "EffectiveOn")::int AS "Year",
                    EXTRACT(MONTH FROM "EffectiveOn")::int AS "Month",
                    "Currency",
                    SUM("ChargeAmount") AS "ChargeAmount",
                    SUM("PaymentAmount") AS "PaymentAmount",
                    SUM("CreditAmount") AS "CreditAmount",
                    SUM("ChargeAmount" - "PaymentAmount" - "CreditAmount") AS "NetMovement"
                FROM filtered
                GROUP BY EXTRACT(YEAR FROM "EffectiveOn")::int,
                         EXTRACT(MONTH FROM "EffectiveOn")::int,
                         "Currency"
            ), running AS (
                SELECT
                    "Year", "Month", "Currency", "ChargeAmount", "PaymentAmount", "CreditAmount",
                    "NetMovement",
                    COALESCE(SUM("NetMovement") OVER (
                        PARTITION BY "Currency"
                        ORDER BY "Year", "Month"
                        ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS "OpeningBalance"
                FROM monthly
            )
            SELECT "Year", "Month", "Currency", "OpeningBalance", "ChargeAmount", "PaymentAmount",
                   "CreditAmount", "OpeningBalance" + "NetMovement" AS "ClosingBalance"
            FROM running
            ORDER BY "Year", "Month", "Currency"
            """).ToListAsync(ct);
        return summaries;
    }

    public async Task<TenantLedgerPeriodSummary?> GetTenantLedgerPeriodSummaryAsync(
        int portfolioId, int tenantAccountId, int months, CancellationToken ct = default)
    {
        if (months is not (3 or 6 or 9 or 12))
            throw new ArgumentOutOfRangeException(nameof(months), "Months must be 3, 6, 9, or 12.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddMonths(-months);
        var currency = await _db.TenantAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId && account.Id == tenantAccountId)
            .Select(account => account.Currency)
            .SingleOrDefaultAsync(ct);
        if (currency is null)
            return null;

        var entries = _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId && entry.TenantAccountId == tenantAccountId
                && entry.Currency == currency
                && entry.EffectiveOn >= from && entry.EffectiveOn <= today);

        var totals = await entries
            .GroupBy(_ => 1)
            .Select(group => new
            {
                ChargeAmount = group.Where(entry => entry.Direction == TenantLedgerDirection.Debit
                    && entry.EntryType != TenantLedgerEntryType.Credit
                    && entry.EntryType != TenantLedgerEntryType.Adjustment).Sum(entry => entry.Amount),
                PaymentAmount = group.Where(entry => entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.Direction == TenantLedgerDirection.Credit).Sum(entry => entry.Amount),
                CreditAmount = group.Where(entry => (entry.EntryType == TenantLedgerEntryType.Credit
                    || entry.EntryType == TenantLedgerEntryType.Adjustment)
                    && entry.Direction == TenantLedgerDirection.Credit).Sum(entry => entry.Amount),
                EndingBalance = group.Sum(entry => entry.Direction == TenantLedgerDirection.Debit
                    ? entry.Amount : -entry.Amount),
            })
            .SingleOrDefaultAsync(ct);
        if (totals is null)
        {
            return new TenantLedgerPeriodSummary
            {
                PeriodMonths = months,
                Currency = currency,
            };
        }

        // Aging buckets remain SQL-side; each amount is a conditional aggregate over open charges.
        var aging = await entries
            .Where(entry => entry.Direction == TenantLedgerDirection.Debit)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Current = group.Where(entry => entry.DueOn >= today)
                    .Sum(entry => entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id).Sum(allocation => allocation.Amount)),
                OneToThirty = group.Where(entry => entry.DueOn < today && entry.DueOn >= today.AddDays(-30))
                    .Sum(entry => entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id).Sum(allocation => allocation.Amount)),
                ThirtyOneToSixty = group.Where(entry => entry.DueOn < today.AddDays(-30) && entry.DueOn >= today.AddDays(-60))
                    .Sum(entry => entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id).Sum(allocation => allocation.Amount)),
                SixtyOneToNinety = group.Where(entry => entry.DueOn < today.AddDays(-60) && entry.DueOn >= today.AddDays(-90))
                    .Sum(entry => entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id).Sum(allocation => allocation.Amount)),
                NinetyPlus = group.Where(entry => entry.DueOn < today.AddDays(-90))
                    .Sum(entry => entry.Amount - _db.TenantLedgerAllocations
                        .Where(allocation => allocation.DebitEntryId == entry.Id).Sum(allocation => allocation.Amount)),
            })
            .SingleOrDefaultAsync(ct);

        return new TenantLedgerPeriodSummary
        {
            PeriodMonths = months,
            Currency = currency,
            ChargeAmount = totals.ChargeAmount,
            PaymentAmount = totals.PaymentAmount,
            CreditAmount = totals.CreditAmount,
            EndingBalance = totals.EndingBalance,
            AgingCurrent = aging?.Current ?? 0m,
            Aging1To30 = aging?.OneToThirty ?? 0m,
            Aging31To60 = aging?.ThirtyOneToSixty ?? 0m,
            Aging61To90 = aging?.SixtyOneToNinety ?? 0m,
            Aging90Plus = aging?.NinetyPlus ?? 0m,
        };
    }

    public async Task<AccountingPage<RecurringTenantChargeRow>> GetRecurringTenantChargesAsync(
        int portfolioId, int tenantAccountId, ListQuery query, CancellationToken ct = default)
    {
        var schedules = _db.RecurringTenantCharges.AsNoTracking()
            .Where(schedule => schedule.PortfolioId == portfolioId && schedule.TenantAccountId == tenantAccountId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            schedules = schedules.Where(schedule => EF.Functions.ILike(schedule.DisplayName, $"%{search}%"));
        }

        var totalCount = await schedules.CountAsync(ct);
        var skip = query.NormalizedSkip;
        var take = query.NormalizedTake;
        var rows = await schedules.OrderBy(schedule => schedule.NextRunDate).ThenBy(schedule => schedule.Id)
            .Skip(skip).Take(take)
            .Select(schedule => new RecurringTenantChargeRow
            {
                Id = schedule.Id,
                PublicId = schedule.PublicId,
                TenantAccountId = schedule.TenantAccountId,
                LeaseAgreementId = schedule.LeaseAgreementId,
                DisplayName = schedule.DisplayName,
                Amount = schedule.Amount,
                Currency = schedule.Currency,
                LedgerAccountId = schedule.LedgerAccountId,
                EffectiveStartOn = schedule.EffectiveStartOn,
                EffectiveEndOn = schedule.EffectiveEndOn,
                MonthlyDueDay = schedule.MonthlyDueDay,
                NextRunDate = schedule.NextRunDate,
                IsActive = schedule.IsActive,
                PropertyId = schedule.PropertyId,
                UnitId = schedule.UnitId,
            }).ToListAsync(ct);

        return new AccountingPage<RecurringTenantChargeRow>
        {
            Items = rows,
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
        };
    }

    private static IOrderedQueryable<GeneralLedgerRow> OrderGeneralLedger(
        IQueryable<GeneralLedgerRow> rows,
        GeneralLedgerQuery query) => query.SortField switch
    {
        "effectiveon" => query.SortDescending
            ? rows.OrderByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.PostedAtUtc)
                .ThenByDescending(row => row.LineId)
            : rows.OrderBy(row => row.EffectiveOn)
                .ThenBy(row => row.PostedAtUtc)
                .ThenBy(row => row.LineId),
        "postedatutc" => query.SortDescending
            ? rows.OrderByDescending(row => row.PostedAtUtc)
                .ThenByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.LineId)
            : rows.OrderBy(row => row.PostedAtUtc)
                .ThenBy(row => row.EffectiveOn)
                .ThenBy(row => row.LineId),
        "accountcode" => query.SortDescending
            ? rows.OrderByDescending(row => row.AccountCode)
                .ThenByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.PostedAtUtc)
                .ThenByDescending(row => row.LineId)
            : rows.OrderBy(row => row.AccountCode)
                .ThenBy(row => row.EffectiveOn)
                .ThenBy(row => row.PostedAtUtc)
                .ThenBy(row => row.LineId),
        _ => rows.OrderByDescending(row => row.EffectiveOn)
            .ThenByDescending(row => row.PostedAtUtc)
            .ThenByDescending(row => row.LineId),
    };

    private static IOrderedQueryable<TenantLedgerEntry> OrderTenantLedger(
        IQueryable<TenantLedgerEntry> entries,
        TenantLedgerQuery query) => query.SortField switch
    {
        "effectiveon" => query.SortDescending
            ? entries.OrderByDescending(entry => entry.EffectiveOn)
                .ThenByDescending(entry => entry.PostedAtUtc)
                .ThenByDescending(entry => entry.Id)
            : entries.OrderBy(entry => entry.EffectiveOn)
                .ThenBy(entry => entry.PostedAtUtc)
                .ThenBy(entry => entry.Id),
        "postedatutc" => query.SortDescending
            ? entries.OrderByDescending(entry => entry.PostedAtUtc)
                .ThenByDescending(entry => entry.EffectiveOn)
                .ThenByDescending(entry => entry.Id)
            : entries.OrderBy(entry => entry.PostedAtUtc)
                .ThenBy(entry => entry.EffectiveOn)
                .ThenBy(entry => entry.Id),
        _ => entries.OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.PostedAtUtc)
            .ThenByDescending(entry => entry.Id),
    };

    private IQueryable<JournalLine> FilterStatementLines(
        WorkspaceReadScope scope, StatementQuery query, DateOnly? from, DateOnly? to)
    {
        var lines = _db.JournalLines.AsNoTracking().WhereAccountingAuthorized(
            _db, scope, CapabilityKeys.MoneyBalancesRead, DateTime.UtcNow);
        if (from is DateOnly start)
            lines = lines.Where(line => line.JournalEntry!.EffectiveOn >= start);
        if (to is DateOnly end)
            lines = lines.Where(line => line.JournalEntry!.EffectiveOn <= end);
        if (!string.IsNullOrWhiteSpace(query.Currency))
            lines = lines.Where(line => line.JournalEntry!.Currency == query.Currency);
        if (query.PropertyId is int propertyId)
            lines = lines.Where(line => line.PropertyId == propertyId);
        if (query.UnitId is int unitId)
            lines = lines.Where(line => line.UnitId == unitId);
        return lines;
    }

    private async Task<StatementSection> LoadStatementSectionAsync(
        WorkspaceReadScope scope,
        StatementQuery query,
        AccountType accountType,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var sectionLines = FilterStatementLines(scope, query, from, to)
            .Where(line => line.LedgerAccount!.AccountType == accountType);
        var rows = await sectionLines
            .GroupBy(line => new
            {
                line.LedgerAccountId,
                line.LedgerAccount!.Code,
                line.LedgerAccount.Name,
                line.LedgerAccount.NormalBalance,
                line.JournalEntry!.Currency,
            })
            .Select(group => new FinancialStatementRow
            {
                AccountId = group.Key.LedgerAccountId,
                AccountCode = group.Key.Code,
                AccountName = group.Key.Name,
                Amount = group.Sum(line => group.Key.NormalBalance == NormalBalance.Debit
                    ? line.DebitAmount - line.CreditAmount
                    : line.CreditAmount - line.DebitAmount),
                Currency = group.Key.Currency,
            })
            .OrderBy(row => row.AccountCode)
            .ThenBy(row => row.Currency)
            .ToListAsync(ct);
        var subtotal = await sectionLines.SumAsync(line =>
            line.LedgerAccount!.NormalBalance == NormalBalance.Debit
                ? line.DebitAmount - line.CreditAmount
                : line.CreditAmount - line.DebitAmount, ct);
        return new StatementSection
        {
            Label = accountType.ToString(),
            Rows = rows,
            Subtotal = subtotal,
        };
    }

    private async Task<StatementTotalsSqlRow> LoadStatementTotalsAsync(
        WorkspaceReadScope scope,
        StatementQuery query,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        return await FilterStatementLines(scope, query, from, to)
            .GroupBy(_ => 1)
            .Select(group => new StatementTotalsSqlRow
            {
                Assets = group.Sum(line => line.LedgerAccount!.AccountType == AccountType.Asset
                    ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? line.DebitAmount - line.CreditAmount
                        : line.CreditAmount - line.DebitAmount
                    : 0m),
                LiabilitiesAndEquity = group.Sum(line =>
                    line.LedgerAccount!.AccountType == AccountType.Liability ||
                    line.LedgerAccount.AccountType == AccountType.Equity
                        ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                            ? line.DebitAmount - line.CreditAmount
                            : line.CreditAmount - line.DebitAmount
                        : 0m),
                Income = group.Sum(line => line.LedgerAccount!.AccountType == AccountType.Income
                    ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? line.DebitAmount - line.CreditAmount
                        : line.CreditAmount - line.DebitAmount
                    : 0m),
                Expenses = group.Sum(line => line.LedgerAccount!.AccountType == AccountType.Expense
                    ? line.LedgerAccount.NormalBalance == NormalBalance.Debit
                        ? line.DebitAmount - line.CreditAmount
                        : line.CreditAmount - line.DebitAmount
                    : 0m),
            })
            .SingleOrDefaultAsync(ct) ?? new StatementTotalsSqlRow();
    }

    private sealed class StatementTotalsSqlRow
    {
        public decimal Assets { get; init; }
        public decimal LiabilitiesAndEquity { get; init; }
        public decimal Income { get; init; }
        public decimal Expenses { get; init; }
    }

    private sealed class MoneyPositionSqlRow
    {
        public decimal TotalCashOnHand { get; init; }
        public decimal TenantDepositsHeld { get; init; }
        public decimal RentStillOwed { get; init; }
        public decimal LoanBalance { get; init; }
        public decimal Assets { get; init; }
        public decimal Liabilities { get; init; }
        public decimal ProfitOrLoss { get; init; }
    }

    private sealed class DepositPositionSqlRow
    {
        public decimal TenantDepositsHeld { get; init; }
    }

    private sealed class CashMovementSqlRow
    {
        public decimal CashReceived { get; init; }
        public decimal CashPaid { get; init; }
    }

    private async Task<IReadOnlyList<int>> GetSourceDocumentIdsAsync(
        int portfolioId,
        JournalSourceType sourceType,
        long sourceId,
        CancellationToken ct)
    {
        var entityType = sourceType switch
        {
            JournalSourceType.TenantCharge or
            JournalSourceType.TenantReceipt or
            JournalSourceType.ProviderSettlement or
            JournalSourceType.TenantConcession or
            JournalSourceType.ReceivableWriteOff => nameof(TenantLedgerEntry),
            JournalSourceType.SecurityDepositReceipt or
            JournalSourceType.SecurityDepositRefund or
            JournalSourceType.SecurityDepositApplication => nameof(SecurityDepositEntry),
            JournalSourceType.ExpensePayment or
            JournalSourceType.BillIncurred or
            JournalSourceType.BillPayment => nameof(Expense),
            JournalSourceType.BankTransfer => nameof(BankTransaction),
            JournalSourceType.LoanPayment => nameof(LoanPayment),
            JournalSourceType.CapitalPurchase or
            JournalSourceType.Depreciation => nameof(CapitalAsset),
            JournalSourceType.OwnerContribution => nameof(OwnerContribution),
            JournalSourceType.OwnerDistribution => nameof(OwnerDistribution),
            _ => null,
        };

        IQueryable<int> documentIds = _db.StoredFiles.AsNoTracking()
            .Where(file =>
                entityType != null &&
                file.PortfolioId == portfolioId &&
                file.EntityType == entityType &&
                file.EntityId == sourceId &&
                file.DeletedAt == null)
            .Select(file => file.Id);
        if (sourceType is JournalSourceType.TenantCharge or
            JournalSourceType.TenantReceipt or
            JournalSourceType.ProviderSettlement or
            JournalSourceType.TenantConcession or
            JournalSourceType.ReceivableWriteOff)
        {
            documentIds = documentIds.Union(_db.TenantLedgerEntries.AsNoTracking()
                .Where(entry =>
                    entry.PortfolioId == portfolioId &&
                    entry.Id == sourceId &&
                    entry.SourceStoredFileId != null)
                .Select(entry => entry.SourceStoredFileId!.Value));
        }
        else if (sourceType is JournalSourceType.SecurityDepositReceipt or
                 JournalSourceType.SecurityDepositRefund or
                 JournalSourceType.SecurityDepositApplication)
        {
            documentIds = documentIds.Union(_db.SecurityDepositEntries.AsNoTracking()
                .Where(entry =>
                    entry.PortfolioId == portfolioId &&
                    entry.Id == sourceId &&
                    entry.SourceStoredFileId != null)
                .Select(entry => entry.SourceStoredFileId!.Value));
        }

        return await documentIds.Distinct().OrderBy(id => id).ToListAsync(ct);
    }

    private async Task<BankReconciliationEvidence?> GetBankEvidenceAsync(
        int portfolioId, JournalSourceType sourceType, long sourceId, CancellationToken ct)
    {
        var bank = await _db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.PortfolioId == portfolioId
                && ((sourceType == JournalSourceType.TenantReceipt
                        && transaction.MatchedTenantLedgerEntryId == sourceId)
                    || (sourceType == JournalSourceType.ExpensePayment
                        && transaction.MatchedExpenseId == sourceId)
                    || (sourceType == JournalSourceType.LoanPayment
                        && transaction.MatchedLoanPaymentId == sourceId)
                    || (sourceType == JournalSourceType.OwnerDistribution
                        && transaction.MatchedOwnerDistributionId == sourceId)))
            .OrderByDescending(transaction => transaction.UpdatedAt)
            .Select(transaction => new BankReconciliationEvidence
            {
                BankTransactionId = transaction.Id,
                BankAccountLabel = transaction.BankConnection!.AccountName,
                MatchedOn = transaction.ExpenseMatchAppliedAt == null
                    ? null : DateOnly.FromDateTime(transaction.ExpenseMatchAppliedAt.Value),
                Status = transaction.MatchStatus,
            })
            .FirstOrDefaultAsync(ct);
        return bank;
    }
}
