using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILoanService"/>
public class LoanService : ILoanService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public LoanService(RentalCommandDbContext db, TimeProvider timeProvider, IAtomicUnitOfWork atomic)
    {
        _db = db;
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

        var items = await MoneyResponseProjection.Loans(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new LoanListResponse
        {
            Items = items,
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

    private static Task<LoanResponse?> GetAsync(
        IQueryable<Loan> loans, int portfolioId, int id, CancellationToken ct)
        => MoneyResponseProjection.Loans(
                loans.Where(loan => loan.Id == id && loan.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);

    public async Task<LoanResponse?> CreateAsync(
        WorkspaceReadScope scope, CreateLoanRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, idempotencyKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<LoanResponse>(outcome.Value);
    }

    public async Task<LoanResponse?> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateLoanRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Update, id, idempotencyKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<LoanResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope, int id, string idempotencyKey, CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Delete, id, idempotencyKey, new object());
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found;
    }

    public async Task<LoanPaymentResponse?> PostPaymentAsync(
        WorkspaceReadScope scope,
        int loanId,
        int paymentId,
        PostLoanPaymentRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        request.LoanId = loanId;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.PostPayment, paymentId, idempotencyKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        if (!outcome.Value.Found) return null;
        return ReadSnapshot<LoanPaymentResponse>(outcome.Value);
    }

    private static TResponse ReadSnapshot<TResponse>(AtomicMoneyMutationResult result) where TResponse : class =>
        result.ResponseJson is { Length: > 0 } json
            ? System.Text.Json.JsonSerializer.Deserialize<TResponse>(json)
                ?? throw new AtomicReceiptInvariantException("The money receipt snapshot is invalid.")
            : throw new AtomicReceiptInvariantException("The money receipt snapshot is missing.");

    public async Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(
        int portfolioId, int loanId, LoanPaymentQuery? query = null, CancellationToken ct = default)
    {
        // Confirm the loan is in-portfolio before returning its schedule (IDOR guard).
        var loanInScope = await _db.Loans
            .AsNoTracking()
            .AnyAsync(l => l.Id == loanId && l.PortfolioId == portfolioId, ct);
        if (!loanInScope)
            return null;

        return await BuildPaymentQuery(
                _db.LoanPayments.AsNoTracking()
                    .Where(p => p.LoanId == loanId && p.PortfolioId == portfolioId),
                query)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoanPaymentResponse>?> GetPaymentsAsync(
        WorkspaceReadScope scope, int loanId, LoanPaymentQuery? query = null, CancellationToken ct = default)
    {
        var loanInScope = await _db.Loans.AsNoTracking()
            .WhereMoneyAuthorized(_db, scope, CapabilityKeys.MoneyBalancesRead, _timeProvider.UtcNow())
            .AnyAsync(loan => loan.Id == loanId, ct);
        if (!loanInScope)
            return null;

        return await BuildPaymentQuery(
                _db.LoanPayments.AsNoTracking()
                    .Where(payment => payment.LoanId == loanId && payment.PortfolioId == scope.PortfolioId),
                query)
            .ToListAsync(ct);
    }

    private static IQueryable<LoanPaymentResponse> BuildPaymentQuery(
        IQueryable<LoanPayment> payments,
        LoanPaymentQuery? query)
    {
        query ??= new LoanPaymentQuery();
        if (query.Status is { } status)
            payments = payments.Where(payment => payment.Status == status);

        var ordered = query.SortField switch
        {
            "duedate" => query.SortDescending
                ? payments.OrderByDescending(payment => payment.DueDate).ThenByDescending(payment => payment.Id)
                : payments.OrderBy(payment => payment.DueDate).ThenBy(payment => payment.Id),
            _ => query.SortDescending
                ? payments.OrderByDescending(payment => payment.PeriodKey).ThenByDescending(payment => payment.Id)
                : payments.OrderBy(payment => payment.PeriodKey).ThenBy(payment => payment.Id),
        };

        var paged = ordered.Skip(query.NormalizedSkip);
        if (query.NormalizedTake is int take)
            paged = paged.Take(take);

        return paged.Select(payment => new LoanPaymentResponse
        {
            Id = payment.Id,
            LoanId = payment.LoanId,
            PeriodKey = payment.PeriodKey,
            DueDate = payment.DueDate,
            PaidDate = payment.PaidDate,
            InterestAmount = payment.InterestAmount,
            PrincipalAmount = payment.PrincipalAmount,
            EscrowAmount = payment.EscrowAmount,
            TotalAmount = payment.TotalAmount,
            BalanceAfter = payment.BalanceAfter,
            Status = payment.Status,
            PaymentDoesNotCoverInterest = payment.PaymentDoesNotCoverInterest,
        });
    }
}
