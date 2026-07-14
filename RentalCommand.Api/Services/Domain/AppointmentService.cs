using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAppointmentService"/>
public class AppointmentService : IAppointmentService
{
    private static readonly AtomicJsonResultCodec<OperationMutationResult> MutationCodec =
        new("appointment.mutation.v1");
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingShowingsManage];
    private static readonly string[] ScheduleSummaryReadCapabilities =
        [CapabilityKeys.WorkRead, CapabilityKeys.LeasingShowingsManage];
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;

    public AppointmentService(RentalCommandDbContext db, IDataUpdateService dataUpdate,
        TimeProvider timeProvider, IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
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

        var rows = await ProjectResponses(q)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new AppointmentListResponse
        {
            Items = rows,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static IQueryable<AppointmentResponse> ProjectResponses(IQueryable<Appointment> appointments) =>
        appointments.Select(a => new AppointmentResponse
            {
                Id = a.Id,
                PortfolioId = a.PortfolioId,
                PropertyId = a.PropertyId,
                UnitId = a.UnitId,
                LeaseManagementId = a.LeaseManagementId,
                RentalApplicationId = a.RentalApplicationId,
                TenantId = a.TenantId,
                Title = a.Title,
                ProspectName = a.ProspectName,
                ProspectEmail = a.ProspectEmail,
                Type = a.Type,
                Status = a.Status,
                ScheduledStart = a.ScheduledStart,
                ScheduledEnd = a.ScheduledEnd,
                AssignedTo = a.AssignedTo,
                Notes = a.Notes,
                PropertyName = a.Property == null ? null : a.Property.Name,
                UnitNumber = a.Unit == null ? null : a.Unit.UnitNumber,
                TenantName = a.Tenant == null
                    ? null
                    : (a.Tenant.FirstName + " " + a.Tenant.LastName).Trim(),
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
            });

    private static AppointmentListQuery ToAppointmentListQuery(ListQuery query, int? propertyId, int? tenantId) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        PropertyId = propertyId,
        TenantId = tenantId,
    };

    public async Task<AppointmentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await ProjectResponses(_db.Appointments.AsNoTracking()
                .Where(a => a.Id == id && a.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AppointmentResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        return await ProjectResponses(AuthorizedAppointments(
                _db.Appointments.AsNoTracking(), scope, ReadCapabilities, _timeProvider.UtcNow())
                .Where(a => a.Id == id))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AppointmentResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateAppointmentRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new CreateAppointmentCommand(
            scope.PortfolioId, Actor(scope), request.PropertyId, request.UnitId,
            request.LeaseManagementId, request.RentalApplicationId, request.TenantId,
            request.Title, request.ProspectName, request.ProspectEmail, request.Type,
            request.Status, request.ScheduledStart.ToUtc(), request.ScheduledEnd.ToUtc(),
            request.AssignedTo, request.Notes, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("appointment.create", idempotencyKey), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<AppointmentResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateAppointmentRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var command = new UpdateAppointmentCommand(
            scope.PortfolioId, Actor(scope), id, request.PropertyId, request.UnitId,
            request.LeaseManagementId, request.RentalApplicationId, request.TenantId,
            request.Title, request.ProspectName, request.ProspectEmail, request.Type,
            request.Status, request.ScheduledStart?.ToUtc(), request.ScheduledEnd.ToUtc(),
            request.AssignedTo, request.Notes, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("appointment.update", idempotencyKey), command, MutationCodec, ct);
        return Response(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, int? expectedPropertyId, string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new DeleteAppointmentCommand(
            scope.PortfolioId, Actor(scope), id, expectedPropertyId, idempotencyKey);
        var outcome = await Atomic.ExecuteAsync(
            Identity("appointment.delete", idempotencyKey), command, MutationCodec, ct);
        return outcome.Value.Outcome == OperationMutationOutcome.Applied;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Atomic appointment mutations are not configured.");

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static AtomicCommandIdentity Identity(string operation, string key)
    {
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return new AtomicCommandIdentity(operation, digest);
    }

    private static AppointmentResponse? Response(OperationMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<AppointmentResponse>(result.ResponseJson);

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
