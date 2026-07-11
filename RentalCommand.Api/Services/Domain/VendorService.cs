using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorService"/>
public class VendorService : IVendorService
{
    private const string EntityType = "Vendor";
    private static readonly AtomicJsonResultCodec<RequestVendorW9Result> RequestW9Codec =
        new("vendor-w9.request.result.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public VendorService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _atomic = atomic;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<VendorResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<VendorListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(v =>
                EF.Functions.ILike(v.Name, $"%{term}%") ||
                EF.Functions.ILike(v.ServiceType, $"%{term}%") ||
                (v.Email != null && EF.Functions.ILike(v.Email, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(v => v.Name) : q.OrderBy(v => v.Name),
            "servicetype" => query.SortDescending ? q.OrderByDescending(v => v.ServiceType) : q.OrderBy(v => v.ServiceType),
            "updatedat" => query.SortDescending ? q.OrderByDescending(v => v.UpdatedAt) : q.OrderBy(v => v.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(v => v.CreatedAt) : q.OrderBy(v => v.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new VendorListResponse
        {
            Items = items.Select(VendorResponse.FromEntity).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<VendorResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);

        return entity == null ? null : VendorResponse.FromEntity(entity);
    }

    public async Task<VendorResponse> CreateAsync(int portfolioId, CreateVendorRequest request, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var entity = new Vendor
        {
            PortfolioId = portfolioId,
            Name = request.Name,
            ServiceType = request.ServiceType,
            Email = request.Email,
            Phone = request.Phone,
            Website = request.Website,
            TaxId = request.TaxId,
            AddressLine1 = request.AddressLine1,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            Is1099Eligible = request.Is1099Eligible,
            W9OnFile = request.W9OnFile,
            Preferred = request.Preferred,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Vendors.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<VendorResponse?> UpdateAsync(int portfolioId, int id, UpdateVendorRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.Name != null) entity.Name = request.Name;
        if (request.ServiceType != null) entity.ServiceType = request.ServiceType;
        if (request.Email != null) entity.Email = request.Email;
        if (request.Phone != null) entity.Phone = request.Phone;
        if (request.Website != null) entity.Website = request.Website;
        if (request.TaxId != null) entity.TaxId = request.TaxId;
        if (request.AddressLine1 != null) entity.AddressLine1 = request.AddressLine1;
        if (request.City != null) entity.City = request.City;
        if (request.State != null) entity.State = request.State;
        if (request.PostalCode != null) entity.PostalCode = request.PostalCode;
        if (request.Is1099Eligible.HasValue) entity.Is1099Eligible = request.Is1099Eligible.Value;
        if (request.W9OnFile.HasValue) entity.W9OnFile = request.W9OnFile.Value;
        if (request.Preferred.HasValue) entity.Preferred = request.Preferred.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Block the soft-delete while the vendor is still attached to a live work order. A work order
        // keeps its VendorId column on a vendor soft-delete, so the order would render an empty vendor
        // name and lose its assignee silently. Only NON-terminal orders count (a Completed/Cancelled/
        // Archived order is historical — keeping the vendor link there is fine). Evaluated SQL-side as a
        // single COUNT; mirrors the active-lease guard on tenant delete.
        var openWorkOrderCount = await _db.WorkOrders
            .CountAsync(w => w.VendorId == id
                && w.PortfolioId == portfolioId
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived, ct);
        if (openWorkOrderCount > 0)
        {
            var plural = openWorkOrderCount == 1 ? "work order" : "work orders";
            throw new DomainValidationException(
                $"This vendor is assigned to {openWorkOrderCount} open {plural}; reassign or close them first.");
        }

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<RequestW9Result> RequestW9Async(
        int portfolioId,
        int id,
        string clientOperationId,
        int? changedByUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var normalizedOperationId = clientOperationId.Trim();
        if (normalizedOperationId.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientOperationId),
                "W-9 request ClientOperationId cannot exceed 160 characters.");
        }

        var operationDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedOperationId)))
            .ToLowerInvariant();
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "vendor-w9.request",
                $"{portfolioId}:{id}:{operationDigest}"),
            new RequestVendorW9Command(
                portfolioId,
                id,
                normalizedOperationId,
                changedByUserId,
                _timeProvider.UtcNow()),
            RequestW9Codec,
            ct);

        return outcome.Value.Outcome switch
        {
            RequestVendorW9Outcome.Queued => RequestW9Result.Queued(outcome.Value.Phone!),
            RequestVendorW9Outcome.VendorHasNoPhone => RequestW9Result.NoPhone(),
            _ => RequestW9Result.NotFound(),
        };
    }
}
