using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using RentalCommand.Data.Vendors;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorService"/>
public class VendorService : IVendorService
{
    private const string EntityType = "Vendor";
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.WorkRead, CapabilityKeys.WorkManage];
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IWriteExecutor _writes;
    private readonly TimeProvider _timeProvider;

    public VendorService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IWriteExecutor writes)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _writes = writes;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<VendorResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<VendorListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var q = AuthorizedVendors(scope, ReadCapabilities).AsNoTracking();

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

        var items = await ProjectResponses(q)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new VendorListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<VendorResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        return await ProjectResponses(AuthorizedVendors(scope, ReadCapabilities).AsNoTracking())
            .FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task<VendorResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateVendorRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Create, 0, operationKey, request,
            createdAtUtc: _timeProvider.UtcNow());
        var write = CoreCrudWriteSupport.Write(
            writeRequest, CreateVendorAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return DeserializeSnapshot<VendorResponse>(outcome.Value);
    }

    public async Task<VendorResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateVendorRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request,
            changedAtUtc: _timeProvider.UtcNow());
        var write = CoreCrudWriteSupport.Write(
            writeRequest, UpdateVendorAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return DeserializeSnapshot<VendorResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new object(),
            changedAtUtc: _timeProvider.UtcNow());
        var write = CoreCrudWriteSupport.Write(
            writeRequest, DeleteVendorAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return outcome.Value.Found;
    }

    private async Task<AtomicCoreCrudMutationResult> CreateVendorAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var create = CoreCrudWriteSupport.Read<CreateVendorRequest>(request);
        var vendor = new Vendor
        {
            PortfolioId = request.PortfolioId, Name = create.Name, ServiceType = create.ServiceType,
            Email = create.Email, Phone = create.Phone, Website = create.Website, TaxId = create.TaxId,
            AddressLine1 = create.AddressLine1, City = create.City, State = create.State,
            PostalCode = create.PostalCode, Is1099Eligible = create.Is1099Eligible,
            W9OnFile = create.W9OnFile, Preferred = create.Preferred, Notes = create.Notes,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Add(vendor);
        context.BindSemanticAudit(vendor, TransactionalWriteDefaults.Audit(
            request, nameof(Vendor), AuditLogOperation.Created,
            $"Vendor {vendor.Name} created", 0));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(Vendor), vendor.Id, now, "entity");
        return new AtomicCoreCrudMutationResult(
            true, true, vendor.Id, JsonSerializer.Serialize(VendorResponse.FromEntity(vendor)));
    }

    private async Task<AtomicCoreCrudMutationResult> UpdateVendorAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var vendor = await _db.Vendors.SingleOrDefaultAsync(entity =>
            entity.Id == request.EntityId && entity.PortfolioId == request.PortfolioId
            && entity.DeletedAt == null, ct);
        if (vendor is null) return new AtomicCoreCrudMutationResult(false, false, 0);
        var update = CoreCrudWriteSupport.Read<UpdateVendorRequest>(request);
        if (update.Name is not null) vendor.Name = update.Name;
        if (update.ServiceType is not null) vendor.ServiceType = update.ServiceType;
        if (update.Email is not null) vendor.Email = update.Email;
        if (update.Phone is not null) vendor.Phone = update.Phone;
        if (update.Website is not null) vendor.Website = update.Website;
        if (update.TaxId is not null) vendor.TaxId = update.TaxId;
        if (update.AddressLine1 is not null) vendor.AddressLine1 = update.AddressLine1;
        if (update.City is not null) vendor.City = update.City;
        if (update.State is not null) vendor.State = update.State;
        if (update.PostalCode is not null) vendor.PostalCode = update.PostalCode;
        if (update.Is1099Eligible.HasValue) vendor.Is1099Eligible = update.Is1099Eligible.Value;
        if (update.W9OnFile.HasValue) vendor.W9OnFile = update.W9OnFile.Value;
        if (update.Preferred.HasValue) vendor.Preferred = update.Preferred.Value;
        if (update.Notes is not null) vendor.Notes = update.Notes;
        vendor.UpdatedAt = now;
        context.BindSemanticAudit(vendor, TransactionalWriteDefaults.Audit(
            request, nameof(Vendor), AuditLogOperation.Updated,
            $"Vendor {vendor.Name} updated", vendor.Id));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(Vendor), vendor.Id, now, "entity");
        return new AtomicCoreCrudMutationResult(
            true, true, vendor.Id, JsonSerializer.Serialize(VendorResponse.FromEntity(vendor)));
    }

    private async Task<AtomicCoreCrudMutationResult> DeleteVendorAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var vendor = await _db.Vendors.SingleOrDefaultAsync(entity =>
            entity.Id == request.EntityId && entity.PortfolioId == request.PortfolioId
            && entity.DeletedAt == null, ct);
        if (vendor is null) return new AtomicCoreCrudMutationResult(false, false, 0);
        var open = await _db.WorkOrders.AsNoTracking().CountAsync(work =>
            work.PortfolioId == request.PortfolioId && work.VendorId == vendor.Id
            && work.Status != WorkOrderStatus.Completed && work.Status != WorkOrderStatus.Cancelled
            && work.Status != WorkOrderStatus.Archived, ct);
        if (open > 0) throw new DomainValidationException(
            $"This vendor is assigned to {open} open {(open == 1 ? "work order" : "work orders")}; reassign or close them first.", 409);
        vendor.DeletedAt = now;
        vendor.UpdatedAt = now;
        context.BindSemanticAudit(vendor, TransactionalWriteDefaults.Audit(
            request, nameof(Vendor), AuditLogOperation.Deleted,
            $"Vendor {vendor.Name} deleted", vendor.Id));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(Vendor), vendor.Id, now, "entity", deleted: true);
        return new AtomicCoreCrudMutationResult(true, true, vendor.Id);
    }

    private Task AuthorizeCoreCrudReplayAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        CoreCrudWriteSupport.AuthorizeReplayAsync(request, _db, context, ct);

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicCoreCrudMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

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
                "A request key cannot exceed 160 characters.");
        }

        var operationDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedOperationId)))
            .ToLowerInvariant();
        var operationKey = $"{portfolioId}:{id}:{operationDigest}";
        var command = new RequestVendorW9Command(
                portfolioId,
                id,
                normalizedOperationId,
                changedByUserId,
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.AccessRevision,
                _timeProvider.UtcNow());
        var outcome = await _writes.ExecuteAsync(
            operationKey, RequestVendorW9Rule.Write(command, _db), ct);

        return outcome.Value.Outcome switch
        {
            RequestVendorW9Outcome.Queued => RequestW9Result.Queued(outcome.Value.Phone!),
            RequestVendorW9Outcome.VendorHasNoPhone => RequestW9Result.NoPhone(),
            _ => RequestW9Result.NotFound(),
        };
    }

    private static IQueryable<VendorResponse> ProjectResponses(IQueryable<Vendor> vendors) =>
        vendors.Select(entity => new VendorResponse
        {
            Id = entity.Id,
            PortfolioId = entity.PortfolioId,
            Name = entity.Name,
            ServiceType = entity.ServiceType,
            Email = entity.Email,
            Phone = entity.Phone,
            Website = entity.Website,
            TaxId = entity.TaxId,
            AddressLine1 = entity.AddressLine1,
            City = entity.City,
            State = entity.State,
            PostalCode = entity.PostalCode,
            Is1099Eligible = entity.Is1099Eligible,
            W9OnFile = entity.W9OnFile,
            Preferred = entity.Preferred,
            Notes = entity.Notes,
            AverageRating = entity.AverageRating,
            RatingCount = entity.RatingCount,
            JobsCompleted = entity.JobsCompleted,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        });

    private IQueryable<Vendor> AuthorizedVendors(WorkspaceReadScope scope, IReadOnlyCollection<string> capabilityKeys)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var assignments = _db.AuthorizedAssignmentsForScope(
            scope,
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            now);
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorizedForScope(_db, scope, capabilityKeys, now);
        return _db.Vendors.Where(vendor =>
            vendor.PortfolioId == scope.PortfolioId &&
            (assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties) ||
             authorizedProperties.Any()));
    }

}
