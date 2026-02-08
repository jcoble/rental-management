using Lifecycle.Api.Auth;
using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class PortalEndpoints
{
    public static WebApplication MapPortalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/portal");

        group.MapGet("/overview", async (HttpContext context, LifecycleDbContext db) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();

            var portfolioId = user.PortfolioId;
            var now = DateTime.UtcNow;

            if (user.Role == UserRole.Tenant)
            {
                if (!user.TenantId.HasValue) return Results.BadRequest(new { error = "Tenant account is not linked" });

                var leases = await db.Leases
                    .Where(l => l.TenantId == user.TenantId.Value)
                    .Include(l => l.Property)
                    .Include(l => l.Unit)
                    .OrderByDescending(l => l.EndDate)
                    .ToListAsync();

                var leaseIds = leases.Select(l => l.Id).ToList();
                var payments = await db.Payments
                    .Where(p => leaseIds.Contains(p.LeaseId))
                    .OrderByDescending(p => p.DueDate)
                    .Take(30)
                    .ToListAsync();

                var workOrders = await db.WorkOrders
                    .Where(w => w.TenantId == user.TenantId.Value)
                    .OrderByDescending(w => w.RequestedAt)
                    .Take(20)
                    .ToListAsync();

                var appointments = await db.Appointments
                    .Where(a => a.TenantId == user.TenantId.Value && a.ScheduledStart >= now.AddDays(-30))
                    .OrderBy(a => a.ScheduledStart)
                    .Take(20)
                    .ToListAsync();

                return Results.Ok(new
                {
                    Role = user.Role.ToString(),
                    User = new { user.DisplayName, user.Email },
                    Leases = leases.Select(l => new
                    {
                        l.Id,
                        l.LeaseNumber,
                        Status = l.Status.ToString(),
                        l.StartDate,
                        l.EndDate,
                        l.MonthlyRent,
                        Property = l.Property?.Name,
                        Unit = l.Unit?.UnitNumber
                    }),
                    Ledger = new
                    {
                        Outstanding = payments.Where(p => p.Status != PaymentStatus.Paid).Sum(p => p.Amount),
                        LastPayments = payments.Take(8).Select(p => new
                        {
                            p.Id,
                            Type = p.PaymentType.ToString(),
                            Status = p.Status.ToString(),
                            p.Amount,
                            p.DueDate,
                            p.PaidDate
                        })
                    },
                    WorkOrders = workOrders.Select(w => new
                    {
                        w.Id,
                        w.Title,
                        w.Category,
                        Priority = w.Priority.ToString(),
                        Status = w.Status.ToString(),
                        w.RequestedAt,
                        w.ScheduledFor,
                        w.CompletedAt
                    }),
                    Appointments = appointments.Select(a => new
                    {
                        a.Id,
                        a.Title,
                        Type = a.Type.ToString(),
                        Status = a.Status.ToString(),
                        a.ScheduledStart
                    })
                });
            }

            if (user.Role == UserRole.Owner)
            {
                if (!user.OwnerId.HasValue) return Results.BadRequest(new { error = "Owner account is not linked" });

                var properties = await db.Properties
                    .Where(p => p.OwnerId == user.OwnerId.Value)
                    .Include(p => p.Units)
                    .ToListAsync();

                var propertyIds = properties.Select(p => p.Id).ToList();
                var leases = await db.Leases
                    .Where(l => propertyIds.Contains(l.PropertyId) && l.Status == LeaseStatus.Active)
                    .ToListAsync();

                var payments = await db.Payments
                    .Where(p => p.PortfolioId == portfolioId)
                    .Join(db.Leases, p => p.LeaseId, l => l.Id, (p, l) => new { p, l })
                    .Where(x => propertyIds.Contains(x.l.PropertyId))
                    .Select(x => x.p)
                    .ToListAsync();

                var expenses = await db.Expenses
                    .Where(e => e.PortfolioId == portfolioId && e.PropertyId.HasValue && propertyIds.Contains(e.PropertyId.Value))
                    .ToListAsync();

                var openWorkOrders = await db.WorkOrders
                    .Where(w => propertyIds.Contains(w.PropertyId) && w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Cancelled)
                    .OrderByDescending(w => w.RequestedAt)
                    .Take(20)
                    .ToListAsync();

                return Results.Ok(new
                {
                    Role = user.Role.ToString(),
                    User = new { user.DisplayName, user.Email },
                    PortfolioSummary = new
                    {
                        PropertyCount = properties.Count,
                        UnitCount = properties.SelectMany(p => p.Units).Count(),
                        OccupiedUnits = properties.SelectMany(p => p.Units).Count(u => u.Status == UnitStatus.Occupied),
                        ActiveLeases = leases.Count,
                        Collected = payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount),
                        Expenses = expenses.Sum(e => e.Amount),
                        Net = payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount) - expenses.Sum(e => e.Amount)
                    },
                    Properties = properties.Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.AddressLine1,
                        p.City,
                        p.State,
                        UnitCount = p.Units.Count,
                        OccupiedUnits = p.Units.Count(u => u.Status == UnitStatus.Occupied)
                    }),
                    OpenWorkOrders = openWorkOrders.Select(w => new
                    {
                        w.Id,
                        w.Title,
                        Priority = w.Priority.ToString(),
                        Status = w.Status.ToString(),
                        w.RequestedAt
                    })
                });
            }

            if (user.Role == UserRole.Agent)
            {
                var assignedAppointments = await db.Appointments
                    .Where(a => a.PortfolioId == portfolioId && a.AssignedTo != null && a.AssignedTo.Contains(user.DisplayName))
                    .OrderBy(a => a.ScheduledStart)
                    .Take(20)
                    .ToListAsync();

                var openWorkOrders = await db.WorkOrders
                    .Where(w => w.PortfolioId == portfolioId && w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Cancelled)
                    .OrderByDescending(w => w.Priority)
                    .ThenBy(w => w.RequestedAt)
                    .Take(30)
                    .ToListAsync();

                return Results.Ok(new
                {
                    Role = user.Role.ToString(),
                    User = new { user.DisplayName, user.Email },
                    Assignments = new
                    {
                        Appointments = assignedAppointments.Select(a => new
                        {
                            a.Id,
                            a.Title,
                            Type = a.Type.ToString(),
                            Status = a.Status.ToString(),
                            a.ScheduledStart
                        }),
                        WorkOrders = openWorkOrders.Select(w => new
                        {
                            w.Id,
                            w.Title,
                            Priority = w.Priority.ToString(),
                            Status = w.Status.ToString(),
                            w.RequestedAt
                        })
                    }
                });
            }

            var openWorkOrdersCount = await db.WorkOrders
                .CountAsync(w => w.PortfolioId == portfolioId && w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Cancelled);

            var overdueBalance = await db.Payments
                .Where(p => p.PortfolioId == portfolioId && p.Status != PaymentStatus.Paid && p.DueDate < now)
                .SumAsync(p => p.Amount);

            var pendingMessages = await db.PortalMessages
                .CountAsync(m => m.PortfolioId == portfolioId && m.Status != PortalMessageStatus.Resolved && m.Status != PortalMessageStatus.Closed);

            return Results.Ok(new
            {
                Role = user.Role.ToString(),
                User = new { user.DisplayName, user.Email },
                Operations = new
                {
                    OpenWorkOrders = openWorkOrdersCount,
                    OverdueBalance = overdueBalance,
                    PendingPortalMessages = pendingMessages
                }
            });
        });

        group.MapGet("/messages", async (HttpContext context, LifecycleDbContext db) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();

            var query = db.PortalMessages
                .Where(m => m.PortfolioId == user.PortfolioId)
                .Include(m => m.UserAccount)
                .OrderByDescending(m => m.CreatedAt)
                .AsQueryable();

            if (user.Role is UserRole.Owner or UserRole.Tenant)
                query = query.Where(m => m.UserAccountId == user.Id);

            var items = await query.Take(100).ToListAsync();

            return Results.Ok(items.Select(m => new
            {
                m.Id,
                m.PortfolioId,
                m.UserAccountId,
                Author = m.UserAccount?.DisplayName,
                AuthorRole = m.UserAccount?.Role.ToString(),
                m.PropertyId,
                m.UnitId,
                m.Subject,
                m.Body,
                Status = m.Status.ToString(),
                m.Reply,
                m.CreatedAt,
                m.UpdatedAt
            }));
        });

        group.MapPost("/messages", async (HttpContext context, CreatePortalMessageRequest req, LifecycleDbContext db) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var message = new PortalMessage
            {
                PortfolioId = user.PortfolioId,
                UserAccountId = user.Id,
                PropertyId = req.PropertyId,
                UnitId = req.UnitId,
                Subject = req.Subject,
                Body = req.Body,
                Status = PortalMessageStatus.Open,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.PortalMessages.Add(message);
            await db.SaveChangesAsync();

            return Results.Created($"/api/portal/messages/{message.Id}", new
            {
                message.Id,
                message.Subject,
                message.Body,
                Status = message.Status.ToString(),
                message.CreatedAt
            });
        });

        group.MapPatch("/messages/{id:int}", async (HttpContext context, int id, UpdatePortalMessageRequest req, LifecycleDbContext db) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();
            if (!AuthUtility.IsAnyRole(user, UserRole.Admin, UserRole.Manager, UserRole.Agent)) return Results.Forbid();

            var message = await db.PortalMessages.FirstOrDefaultAsync(m => m.Id == id && m.PortfolioId == user.PortfolioId);
            if (message is null) return Results.NotFound();

            if (req.Status.HasValue) message.Status = req.Status.Value;
            if (req.Reply is not null) message.Reply = req.Reply;
            message.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                message.Id,
                Status = message.Status.ToString(),
                message.Reply,
                message.UpdatedAt
            });
        });

        group.MapPost("/tenant/work-orders", async (HttpContext context, CreateTenantWorkOrderRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();
            if (user.Role != UserRole.Tenant || !user.TenantId.HasValue) return Results.Forbid();

            var lease = await db.Leases
                .Where(l => l.TenantId == user.TenantId.Value && l.Status == LeaseStatus.Active)
                .OrderByDescending(l => l.StartDate)
                .FirstOrDefaultAsync();

            if (lease is null)
                return Results.BadRequest(new { error = "No active lease found for tenant." });

            var now = DateTime.UtcNow;
            var workOrder = new WorkOrder
            {
                PortfolioId = user.PortfolioId,
                PropertyId = lease.PropertyId,
                UnitId = lease.UnitId,
                TenantId = user.TenantId,
                LeaseId = lease.Id,
                Title = req.Title,
                Description = req.Description,
                Category = string.IsNullOrWhiteSpace(req.Category) ? "Resident Request" : req.Category,
                Priority = req.Priority,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                CreatedBy = user.DisplayName,
                UpdatedAt = now
            };

            db.WorkOrders.Add(workOrder);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                user.PortfolioId,
                RentalActivityType.WorkOrderCreated,
                "WorkOrder",
                workOrder.Id,
                "Created",
                $"Portal work order '{workOrder.Title}' submitted by {user.DisplayName}",
                user.DisplayName);

            await sse.BroadcastAsync("work-order:created", new { workOrder.Id, workOrder.PortfolioId });

            return Results.Created($"/api/work-orders/{workOrder.Id}", new
            {
                workOrder.Id,
                workOrder.Title,
                workOrder.Description,
                Priority = workOrder.Priority.ToString(),
                Status = workOrder.Status.ToString(),
                workOrder.RequestedAt
            });
        });

        return app;
    }
}

public record CreatePortalMessageRequest(string Subject, string Body, int? PropertyId = null, int? UnitId = null);
public record UpdatePortalMessageRequest(PortalMessageStatus? Status = null, string? Reply = null);
public record CreateTenantWorkOrderRequest(string Title, string Description, string? Category = null, WorkOrderPriority Priority = WorkOrderPriority.Normal);
