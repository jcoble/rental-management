using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class AppointmentEndpoints
{
    public static WebApplication MapAppointmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/appointments");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var items = await db.Appointments
                .Where(a => a.PortfolioId == portfolioId)
                .Include(a => a.Property)
                .Include(a => a.Unit)
                .Include(a => a.Tenant)
                .OrderBy(a => a.ScheduledStart)
                .ToListAsync();

            return Results.Ok(items.Select(a => new
            {
                a.Id,
                a.PortfolioId,
                a.PropertyId,
                a.UnitId,
                a.LeaseId,
                a.TenantId,
                a.Title,
                a.ProspectName,
                a.ProspectEmail,
                Type = a.Type.ToString(),
                Status = a.Status.ToString(),
                a.ScheduledStart,
                a.ScheduledEnd,
                a.AssignedTo,
                a.Notes,
                PropertyName = a.Property?.Name,
                UnitNumber = a.Unit?.UnitNumber,
                TenantName = a.Tenant is null ? null : $"{a.Tenant.FirstName} {a.Tenant.LastName}",
                a.CreatedAt,
                a.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateAppointmentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var appt = new Appointment
            {
                PortfolioId = req.PortfolioId,
                PropertyId = req.PropertyId,
                UnitId = req.UnitId,
                LeaseId = req.LeaseId,
                TenantId = req.TenantId,
                Title = req.Title,
                ProspectName = req.ProspectName,
                ProspectEmail = req.ProspectEmail,
                Type = req.Type,
                Status = req.Status ?? AppointmentStatus.Scheduled,
                ScheduledStart = req.ScheduledStart,
                ScheduledEnd = req.ScheduledEnd,
                AssignedTo = req.AssignedTo,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Appointments.Add(appt);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.AppointmentScheduled,
                "Appointment",
                appt.Id,
                "Created",
                $"Appointment '{appt.Title}' scheduled for {appt.ScheduledStart:u}",
                "manual");

            await sse.BroadcastAsync("appointment:created", new { appt.Id, appt.PortfolioId });
            return Results.Created($"/api/appointments/{appt.Id}", appt);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateAppointmentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var appt = await db.Appointments.FindAsync(id);
            if (appt is null) return Results.NotFound();

            if (req.PropertyId.HasValue) appt.PropertyId = req.PropertyId;
            if (req.UnitId.HasValue) appt.UnitId = req.UnitId;
            if (req.LeaseId.HasValue) appt.LeaseId = req.LeaseId;
            if (req.TenantId.HasValue) appt.TenantId = req.TenantId;
            if (req.Title is not null) appt.Title = req.Title;
            if (req.ProspectName is not null) appt.ProspectName = req.ProspectName;
            if (req.ProspectEmail is not null) appt.ProspectEmail = req.ProspectEmail;
            if (req.Type.HasValue) appt.Type = req.Type.Value;
            if (req.Status.HasValue) appt.Status = req.Status.Value;
            if (req.ScheduledStart.HasValue) appt.ScheduledStart = req.ScheduledStart.Value;
            if (req.ScheduledEnd.HasValue) appt.ScheduledEnd = req.ScheduledEnd;
            if (req.AssignedTo is not null) appt.AssignedTo = req.AssignedTo;
            if (req.Notes is not null) appt.Notes = req.Notes;
            appt.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("appointment:updated", new { appt.Id, appt.PortfolioId });
            return Results.Ok(appt);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var appt = await db.Appointments.FindAsync(id);
            if (appt is null) return Results.NotFound();
            db.Appointments.Remove(appt);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateAppointmentRequest(
    int PortfolioId,
    string Title,
    AppointmentType Type,
    DateTime ScheduledStart,
    int? PropertyId = null,
    int? UnitId = null,
    int? LeaseId = null,
    int? TenantId = null,
    AppointmentStatus? Status = null,
    DateTime? ScheduledEnd = null,
    string? ProspectName = null,
    string? ProspectEmail = null,
    string? AssignedTo = null,
    string? Notes = null);

public record UpdateAppointmentRequest(
    int? PropertyId = null,
    int? UnitId = null,
    int? LeaseId = null,
    int? TenantId = null,
    string? Title = null,
    string? ProspectName = null,
    string? ProspectEmail = null,
    AppointmentType? Type = null,
    AppointmentStatus? Status = null,
    DateTime? ScheduledStart = null,
    DateTime? ScheduledEnd = null,
    string? AssignedTo = null,
    string? Notes = null);
