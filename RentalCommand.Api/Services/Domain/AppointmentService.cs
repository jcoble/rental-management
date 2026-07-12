using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAppointmentService"/>
public class AppointmentService : IAppointmentService
{
    private const string EntityType = "Appointment";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public AppointmentService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<AppointmentResponse>> ListAsync(int portfolioId, int? propertyId, int? tenantId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToAppointmentListQuery(query, propertyId, tenantId), ct);
        return page.Items;
    }

    public async Task<AppointmentListResponse> ListPageAsync(int portfolioId, AppointmentListQuery query, CancellationToken ct = default)
    {
        var q = _db.Appointments
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

        if (query.PropertyId.HasValue)
        {
            q = q.Where(a => a.PropertyId == query.PropertyId.Value);
        }

        if (query.TenantId.HasValue)
        {
            q = q.Where(a => a.TenantId == query.TenantId.Value);
        }

        if (query.Type.HasValue)
        {
            q = q.Where(a => a.Type == query.Type.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(a => a.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                EF.Functions.ILike(a.Title, $"%{term}%") ||
                (a.ProspectName != null && EF.Functions.ILike(a.ProspectName, $"%{term}%")) ||
                (a.ProspectEmail != null && EF.Functions.ILike(a.ProspectEmail, $"%{term}%")) ||
                (a.AssignedTo != null && EF.Functions.ILike(a.AssignedTo, $"%{term}%")) ||
                (a.Property != null && EF.Functions.ILike(a.Property.Name, $"%{term}%")) ||
                (a.Unit != null && EF.Functions.ILike(a.Unit.UnitNumber, $"%{term}%")) ||
                (a.Tenant != null && (
                    EF.Functions.ILike(a.Tenant.FirstName, $"%{term}%") ||
                    EF.Functions.ILike(a.Tenant.LastName, $"%{term}%"))));
        }

        var totalCount = await q.CountAsync(ct);

        q = query.SortField switch
        {
            "title" => query.SortDescending ? q.OrderByDescending(a => a.Title) : q.OrderBy(a => a.Title),
            "propertyname" => query.SortDescending ? q.OrderByDescending(a => a.Property!.Name) : q.OrderBy(a => a.Property!.Name),
            "unitnumber" => query.SortDescending ? q.OrderByDescending(a => a.Unit!.UnitNumber) : q.OrderBy(a => a.Unit!.UnitNumber),
            "tenantname" => query.SortDescending
                ? q.OrderByDescending(a => a.Tenant!.FirstName).ThenByDescending(a => a.Tenant!.LastName)
                : q.OrderBy(a => a.Tenant!.FirstName).ThenBy(a => a.Tenant!.LastName),
            "status" => query.SortDescending ? q.OrderByDescending(a => a.Status) : q.OrderBy(a => a.Status),
            "type" => query.SortDescending ? q.OrderByDescending(a => a.Type) : q.OrderBy(a => a.Type),
            "scheduledstart" => query.SortDescending ? q.OrderByDescending(a => a.ScheduledStart) : q.OrderBy(a => a.ScheduledStart),
            "scheduledend" => query.SortDescending ? q.OrderByDescending(a => a.ScheduledEnd) : q.OrderBy(a => a.ScheduledEnd),
            "updatedat" => query.SortDescending ? q.OrderByDescending(a => a.UpdatedAt) : q.OrderBy(a => a.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(a => a.ScheduledStart) : q.OrderBy(a => a.ScheduledStart),
        };

        var rows = await q
            .Select(a => new AppointmentListRow(
                a,
                a.Property != null ? a.Property.Name : null,
                a.Unit != null ? a.Unit.UnitNumber : null,
                a.Tenant != null ? ((a.Tenant.FirstName + " " + a.Tenant.LastName)).Trim() : null))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new AppointmentListResponse
        {
            Items = rows.Select(ToListResponse).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static AppointmentListQuery ToAppointmentListQuery(ListQuery query, int? propertyId, int? tenantId) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        PropertyId = propertyId,
        TenantId = tenantId,
    };

    private sealed record AppointmentListRow(
        Appointment Appointment, string? PropertyName, string? UnitNumber, string? TenantName);

    private static AppointmentResponse ToListResponse(AppointmentListRow row)
    {
        var response = AppointmentResponse.FromEntity(row.Appointment);
        response.PropertyName = row.PropertyName;
        response.UnitNumber = row.UnitNumber;
        response.TenantName = row.TenantName;
        return response;
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
        if (!await ReferencesInScopeAsync(portfolioId, request.PropertyId, request.UnitId, request.LeaseManagementId, request.RentalApplicationId, request.TenantId, ct))
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var startUtc = request.ScheduledStart.ToUtc();
        var endUtc = request.ScheduledEnd.ToUtc();
        EnsureValidTimeRange(startUtc, endUtc);

        var entity = new Appointment
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            LeaseManagementId = request.LeaseManagementId,
            RentalApplicationId = request.RentalApplicationId,
            TenantId = request.TenantId,
            Title = request.Title,
            ProspectName = request.ProspectName,
            ProspectEmail = request.ProspectEmail,
            Type = request.Type,
            Status = request.Status,
            ScheduledStart = startUtc,
            ScheduledEnd = endUtc,
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

        if (!await ReferencesInScopeAsync(portfolioId, request.PropertyId, request.UnitId, request.LeaseManagementId, request.RentalApplicationId, request.TenantId, ct))
        {
            return null;
        }

        if (request.PropertyId.HasValue) entity.PropertyId = request.PropertyId;
        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.LeaseManagementId.HasValue) entity.LeaseManagementId = request.LeaseManagementId;
        if (request.RentalApplicationId.HasValue) entity.RentalApplicationId = request.RentalApplicationId;
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

        // Re-check the window against the effective values, since either end could have been patched
        // independently (e.g. moving only the start past a previously-set end).
        EnsureValidTimeRange(entity.ScheduledStart, entity.ScheduledEnd);

        entity.UpdatedAt = _timeProvider.UtcNow();

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

    /// <summary>
    /// Rejects an inverted or zero-length appointment window: when an end time is supplied it must be
    /// strictly after the start. A null end (open-ended appointment) is always allowed. Compares the
    /// UTC-normalized instants that will actually be stored. Throws a 400.
    /// </summary>
    private static void EnsureValidTimeRange(DateTime startUtc, DateTime? endUtc)
    {
        if (endUtc is { } end && end <= startUtc)
        {
            throw new DomainValidationException(
                "The appointment end time must be after its start time.");
        }
    }

    /// <summary>Confirms each supplied optional FK belongs to the caller's portfolio (no cross-tenant linking).</summary>
    private async Task<bool> ReferencesInScopeAsync(int portfolioId, int? propertyId, int? unitId, int? leaseManagementId, int? applicationId, int? tenantId, CancellationToken ct)
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

        if (leaseManagementId.HasValue &&
            !await _db.EnsureLeaseManagementInPortfolioAsync(portfolioId, leaseManagementId.Value, ct))
        {
            return false;
        }

        if (applicationId.HasValue &&
            !await _db.EnsureApplicationInPortfolioAsync(portfolioId, applicationId.Value, ct))
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
