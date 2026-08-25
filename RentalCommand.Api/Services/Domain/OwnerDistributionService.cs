using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Money;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerDistributionService"/>
public class OwnerDistributionService : IOwnerDistributionService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public OwnerDistributionService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IRequestWriteExecutor writes)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
    }

    public async Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<OwnerDistributionResponse>> ListAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<OwnerDistributionListResponse> ListPageAsync(
        int portfolioId, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, query);
        return await BuildPageAsync(filtered, query, ct);
    }

    public Task<OwnerDistributionListResponse> ListPageAsync(
        WorkspaceReadScope scope, OwnerDistributionListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.OwnerDistributions.AsNoTracking().WhereAuthorized(
                _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow()),
            scope.PortfolioId, query);
        return BuildPageAsync(filtered, query, ct);
    }

    private async Task<OwnerDistributionListResponse> BuildPageAsync(
        IQueryable<OwnerDistribution> filtered, OwnerDistributionListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await ProjectResponse(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new OwnerDistributionListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<OwnerDistributionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
        GetAsync(_db.OwnerDistributions.AsNoTracking(), portfolioId, id, ct);

    public Task<OwnerDistributionResponse?> GetAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        GetAsync(
            _db.OwnerDistributions.AsNoTracking().WhereAuthorized(
                _db, scope, CapabilityKeys.MoneyOwnerReportsRead, _timeProvider.UtcNow()),
            scope.PortfolioId, id, ct);

    private async Task<OwnerDistributionResponse?> GetAsync(
        IQueryable<OwnerDistribution> distributions, int portfolioId, int id, CancellationToken ct)
    {
        return await ProjectResponse(distributions.Where(d => d.Id == id && d.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OwnerDistributionResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Create, 0, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot(outcome.Value) : null;
    }

    public async Task<OwnerDistributionResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Update, id, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot(outcome.Value) : null;
    }

    public async Task<OwnerDistributionResponse?> ApproveAsync(
        WorkspaceReadScope scope, int id, ApproveOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Approve, id, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot(outcome.Value) : null;
    }

    public async Task<OwnerDistributionResponse?> RejectAsync(
        WorkspaceReadScope scope, int id, RejectOwnerDistributionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Reject, id, idempotencyKey, request,
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found ? ReadSnapshot(outcome.Value) : null;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Delete, id, idempotencyKey, new object(),
            _timeProvider.UtcNow());
        var outcome = await ExecuteAsync(command, ct);
        return outcome.Value.Found;
    }

    private static OwnerDistributionResponse ReadSnapshot(AtomicMoneyMutationResult result) =>
        System.Text.Json.JsonSerializer.Deserialize<OwnerDistributionResponse>(result.ResponseJson!)
        ?? throw new InvalidOperationException("Atomic owner-distribution receipt did not contain a response snapshot.");

    private Task<AtomicCommandOutcome<AtomicMoneyMutationResult>> ExecuteAsync(
        AtomicMoneyMutationCommand command, CancellationToken ct) =>
        _writes.ExecuteAsync(
            AtomicMoneyMutation.Identity(command).IdempotencyKey,
            AtomicMoneyMutation.Write(command, _db), ct);

    private IQueryable<OwnerDistribution> BuildListQuery(int portfolioId, OwnerDistributionListQuery query)
        => BuildListQuery(_db.OwnerDistributions.AsNoTracking(), portfolioId, query);

    private static IQueryable<OwnerDistribution> BuildListQuery(
        IQueryable<OwnerDistribution> q, int portfolioId, OwnerDistributionListQuery query)
    {
        q = q.Where(d => d.PortfolioId == portfolioId);

        if (query.OwnerEntityId.HasValue)
            q = q.Where(d => d.OwnerEntityId == query.OwnerEntityId.Value);

        if (query.PropertyId.HasValue)
            q = q.Where(d => d.PropertyId == query.PropertyId.Value);

        if (query.Year.HasValue)
        {
            var (start, end) = YearRange(query.Year.Value);
            q = q.Where(d => d.Date >= start && d.Date < end);
        }

        if (query.Status.HasValue)
            q = q.Where(d => d.Status == query.Status.Value);

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(d => d.Date >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(d => d.Date < toUtcExclusive);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d =>
                (d.Memo != null && EF.Functions.ILike(d.Memo, $"%{term}%")) ||
                EF.Functions.ILike(d.OwnerEntity!.Name, $"%{term}%") ||
                (d.Property != null && EF.Functions.ILike(d.Property.Name, $"%{term}%")));
        }

        return q;
    }

    private static IQueryable<OwnerDistribution> ApplySort(
        IQueryable<OwnerDistribution> q, OwnerDistributionListQuery query)
    {
        var ordered = query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(d => d.Amount) : q.OrderBy(d => d.Amount),
            "owner" or "ownername" => query.SortDescending
                ? q.OrderByDescending(d => d.OwnerEntity!.Name)
                : q.OrderBy(d => d.OwnerEntity!.Name),
            "property" or "propertyname" => query.SortDescending
                ? q.OrderByDescending(d => d.Property!.Name)
                : q.OrderBy(d => d.Property!.Name),
            "method" => query.SortDescending ? q.OrderByDescending(d => d.Method) : q.OrderBy(d => d.Method),
            "updatedat" => query.SortDescending ? q.OrderByDescending(d => d.UpdatedAt) : q.OrderBy(d => d.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(d => d.CreatedAt) : q.OrderBy(d => d.CreatedAt),
            "date" => query.SortDescending ? q.OrderByDescending(d => d.Date) : q.OrderBy(d => d.Date),
            _ => q.OrderByDescending(d => d.Date),
        };

        return ordered.ThenByDescending(d => d.Id);
    }

    private IQueryable<OwnerDistributionResponse> ProjectResponse(IQueryable<OwnerDistribution> query)
    {
        return query.Select(d => new OwnerDistributionResponse
        {
            Id = d.Id,
            PortfolioId = d.PortfolioId,
            OwnerEntityId = d.OwnerEntityId,
            OwnerName = d.OwnerEntity!.Name,
            PropertyId = d.PropertyId,
            PropertyName = d.Property == null ? null : d.Property.Name,
            Date = d.Date,
            Amount = d.Amount,
            Method = d.Method,
            Status = d.Status,
            ApprovedAt = d.ApprovedAt,
            ApprovedBusinessDate = d.ApprovedBusinessDate,
            ApprovedByUserId = d.ApprovedByUserId,
            RejectedAt = d.RejectedAt,
            RejectedByUserId = d.RejectedByUserId,
            RejectionReason = d.RejectionReason,
            BankReference = d.BankReference,
            ExportReference = d.ExportReference,
            ExportedAt = d.ExportedAt,
            Memo = d.Memo,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        });
    }

    private static (DateTime Start, DateTime End) YearRange(int year)
    {
        return (
            new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
