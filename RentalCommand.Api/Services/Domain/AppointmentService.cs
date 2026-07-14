using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAppointmentService"/>
public class AppointmentService : IAppointmentService
{
    private const string EntityType = "Appointment";
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingShowingsManage];
    private static readonly string[] ScheduleSummaryReadCapabilities =
        [CapabilityKeys.WorkRead, CapabilityKeys.LeasingShowingsManage];
    private static readonly string[] WriteCapabilities =
        [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingShowingsManage];

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

        return await ListPageFromQueryAsync(q, query, ct);
    }

    public async Task<IReadOnlyList<AppointmentResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        int? tenantId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(
            scope,
            ToAppointmentListQuery(query, propertyId, tenantId),
            ct);
        return page.Items;
    }

    public Task<AppointmentListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        AppointmentListQuery query,
        CancellationToken ct = default)
    {
        var authorized = AuthorizedAppointments(
            _db.Appointments.AsNoTracking(),
            scope,
            ReadCapabilities,
            _timeProvider.UtcNow());
        return ListPageFromQueryAsync(authorized, query, ct);
    }

    public async Task<AppointmentScheduleSummaryResponse> GetScheduleSummaryAuthorizedAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default)
    {
        var windowStartUtc = _timeProvider.UtcNow();
        var windowEndUtc = windowStartUtc.AddDays(7);

        // One translated statement owns session/access/capability/property authorization, the
        // half-open date window, eligible statuses, and COUNT(*). A zero-row GROUP BY result is
        // normalized only after PostgreSQL has completed the aggregate; appointments are never
        // materialized for the shell.
        return await BuildScheduleSummaryQuery(scope, windowStartUtc, windowEndUtc)
            .SingleOrDefaultAsync(ct)
            ?? new AppointmentScheduleSummaryResponse
            {
                NextSevenDaysCount = 0,
                WindowStartUtc = windowStartUtc,
                WindowEndUtc = windowEndUtc,
            };
    }

    internal IQueryable<AppointmentScheduleSummaryResponse> BuildScheduleSummaryQuery(
        WorkspaceReadScope scope,
        DateTime windowStartUtc,
        DateTime windowEndUtc)
    {
        var authorized = AuthorizedAppointments(
            _db.Appointments.AsNoTracking(),
            scope,
            ScheduleSummaryReadCapabilities,
            windowStartUtc);

        return authorized
            .Where(appointment =>
                (appointment.Status == AppointmentStatus.Scheduled ||
                 appointment.Status == AppointmentStatus.Confirmed) &&
                appointment.ScheduledStart >= windowStartUtc &&
                appointment.ScheduledStart < windowEndUtc)
            .GroupBy(_ => 1)
            .Select(group => new AppointmentScheduleSummaryResponse
            {
                NextSevenDaysCount = group.Count(),
                WindowStartUtc = windowStartUtc,
                WindowEndUtc = windowEndUtc,
            });
    }

    private static async Task<AppointmentListResponse> ListPageFromQueryAsync(
        IQueryable<Appointment> q,
        AppointmentListQuery query,
        CancellationToken ct)
    {

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

    public async Task<AppointmentResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var entity = await AuthorizedAppointments(
                _db.Appointments
                    .AsNoTracking()
                    .Include(a => a.Property)
                    .Include(a => a.Unit)
                    .Include(a => a.Tenant),
                scope,
                ReadCapabilities,
                _timeProvider.UtcNow())
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        return entity == null ? null : AppointmentResponse.FromEntity(entity);
    }

    public async Task<AppointmentResponse?> CreateAsync(int portfolioId, CreateAppointmentRequest request, CancellationToken ct = default)
        => await CreateCoreAsync(portfolioId, request, broadcast: true, ct);

    public async Task<AppointmentResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateAppointmentRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var now = _timeProvider.UtcNow();
            var canCreate = request.PropertyId.HasValue
                ? await _db.Properties
                    .AsNoTracking()
                    .WhereAuthorized(_db, scope, WriteCapabilities, now)
                    .AnyAsync(property => property.Id == request.PropertyId.Value, innerCt)
                : await _db.AuthorizedAllPropertyAssignments(
                        scope,
                        WriteCapabilities,
                        CapabilityAuthorizationTargetKind.Property,
                        now)
                    .AnyAsync(innerCt);

            return canCreate
                ? await CreateCoreAsync(scope.PortfolioId, request, broadcast: false, innerCt)
                : null;
        }, ct);

        if (response != null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<AppointmentResponse?> CreateCoreAsync(
        int portfolioId,
        CreateAppointmentRequest request,
        bool broadcast,
        CancellationToken ct)
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
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }

        return response;
    }

    public async Task<AppointmentResponse?> UpdateAsync(int portfolioId, int id, UpdateAppointmentRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        return entity == null
            ? null
            : await UpdateCoreAsync(portfolioId, entity, request, broadcast: true, ct);
    }

    public async Task<AppointmentResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateAppointmentRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var now = _timeProvider.UtcNow();
            var entity = await AuthorizedAppointments(
                    _db.Appointments,
                    scope,
                    WriteCapabilities,
                    now)
                .FirstOrDefaultAsync(a => a.Id == id, innerCt);
            if (entity == null)
            {
                return null;
            }

            if (request.PropertyId.HasValue)
            {
                var destinationAuthorized = await _db.Properties
                    .AsNoTracking()
                    .WhereAuthorized(_db, scope, WriteCapabilities, now)
                    .AnyAsync(property => property.Id == request.PropertyId.Value, innerCt);
                if (!destinationAuthorized)
                {
                    return null;
                }
            }

            return await UpdateCoreAsync(
                scope.PortfolioId, entity, request, broadcast: false, innerCt);
        }, ct);

        if (response != null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<AppointmentResponse?> UpdateCoreAsync(
        int portfolioId,
        Appointment entity,
        UpdateAppointmentRequest request,
        bool broadcast,
        CancellationToken ct)
    {

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
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }

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

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var deleted = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var entity = await AuthorizedAppointments(
                    _db.Appointments,
                    scope,
                    WriteCapabilities,
                    _timeProvider.UtcNow())
                .FirstOrDefaultAsync(a => a.Id == id, innerCt);
            if (entity == null)
            {
                return false;
            }

            _db.Appointments.Remove(entity);
            await _db.SaveChangesAsync(innerCt);
            return true;
        }, ct);

        if (deleted)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        }

        return deleted;
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

    private IQueryable<Appointment> AuthorizedAppointments(
        IQueryable<Appointment> appointments,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities,
        DateTime utcNow)
    {
        var allProperties = _db.AuthorizedAllPropertyAssignments(
            scope,
            capabilities,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, utcNow);

        return appointments.Where(appointment =>
            appointment.PortfolioId == scope.PortfolioId &&
            ((appointment.PropertyId == null && allProperties.Any()) ||
             (appointment.PropertyId != null && authorizedProperties.Any(property =>
                 property.Id == appointment.PropertyId &&
                 property.PortfolioId == appointment.PortfolioId))));
    }
}
