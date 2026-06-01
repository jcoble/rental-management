using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISecurityDepositService"/>
public class SecurityDepositService : ISecurityDepositService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly RentalCommandDbContext _db;

    public SecurityDepositService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SecurityDepositResponse>> ListAsync(int portfolioId, int? leaseId, CancellationToken ct = default)
    {
        var q = _db.SecurityDepositHoldings
            .AsNoTracking()
            .Include(h => h.Lease)
            .Where(h => h.PortfolioId == portfolioId);

        if (leaseId.HasValue)
            q = q.Where(h => h.LeaseId == leaseId.Value);

        var items = await q.OrderByDescending(h => h.CreatedAt).ToListAsync(ct);
        return items.Select(SecurityDepositResponse.FromEntity).ToList();
    }

    public async Task<SecurityDepositResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .AsNoTracking()
            .Include(h => h.Lease)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        return entity == null ? null : SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> CreateAsync(int portfolioId, CreateDepositRequest request, CancellationToken ct = default)
    {
        // Verify the lease belongs to this portfolio.
        var lease = await _db.Leases
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == request.LeaseId && l.PortfolioId == portfolioId, ct);

        if (lease == null)
            return null;

        var now = DateTime.UtcNow;
        var entity = new SecurityDepositHolding
        {
            PortfolioId = portfolioId,
            LeaseId = request.LeaseId,
            Amount = request.Amount ?? lease.SecurityDeposit,
            Status = SecurityDepositStatus.Held,
            HeldAt = now,
            DeductionsJson = "[]",
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SecurityDepositHoldings.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Reload with navigation for response.
        entity.Lease = lease;
        return SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> AddDeductionAsync(int portfolioId, int id, AddDeductionRequest request, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .Include(h => h.Lease)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        // Reject if already returned.
        if (entity.Status is SecurityDepositStatus.Returned or SecurityDepositStatus.PartiallyReturned)
            return null;

        var deductions = string.IsNullOrWhiteSpace(entity.DeductionsJson)
            ? new List<DepositDeduction>()
            : JsonSerializer.Deserialize<List<DepositDeduction>>(entity.DeductionsJson, _jsonOptions) ?? [];

        deductions.Add(new DepositDeduction(request.Reason, request.Amount, request.Notes));

        entity.DeductionsJson = JsonSerializer.Serialize(deductions);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> ReturnAsync(int portfolioId, int id, ReturnDepositRequest request, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .Include(h => h.Lease)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        // Reject if already returned.
        if (entity.Status is SecurityDepositStatus.Returned or SecurityDepositStatus.PartiallyReturned)
            return null;

        var deductions = string.IsNullOrWhiteSpace(entity.DeductionsJson)
            ? new List<DepositDeduction>()
            : JsonSerializer.Deserialize<List<DepositDeduction>>(entity.DeductionsJson, _jsonOptions) ?? [];

        var totalDeductions = deductions.Sum(d => d.Amount);
        var net = Math.Max(0m, entity.Amount - totalDeductions);

        entity.ReturnedAmount = net;
        entity.ReturnedAt = DateTime.UtcNow;
        // "Returned" only when net == original amount (no deductions); otherwise "PartiallyReturned".
        entity.Status = totalDeductions > 0m ? SecurityDepositStatus.PartiallyReturned : SecurityDepositStatus.Returned;
        if (request.Notes != null)
            entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return SecurityDepositResponse.FromEntity(entity);
    }
}
