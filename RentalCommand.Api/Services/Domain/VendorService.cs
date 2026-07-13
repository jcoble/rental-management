using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

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

    public async Task<IReadOnlyList<VendorResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<VendorListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var q = AuthorizedVendors(scope, CapabilityKeys.WorkRead).AsNoTracking();

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

    public async Task<VendorResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        var entity = await AuthorizedVendors(scope, CapabilityKeys.WorkRead)
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        return entity == null ? null : VendorResponse.FromEntity(entity);
    }

    public Task<VendorResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateVendorRequest request,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        if (!await HasAllPropertiesAsync(scope, CapabilityKeys.WorkManage, token)) return null;
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
        await _db.SaveChangesAsync(token);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, token);
        return response;
    }, ct);

    public Task<VendorResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateVendorRequest request,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        var entity = await AuthorizedVendors(scope, CapabilityKeys.WorkManage)
            .FirstOrDefaultAsync(v => v.Id == id, token);
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

        await _db.SaveChangesAsync(token);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, token);
        return response;
    }, ct);

    public Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        var entity = await AuthorizedVendors(scope, CapabilityKeys.WorkManage)
            .FirstOrDefaultAsync(v => v.Id == id, token);
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
                && w.Status != WorkOrderStatus.Archived, token);
        if (openWorkOrderCount > 0)
        {
            var plural = openWorkOrderCount == 1 ? "work order" : "work orders";
            throw new DomainValidationException(
                $"This vendor is assigned to {openWorkOrderCount} open {plural}; reassign or close them first.");
        }

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(token);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, token);
        return true;
    }, ct);

    public async Task<RequestW9Result> RequestW9Async(
        WorkspaceReadScope scope,
        int id,
        string clientOperationId,
        int? changedByUserId,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
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
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.AccessRevision,
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

    private IQueryable<Vendor> AuthorizedVendors(WorkspaceReadScope scope, string capabilityKey)
    {
        var assignments = _db.AuthorizedWorkspaceAssignments(
            scope,
            [capabilityKey],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());
        return _db.Vendors.Where(vendor =>
            vendor.PortfolioId == scope.PortfolioId && assignments.Any());
    }

    private Task<bool> HasAllPropertiesAsync(
        WorkspaceReadScope scope,
        string capabilityKey,
        CancellationToken ct) =>
        _db.AuthorizedWorkspaceAssignments(
                scope,
                [capabilityKey],
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.UtcNow())
            .AnyAsync(ct);
}
