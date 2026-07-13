using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILoanService"/>
public class LoanService : ILoanService
{
    private const string EntityType = "Loan";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public LoanService(RentalCommandDbContext db, IDataUpdateService dataUpdate,
        TimeProvider timeProvider, IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<LoanResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, propertyId, query, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<LoanResponse>> ListAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, propertyId, query, ct);
        return page.Items;
    }

    public async Task<LoanListResponse> ListPageAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, propertyId, query);
        return await BuildPageAsync(filtered, query, ct);
    }

    public Task<LoanListResponse> ListPageAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(
            _db.Loans.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, propertyId, query);
        return BuildPageAsync(filtered, query, ct);
    }

    private static async Task<LoanListResponse> BuildPageAsync(
        IQueryable<Loan> filtered, ListQuery query, CancellationToken ct)
    {
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplySort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(l => new { Loan = l, PropertyName = l.Property!.Name })
            .ToListAsync(ct);

        return new LoanListResponse
        {
            Items = items.Select(x =>
            {
                var r = LoanResponse.FromEntity(x.Loan);
                r.PropertyName = x.PropertyName;
                return r;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<Loan> BuildListQuery(int portfolioId, int? propertyId, ListQuery query)
        => BuildListQuery(_db.Loans.AsNoTracking(), portfolioId, propertyId, query);

    private static IQueryable<Loan> BuildListQuery(
        IQueryable<Loan> q, int portfolioId, int? propertyId, ListQuery query)
    {
        q = q.Where(l => l.PortfolioId == portfolioId);

        if (propertyId.HasValue)
            q = q.Where(l => l.PropertyId == propertyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l => EF.Functions.ILike(l.Lender, $"%{term}%"));
        }

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(l => l.StartDate >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(l => l.StartDate < toUtcExclusive);

        return q;
    }

    private static IQueryable<Loan> ApplySort(IQueryable<Loan> q, ListQuery query)
    {
        var ordered = query.SortField switch
        {
            "lender" => query.SortDescending ? q.OrderByDescending(l => l.Lender) : q.OrderBy(l => l.Lender),
            "balance" or "currentbalance" => query.SortDescending ? q.OrderByDescending(l => l.CurrentBalance) : q.OrderBy(l => l.CurrentBalance),
            "originalamount" => query.SortDescending ? q.OrderByDescending(l => l.OriginalAmount) : q.OrderBy(l => l.OriginalAmount),
            "annualinterestratepct" => query.SortDescending ? q.OrderByDescending(l => l.AnnualInterestRatePct) : q.OrderBy(l => l.AnnualInterestRatePct),
            "monthlyprincipalinterest" => query.SortDescending ? q.OrderByDescending(l => l.MonthlyPrincipalInterest) : q.OrderBy(l => l.MonthlyPrincipalInterest),
            "monthlyescrow" => query.SortDescending ? q.OrderByDescending(l => l.MonthlyEscrow) : q.OrderBy(l => l.MonthlyEscrow),
            "status" => query.SortDescending ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "startdate" => query.SortDescending ? q.OrderByDescending(l => l.StartDate) : q.OrderBy(l => l.StartDate),
            "updatedat" => query.SortDescending ? q.OrderByDescending(l => l.UpdatedAt) : q.OrderBy(l => l.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
        };

        return ordered.ThenBy(l => l.Id);
    }

    public Task<LoanResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
        GetAsync(_db.Loans.AsNoTracking(), portfolioId, id, ct);

    public Task<LoanResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        GetAsync(
            _db.Loans.AsNoTracking().WhereMoneyAuthorized(
                _db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow()),
            scope.PortfolioId, id, ct);

    private static async Task<LoanResponse?> GetAsync(
        IQueryable<Loan> loans, int portfolioId, int id, CancellationToken ct)
    {
        var entity = await loans
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);

        return entity == null ? null : LoanResponse.FromEntity(entity);
    }

    public async Task<LoanResponse?> CreateAsync(int portfolioId, CreateLoanRequest request, CancellationToken ct = default)
    {
        // IDOR guard: the property must belong to this portfolio (no cross-tenant linking).
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
            return null;

        var now = _timeProvider.UtcNow();
        var entity = new Loan
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            Lender = request.Lender,
            OriginalAmount = request.OriginalAmount,
            CurrentBalance = request.CurrentBalance ?? request.OriginalAmount,
            AnnualInterestRatePct = request.AnnualInterestRatePct,
            TermMonths = request.TermMonths,
            StartDate = request.StartDate.ToUtc(),
            DayOfMonthDue = request.DayOfMonthDue,
            MonthlyPrincipalInterest = request.MonthlyPrincipalInterest,
            MonthlyEscrow = request.MonthlyEscrow,
            EscrowCoversTaxes = request.EscrowCoversTaxes,
            EscrowCoversInsurance = request.EscrowCoversInsurance,
            Status = request.Status,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Loans.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Re-read the property name for the response without a tracked nav.
        var propertyName = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == entity.PropertyId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        var response = LoanResponse.FromEntity(entity);
        response.PropertyName = propertyName;
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<LoanResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateLoanRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<LoanResponse?> UpdateAsync(int portfolioId, int id, UpdateLoanRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        if (request.Lender != null) entity.Lender = request.Lender;
        if (request.OriginalAmount.HasValue) entity.OriginalAmount = request.OriginalAmount.Value;
        if (request.CurrentBalance.HasValue) entity.CurrentBalance = request.CurrentBalance.Value;
        if (request.AnnualInterestRatePct.HasValue) entity.AnnualInterestRatePct = request.AnnualInterestRatePct.Value;
        if (request.TermMonths.HasValue) entity.TermMonths = request.TermMonths.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.DayOfMonthDue.HasValue) entity.DayOfMonthDue = request.DayOfMonthDue.Value;
        if (request.MonthlyPrincipalInterest.HasValue) entity.MonthlyPrincipalInterest = request.MonthlyPrincipalInterest.Value;
        if (request.MonthlyEscrow.HasValue) entity.MonthlyEscrow = request.MonthlyEscrow.Value;
        if (request.EscrowCoversTaxes.HasValue) entity.EscrowCoversTaxes = request.EscrowCoversTaxes.Value;
        if (request.EscrowCoversInsurance.HasValue) entity.EscrowCoversInsurance = request.EscrowCoversInsurance.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var propertyName = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == entity.PropertyId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        var response = LoanResponse.FromEntity(entity);
        response.PropertyName = propertyName;
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<LoanResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateLoanRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        if (await GetAsync(scope, id, ct) is null)
            return null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Update, id, idempotencyKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        var response = await GetAsync(scope.PortfolioId, id, ct);
        if (response is not null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
            return false;

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var visible = await GetAsync(scope, id, ct) is not null;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Delete, id, idempotencyKey, new object());
        AtomicCommandOutcome<AtomicMoneyMutationResult> outcome;
        try
        {
            outcome = await Atomic.ExecuteAsync(
                AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        }
        catch (UnauthorizedAccessException) when (!visible)
        {
            return false;
        }
        if (outcome.Value.Applied)
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        return visible || outcome.Disposition == AtomicCommandDisposition.Replayed
            ? outcome.Value.Found
            : false;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped loan mutations require the atomic persistence kernel.");

    public async Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(int portfolioId, int loanId, CancellationToken ct = default)
    {
        // Confirm the loan is in-portfolio before returning its schedule (IDOR guard).
        var loanInScope = await _db.Loans
            .AsNoTracking()
            .AnyAsync(l => l.Id == loanId && l.PortfolioId == portfolioId, ct);
        if (!loanInScope)
            return null;

        return await _db.LoanPayments
            .AsNoTracking()
            .Where(p => p.LoanId == loanId && p.PortfolioId == portfolioId)
            .OrderBy(p => p.PeriodKey)
            .Select(p => LoanPaymentResponse.FromEntity(p))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(
        WorkspaceReadScope scope, int loanId, CancellationToken ct = default)
    {
        var loanInScope = await _db.Loans.AsNoTracking()
            .WhereMoneyAuthorized(_db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow())
            .AnyAsync(loan => loan.Id == loanId, ct);
        if (!loanInScope)
            return null;

        return await _db.LoanPayments.AsNoTracking()
            .Where(payment => payment.LoanId == loanId && payment.PortfolioId == scope.PortfolioId)
            .OrderBy(payment => payment.PeriodKey)
            .Select(payment => LoanPaymentResponse.FromEntity(payment))
            .ToListAsync(ct);
    }
}
