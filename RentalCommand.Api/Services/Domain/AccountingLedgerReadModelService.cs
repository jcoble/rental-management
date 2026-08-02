using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public interface IAccountingLedgerReadModelService
{
    Task<AccountingPage<ChartOfAccountsRow>> GetChartOfAccountsAsync(
        int portfolioId, ChartOfAccountsQuery query, CancellationToken ct = default);

    Task<AccountingPage<GeneralLedgerRow>> GetGeneralLedgerAsync(
        int portfolioId, GeneralLedgerQuery query, CancellationToken ct = default);

    Task<JournalDetail?> GetJournalDetailAsync(
        int portfolioId, Guid publicId, CancellationToken ct = default);

    Task<TrialBalanceResponse> GetTrialBalanceAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default);

    Task<FinancialStatementResponse> GetBalanceSheetAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default);

    Task<FinancialStatementResponse> GetIncomeStatementAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default);

    Task<AccountingPage<TenantLedgerRow>?> GetTenantLedgerAsync(
        int portfolioId, int tenantAccountId, TenantLedgerQuery query, CancellationToken ct = default);

    Task<TenantLedgerRow?> GetTenantLedgerEntryAsync(
        int portfolioId, int tenantAccountId, long entryId, CancellationToken ct = default);

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
        if (!string.IsNullOrWhiteSpace(query.AccountTypes))
        {
            var accountTypes = query.AccountTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => Enum.TryParse<AccountType>(value, true, out var accountType)
                    ? accountType
                    : (AccountType?)null)
                .Where(accountType => accountType.HasValue)
                .Select(accountType => accountType!.Value)
                .Distinct()
                .ToArray();
            if (accountTypes.Length > 0)
                accounts = accounts.Where(account => accountTypes.Contains(account.AccountType));
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
        int portfolioId, GeneralLedgerQuery query, CancellationToken ct = default)
    {
        var lines = _db.JournalLines
            .AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == portfolioId);

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
            DebitAmount = line.DebitAmount,
            CreditAmount = line.CreditAmount,
            Currency = line.JournalEntry.Currency,
            PropertyId = line.PropertyId,
            UnitId = line.UnitId,
            TenantAccountId = line.TenantAccountId,
            OwnerEntityId = line.OwnerEntityId,
            RunningBalance = accountIsSelected
                ? _db.JournalLines
                    .Where(previous => previous.JournalEntry!.PortfolioId == portfolioId
                        && previous.LedgerAccountId == line.LedgerAccountId
                        && previous.JournalEntry.Currency == line.JournalEntry.Currency
                        && (previous.JournalEntry.EffectiveOn < line.JournalEntry.EffectiveOn
                            || (previous.JournalEntry.EffectiveOn == line.JournalEntry.EffectiveOn
                                && (previous.JournalEntry.PostedAtUtc < line.JournalEntry.PostedAtUtc
                                    || (previous.JournalEntry.PostedAtUtc == line.JournalEntry.PostedAtUtc
                                        && (previous.JournalEntry.Id < line.JournalEntry.Id
                                            || (previous.JournalEntry.Id == line.JournalEntry.Id
                                                && previous.Id <= line.Id)))))))
                    .Select(previous => previous.DebitAmount - previous.CreditAmount)
                    .Sum()
                : null,
        });

        var rows = await projected
            .OrderBy(row => row.EffectiveOn)
            .ThenBy(row => row.PostedAtUtc)
            .ThenBy(row => row.JournalEntryPublicId)
            .ThenBy(row => row.LineId)
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
        int portfolioId, Guid publicId, CancellationToken ct = default)
    {
        var header = await _db.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId && entry.PublicId == publicId)
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
            DocumentIds = [],
            BankReconciliationEvidence = await GetBankEvidenceAsync(
                portfolioId, header.SourceType, header.SourceId, ct),
        };
    }

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default)
    {
        var lines = FilterStatementLines(portfolioId, query, query.From, query.To);
        var rows = await lines
            .GroupBy(line => new
            {
                line.LedgerAccountId,
                line.LedgerAccount!.Code,
                line.LedgerAccount.Name,
                line.LedgerAccount.AccountType,
                line.JournalEntry!.Currency,
            })
            .Select(group => new TrialBalanceRow
            {
                AccountId = group.Key.LedgerAccountId,
                AccountCode = group.Key.Code,
                AccountName = group.Key.Name,
                AccountType = group.Key.AccountType,
                DebitBalance = group.Sum(line => line.DebitAmount),
                CreditBalance = group.Sum(line => line.CreditAmount),
                Currency = group.Key.Currency,
            })
            .OrderBy(row => row.AccountCode)
            .ThenBy(row => row.Currency)
            .ToListAsync(ct);
        var totalDebits = await lines.SumAsync(line => line.DebitAmount, ct);
        var totalCredits = await lines.SumAsync(line => line.CreditAmount, ct);
        return new TrialBalanceResponse
        {
            Rows = rows,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits,
            IsBalanced = totalDebits == totalCredits,
        };
    }

    public async Task<FinancialStatementResponse> GetBalanceSheetAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default)
    {
        var sections = new List<StatementSection>();
        foreach (var type in new[] { AccountType.Asset, AccountType.Liability, AccountType.Equity })
        {
            var section = await LoadStatementSectionAsync(portfolioId, query, type, ct);
            if (section.Rows.Count > 0)
                sections.Add(section);
        }

        var totals = await LoadStatementTotalsAsync(portfolioId, query, ct);
        return new FinancialStatementResponse
        {
            Sections = sections,
            Totals = new StatementTotals
            {
                Total = totals.Assets,
                Assets = totals.Assets,
                LiabilitiesAndEquity = totals.LiabilitiesAndEquity,
            },
        };
    }

    public async Task<FinancialStatementResponse> GetIncomeStatementAsync(
        int portfolioId, StatementQuery query, CancellationToken ct = default)
    {
        var sections = new List<StatementSection>();
        foreach (var type in new[] { AccountType.Income, AccountType.Expense })
        {
            var section = await LoadStatementSectionAsync(portfolioId, query, type, ct);
            if (section.Rows.Count > 0)
                sections.Add(section);
        }

        var totals = await LoadStatementTotalsAsync(portfolioId, query, ct);
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
        var page = await entries
            .OrderBy(entry => entry.EffectiveOn)
            .ThenBy(entry => entry.PostedAtUtc)
            .ThenBy(entry => entry.Id)
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

    public async Task<TenantLedgerRow?> GetTenantLedgerEntryAsync(
        int portfolioId, int tenantAccountId, long entryId, CancellationToken ct = default)
    {
        var row = await _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.Id == entryId)
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
                ReversesEntryId = entry.ReversesEntryId,
                JournalEntryPublicId = _db.JournalEntries
                    .Where(journal => journal.PortfolioId == portfolioId && journal.SourceId == entry.Id)
                    .OrderByDescending(journal => journal.Id)
                    .Select(journal => (Guid?)journal.PublicId)
                    .FirstOrDefault(),
                Currency = entry.Currency,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null)
            return null;

        row.Allocations = await _db.TenantLedgerAllocations
            .AsNoTracking()
            .Where(allocation => allocation.PortfolioId == portfolioId
                && (allocation.DebitEntryId == entryId || allocation.CreditEntryId == entryId))
            .Select(allocation => allocation.DebitEntryId == entryId
                ? new AllocationRef
                {
                    TargetSourceId = allocation.CreditEntryId,
                    TargetPublicId = allocation.CreditEntry!.PublicId,
                    TargetDescription = allocation.CreditEntry.Description,
                    Amount = allocation.Amount,
                    EffectiveOn = allocation.CreditEntry.EffectiveOn,
                }
                : new AllocationRef
                {
                    TargetSourceId = allocation.DebitEntryId,
                    TargetPublicId = allocation.DebitEntry!.PublicId,
                    TargetDescription = allocation.DebitEntry.Description,
                    Amount = allocation.Amount,
                    EffectiveOn = allocation.DebitEntry.EffectiveOn,
                })
            .ToArrayAsync(ct);
        return row;
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
        var entries = _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId && entry.TenantAccountId == tenantAccountId
                && entry.EffectiveOn >= from && entry.EffectiveOn <= today);
        var currency = await entries.Select(entry => entry.Currency).Distinct().SingleOrDefaultAsync(ct);
        if (currency is null)
            return null;

        var totals = await entries
            .Where(entry => entry.Currency == currency)
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
            return null;

        // Aging buckets remain SQL-side; each amount is a conditional aggregate over open charges.
        var aging = await entries
            .Where(entry => entry.Currency == currency && entry.Direction == TenantLedgerDirection.Debit)
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

    private IQueryable<JournalLine> FilterStatementLines(
        int portfolioId, StatementQuery query, DateOnly? from, DateOnly? to)
    {
        var lines = _db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == portfolioId);
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
        int portfolioId, StatementQuery query, AccountType accountType, CancellationToken ct)
    {
        // Grouping, account-balance arithmetic, and the section subtotal all stay in one SQL
        // statement. The window aggregate repeats the SQL subtotal on each bounded result row so
        // the response can expose it without summing materialized rows in application code.
        var rows = await _db.Database.SqlQuery<StatementSqlRow>($$"""
            WITH grouped AS (
                SELECT account."Id" AS "AccountId",
                       account."Code" AS "AccountCode",
                       account."Name" AS "AccountName",
                       entry."Currency" AS "Currency",
                       SUM(CASE WHEN account."NormalBalance" = 'Debit'
                                THEN line."DebitAmount" - line."CreditAmount"
                                ELSE line."CreditAmount" - line."DebitAmount" END) AS "Amount"
                FROM "JournalLines" AS line
                JOIN "JournalEntries" AS entry
                  ON entry."Id" = line."JournalEntryId"
                JOIN "LedgerAccounts" AS account
                  ON account."Id" = line."LedgerAccountId"
                JOIN "Portfolios" AS portfolio
                  ON portfolio."Id" = entry."PortfolioId"
                 AND portfolio."DeletedAt" IS NULL
                WHERE entry."PortfolioId" = {{portfolioId}}
                  AND account."AccountType" = {{accountType.ToString()}}
                  AND (CAST({{query.From}} AS date) IS NULL OR entry."EffectiveOn" >= {{query.From}})
                  AND (CAST({{query.To}} AS date) IS NULL OR entry."EffectiveOn" <= {{query.To}})
                  AND (CAST({{query.Currency}} AS text) IS NULL OR entry."Currency" = {{query.Currency}})
                  AND (CAST({{query.PropertyId}} AS integer) IS NULL OR line."PropertyId" = {{query.PropertyId}})
                  AND (CAST({{query.UnitId}} AS integer) IS NULL OR line."UnitId" = {{query.UnitId}})
                GROUP BY account."Id", account."Code", account."Name",
                         account."NormalBalance", entry."Currency"
            ), with_subtotal AS (
                SELECT grouped.*,
                       SUM(grouped."Amount") OVER () AS "SectionSubtotal"
                FROM grouped
            )
            SELECT "AccountId", "AccountCode", "AccountName", "Currency", "Amount", "SectionSubtotal"
            FROM with_subtotal
            ORDER BY "AccountCode", "Currency"
            """).ToListAsync(ct);
        return new StatementSection
        {
            Label = accountType.ToString(),
            Rows = rows.Select(row => new FinancialStatementRow
            {
                AccountId = row.AccountId,
                AccountCode = row.AccountCode,
                AccountName = row.AccountName,
                Amount = row.Amount,
                Currency = row.Currency,
            }).ToArray(),
            Subtotal = rows.FirstOrDefault()?.SectionSubtotal ?? 0m,
        };
    }

    private sealed class StatementSqlRow
    {
        public int AccountId { get; init; }
        public string AccountCode { get; init; } = string.Empty;
        public string AccountName { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public decimal SectionSubtotal { get; init; }
    }

    private async Task<StatementTotalsSqlRow> LoadStatementTotalsAsync(
        int portfolioId, StatementQuery query, CancellationToken ct)
    {
        var totals = await _db.Database.SqlQuery<StatementTotalsSqlRow>($$"""
            SELECT
                COALESCE(SUM(CASE WHEN account."AccountType" = 'Asset'
                    THEN CASE WHEN account."NormalBalance" = 'Debit'
                              THEN line."DebitAmount" - line."CreditAmount"
                              ELSE line."CreditAmount" - line."DebitAmount" END
                    ELSE 0 END), 0) AS "Assets",
                COALESCE(SUM(CASE WHEN account."AccountType" IN ('Liability', 'Equity')
                    THEN CASE WHEN account."NormalBalance" = 'Debit'
                              THEN line."DebitAmount" - line."CreditAmount"
                              ELSE line."CreditAmount" - line."DebitAmount" END
                    ELSE 0 END), 0) AS "LiabilitiesAndEquity",
                COALESCE(SUM(CASE WHEN account."AccountType" = 'Income'
                    THEN CASE WHEN account."NormalBalance" = 'Debit'
                              THEN line."DebitAmount" - line."CreditAmount"
                              ELSE line."CreditAmount" - line."DebitAmount" END
                    ELSE 0 END), 0) AS "Income",
                COALESCE(SUM(CASE WHEN account."AccountType" = 'Expense'
                    THEN CASE WHEN account."NormalBalance" = 'Debit'
                              THEN line."DebitAmount" - line."CreditAmount"
                              ELSE line."CreditAmount" - line."DebitAmount" END
                    ELSE 0 END), 0) AS "Expenses"
            FROM "JournalLines" AS line
            JOIN "JournalEntries" AS entry
              ON entry."Id" = line."JournalEntryId"
            JOIN "LedgerAccounts" AS account
              ON account."Id" = line."LedgerAccountId"
            JOIN "Portfolios" AS portfolio
              ON portfolio."Id" = entry."PortfolioId"
             AND portfolio."DeletedAt" IS NULL
            WHERE entry."PortfolioId" = {{portfolioId}}
              AND (CAST({{query.From}} AS date) IS NULL OR entry."EffectiveOn" >= {{query.From}})
              AND (CAST({{query.To}} AS date) IS NULL OR entry."EffectiveOn" <= {{query.To}})
              AND (CAST({{query.Currency}} AS text) IS NULL OR entry."Currency" = {{query.Currency}})
              AND (CAST({{query.PropertyId}} AS integer) IS NULL OR line."PropertyId" = {{query.PropertyId}})
              AND (CAST({{query.UnitId}} AS integer) IS NULL OR line."UnitId" = {{query.UnitId}})
            """).SingleAsync(ct);
        return totals;
    }

    private sealed class StatementTotalsSqlRow
    {
        public decimal Assets { get; init; }
        public decimal LiabilitiesAndEquity { get; init; }
        public decimal Income { get; init; }
        public decimal Expenses { get; init; }
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
