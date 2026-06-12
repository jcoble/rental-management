using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAppointmentService"/>
public class AppointmentService : IAppointmentService
{
    private const string EntityType = "Appointment";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public AppointmentService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<AppointmentResponse>> ListAsync(int portfolioId, int? propertyId, int? tenantId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Appointments
            .AsNoTracking()
            .Include(a => a.Property)
            .Include(a => a.Unit)
            .Include(a => a.Tenant)
            .Where(a => a.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(a => a.PropertyId == propertyId.Value);
        }

        if (tenantId.HasValue)
        {
            q = q.Where(a => a.TenantId == tenantId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                EF.Functions.ILike(a.Title, $"%{term}%") ||
                (a.ProspectName != null && EF.Functions.ILike(a.ProspectName, $"%{term}%")) ||
                (a.ProspectEmail != null && EF.Functions.ILike(a.ProspectEmail, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "title" => query.SortDescending ? q.OrderByDescending(a => a.Title) : q.OrderBy(a => a.Title),
            "status" => query.SortDescending ? q.OrderByDescending(a => a.Status) : q.OrderBy(a => a.Status),
            "type" => query.SortDescending ? q.OrderByDescending(a => a.Type) : q.OrderBy(a => a.Type),
            "scheduledstart" => query.SortDescending ? q.OrderByDescending(a => a.ScheduledStart) : q.OrderBy(a => a.ScheduledStart),
            "updatedat" => query.SortDescending ? q.OrderByDescending(a => a.UpdatedAt) : q.OrderBy(a => a.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(a => a.ScheduledStart) : q.OrderBy(a => a.ScheduledStart),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(AppointmentResponse.FromEntity).ToList();
    }

    public async Task<AppointmentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Appointments
            .AsNoTracking()
            .Include(a => a.Property)
            .Include(a => a.Unit)
            .Include(a => a.Tenant)
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);

        return entity == null ? null : AppointmentResponse.FromEntity(entity);
    }

    public async Task<AppointmentResponse?> CreateAsync(int portfolioId, CreateAppointmentRequest request, CancellationToken ct = default)
    {
        if (!await ReferencesInScopeAsync(portfolioId, request.PropertyId, request.UnitId, request.LeaseId, request.TenantId, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Appointment
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            LeaseId = request.LeaseId,
            TenantId = request.TenantId,
            Title = request.Title,
            ProspectName = request.ProspectName,
            ProspectEmail = request.ProspectEmail,
            Type = request.Type,
            Status = request.Status,
            ScheduledStart = request.ScheduledStart.ToUtc(),
            ScheduledEnd = request.ScheduledEnd.ToUtc(),
            AssignedTo = request.AssignedTo,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Appointments.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = AppointmentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<AppointmentResponse?> UpdateAsync(int portfolioId, int id, UpdateAppointmentRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (!await ReferencesInScopeAsync(portfolioId, request.PropertyId, request.UnitId, request.LeaseId, request.TenantId, ct))
        {
            return null;
        }

        if (request.PropertyId.HasValue) entity.PropertyId = request.PropertyId;
        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.LeaseId.HasValue) entity.LeaseId = request.LeaseId;
        if (request.TenantId.HasValue) entity.TenantId = request.TenantId;
        if (request.Title != null) entity.Title = request.Title;
        if (request.ProspectName != null) entity.ProspectName = request.ProspectName;
        if (request.ProspectEmail != null) entity.ProspectEmail = request.ProspectEmail;
        if (request.Type.HasValue) entity.Type = request.Type.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.ScheduledStart.HasValue) entity.ScheduledStart = request.ScheduledStart.Value.ToUtc();
        if (request.ScheduledEnd.HasValue) entity.ScheduledEnd = request.ScheduledEnd.ToUtc();
        if (request.AssignedTo != null) entity.AssignedTo = request.AssignedTo;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = AppointmentResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // No soft-delete column on Appointment; remove the row outright.
        _db.Appointments.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    /// <summary>Confirms each supplied optional FK belongs to the caller's portfolio (no cross-tenant linking).</summary>
    private async Task<bool> ReferencesInScopeAsync(int portfolioId, int? propertyId, int? unitId, int? leaseId, int? tenantId, CancellationToken ct)
    {
        if (propertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, propertyId.Value, ct))
        {
            return false;
        }

        if (unitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, unitId.Value, null, ct))
        {
            return false;
        }

        if (leaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, leaseId.Value, ct))
        {
            return false;
        }

        if (tenantId.HasValue &&
            !await _db.EnsureTenantInPortfolioAsync(portfolioId, tenantId.Value, ct))
        {
            return false;
        }

        return true;
    }
}
