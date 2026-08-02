using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Money;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Workspace-scoped owner funding lifecycle backed by the atomic money command.</summary>
public sealed class OwnerContributionService : IOwnerContributionService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public OwnerContributionService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<OwnerContributionResponse>> ListAsync(
        WorkspaceReadScope scope,
        OwnerContributionListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public Task<AccountingPage<OwnerContributionResponse>> ListPageAsync(
        WorkspaceReadScope scope,
        OwnerContributionListQuery query,
        CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.OwnerContributions.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow()),
            scope.PortfolioId,
            query);
        return BuildPageAsync(filtered, query, ct);
    }

    public Task<OwnerContributionResponse?> GetAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default) =>
        ProjectResponse(
                _db.OwnerContributions.AsNoTracking().WhereMoneyAuthorized(
                    _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow())
                    .Where(contribution =>
                        contribution.PortfolioId == scope.PortfolioId && contribution.Id == id))
            .FirstOrDefaultAsync(ct);

    public async Task<OwnerContributionResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateOwnerContributionRequest request,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ReadResult(await ExecuteAsync(
            scope, AtomicMoneyOperation.Create, 0, idempotencyKey, request, ct));

    public async Task<OwnerContributionResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateOwnerContributionRequest request,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ReadResult(await ExecuteAsync(
            scope, AtomicMoneyOperation.Update, id, idempotencyKey, request, ct));

    public async Task<OwnerContributionResponse?> ApproveAsync(
        WorkspaceReadScope scope,
        int id,
        ApproveOwnerContributionRequest request,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ReadResult(await ExecuteAsync(
            scope, AtomicMoneyOperation.Approve, id, idempotencyKey, request, ct));

    public async Task<OwnerContributionResponse?> RejectAsync(
        WorkspaceReadScope scope,
        int id,
        RejectOwnerContributionRequest request,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ReadResult(await ExecuteAsync(
            scope, AtomicMoneyOperation.Reject, id, idempotencyKey, request, ct));

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string idempotencyKey,
        CancellationToken ct = default) =>
        (await ExecuteAsync(scope, AtomicMoneyOperation.Delete, id, idempotencyKey, new object(), ct)).Found;

    private async Task<AtomicMoneyMutationResult> ExecuteAsync<TRequest>(
        WorkspaceReadScope scope,
        AtomicMoneyOperation operation,
        int entityId,
        string idempotencyKey,
        TRequest request,
        CancellationToken ct)
        where TRequest : class
    {
        var command = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerContribution,
            operation,
            entityId,
            idempotencyKey,
            request,
            _timeProvider.UtcNow());
        return (await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct)).Value;
    }

    private static OwnerContributionResponse? ReadResult(AtomicMoneyMutationResult result) =>
        result.Found
            ? System.Text.Json.JsonSerializer.Deserialize<OwnerContributionResponse>(result.ResponseJson!)
              ?? throw new InvalidOperationException(
                  "Atomic owner-contribution receipt did not contain a response snapshot.")
            : null;

    private async Task<AccountingPage<OwnerContributionResponse>> BuildPageAsync(
        IQueryable<OwnerContribution> filtered,
        OwnerContributionListQuery query,
        CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);
        var items = await ProjectResponse(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new AccountingPage<OwnerContributionResponse>
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static IQueryable<OwnerContribution> BuildListQuery(
        IQueryable<OwnerContribution> query,
        int portfolioId,
        OwnerContributionListQuery filter)
    {
        query = query.Where(contribution => contribution.PortfolioId == portfolioId);
        if (filter.OwnerEntityId.HasValue)
            query = query.Where(contribution => contribution.OwnerEntityId == filter.OwnerEntityId.Value);
        if (filter.PropertyId.HasValue)
            query = query.Where(contribution => contribution.PropertyId == filter.PropertyId.Value);
        if (filter.Year.HasValue)
        {
            var start = new DateTime(filter.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = start.AddYears(1);
            query = query.Where(contribution => contribution.Date >= start && contribution.Date < end);
        }
        if (filter.Status.HasValue)
            query = query.Where(contribution => contribution.Status == filter.Status.Value);
        var (from, to) = ListDateRange.UtcDay(filter.From, filter.To);
        if (from is { } fromUtc)
            query = query.Where(contribution => contribution.Date >= fromUtc);
        if (to is { } toUtcExclusive)
            query = query.Where(contribution => contribution.Date < toUtcExclusive);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(contribution =>
                (contribution.Memo != null && EF.Functions.ILike(contribution.Memo, $"%{term}%")) ||
                EF.Functions.ILike(contribution.OwnerEntity!.Name, $"%{term}%") ||
                (contribution.Property != null && EF.Functions.ILike(contribution.Property.Name, $"%{term}%")));
        }
        return query;
    }

    private static IQueryable<OwnerContribution> ApplySort(
        IQueryable<OwnerContribution> query,
        OwnerContributionListQuery filter)
    {
        var ordered = filter.SortField switch
        {
            "amount" => filter.SortDescending ? query.OrderByDescending(row => row.Amount) : query.OrderBy(row => row.Amount),
            "owner" or "ownername" => filter.SortDescending
                ? query.OrderByDescending(row => row.OwnerEntity!.Name)
                : query.OrderBy(row => row.OwnerEntity!.Name),
            "property" or "propertyname" => filter.SortDescending
                ? query.OrderByDescending(row => row.Property!.Name)
                : query.OrderBy(row => row.Property!.Name),
            "method" => filter.SortDescending ? query.OrderByDescending(row => row.Method) : query.OrderBy(row => row.Method),
            "updatedat" => filter.SortDescending ? query.OrderByDescending(row => row.UpdatedAt) : query.OrderBy(row => row.UpdatedAt),
            "createdat" => filter.SortDescending ? query.OrderByDescending(row => row.CreatedAt) : query.OrderBy(row => row.CreatedAt),
            "date" => filter.SortDescending ? query.OrderByDescending(row => row.Date) : query.OrderBy(row => row.Date),
            _ => query.OrderByDescending(row => row.Date),
        };
        return ordered.ThenByDescending(row => row.Id);
    }

    internal IQueryable<OwnerContributionResponse> ProjectResponse(
        IQueryable<OwnerContribution> query) =>
        query.Select(contribution => new OwnerContributionResponse
        {
            Id = contribution.Id,
            PortfolioId = contribution.PortfolioId,
            OwnerEntityId = contribution.OwnerEntityId,
            OwnerName = contribution.OwnerEntity!.Name,
            PropertyId = contribution.PropertyId,
            PropertyName = contribution.Property == null ? null : contribution.Property.Name,
            Date = contribution.Date,
            Amount = contribution.Amount,
            Method = contribution.Method,
            Status = contribution.Status,
            ApprovedAt = contribution.ApprovedAt,
            ApprovedBusinessDate = contribution.ApprovedBusinessDate,
            ApprovedByUserId = contribution.ApprovedByUserId,
            RejectedAt = contribution.RejectedAt,
            RejectedByUserId = contribution.RejectedByUserId,
            RejectionReason = contribution.RejectionReason,
            BankReference = contribution.BankReference,
            ExportReference = contribution.ExportReference,
            ExportedAt = contribution.ExportedAt,
            Memo = contribution.Memo,
            CreatedAt = contribution.CreatedAt,
            UpdatedAt = contribution.UpdatedAt,
            AccountId = _db.JournalEntries
                .Where(entry => entry.PortfolioId == contribution.PortfolioId &&
                    entry.SourceType == JournalSourceType.OwnerContribution && entry.SourceId == contribution.Id)
                .SelectMany(entry => entry.Lines)
                .Where(line => line.LedgerAccount!.AccountType == AccountType.Equity)
                .OrderBy(line => line.LedgerAccount!.Code)
                .Select(line => (int?)line.LedgerAccountId)
                .FirstOrDefault(),
            AccountName = _db.JournalEntries
                .Where(entry => entry.PortfolioId == contribution.PortfolioId &&
                    entry.SourceType == JournalSourceType.OwnerContribution && entry.SourceId == contribution.Id)
                .SelectMany(entry => entry.Lines)
                .Where(line => line.LedgerAccount!.AccountType == AccountType.Equity)
                .OrderBy(line => line.LedgerAccount!.Code)
                .Select(line => line.LedgerAccount!.Name)
                .FirstOrDefault(),
            JournalEntryPublicId = _db.JournalEntries
                .Where(entry => entry.PortfolioId == contribution.PortfolioId &&
                    entry.SourceType == JournalSourceType.OwnerContribution && entry.SourceId == contribution.Id)
                .OrderBy(entry => entry.Id)
                .Select(entry => (Guid?)entry.PublicId)
                .FirstOrDefault(),
        });
}
