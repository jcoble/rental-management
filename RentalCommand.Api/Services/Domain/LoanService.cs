using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILoanService"/>
public class LoanService : ILoanService
{
    private const string EntityType = "Loan";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public LoanService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<LoanResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Loans
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId);

        if (propertyId.HasValue)
            q = q.Where(l => l.PropertyId == propertyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l => EF.Functions.ILike(l.Lender, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "lender" => query.SortDescending ? q.OrderByDescending(l => l.Lender) : q.OrderBy(l => l.Lender),
            "balance" => query.SortDescending ? q.OrderByDescending(l => l.CurrentBalance) : q.OrderBy(l => l.CurrentBalance),
            "status" => query.SortDescending ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "startdate" => query.SortDescending ? q.OrderByDescending(l => l.StartDate) : q.OrderBy(l => l.StartDate),
            _ => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
        };

        // Project the property name SQL-side (no per-row follow-up query).
        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(l => new { Loan = l, PropertyName = l.Property!.Name })
            .ToListAsync(ct);

        return items.Select(x =>
        {
            var r = LoanResponse.FromEntity(x.Loan);
            r.PropertyName = x.PropertyName;
            return r;
        }).ToList();
    }

    public async Task<LoanResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Loans
            .AsNoTracking()
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);

        return entity == null ? null : LoanResponse.FromEntity(entity);
    }

    public async Task<LoanResponse?> CreateAsync(int portfolioId, CreateLoanRequest request, CancellationToken ct = default)
    {
        // IDOR guard: the property must belong to this portfolio (no cross-tenant linking).
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
            return null;

        var now = DateTime.UtcNow;
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
        entity.UpdatedAt = DateTime.UtcNow;

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

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
            return false;

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

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
}
