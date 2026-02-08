using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class PaymentEndpoints
{
    public static WebApplication MapPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/payments");

        group.MapGet("/", async (int portfolioId, string? status, LifecycleDbContext db) =>
        {
            var query = db.Payments
                .Where(p => p.PortfolioId == portfolioId)
                .Include(p => p.Lease)
                .ThenInclude(l => l!.Tenant)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PaymentStatus>(status, out var parsed))
            {
                query = query.Where(p => p.Status == parsed);
            }

            var payments = await query.OrderByDescending(p => p.DueDate).ToListAsync();

            return Results.Ok(payments.Select(p => new
            {
                p.Id,
                p.PortfolioId,
                p.LeaseId,
                Type = p.PaymentType.ToString(),
                Status = p.Status.ToString(),
                p.Amount,
                p.DueDate,
                p.PaidDate,
                p.Method,
                p.ExternalReference,
                p.Notes,
                TenantName = p.Lease?.Tenant is null ? null : $"{p.Lease.Tenant.FirstName} {p.Lease.Tenant.LastName}",
                LeaseNumber = p.Lease?.LeaseNumber,
                p.CreatedAt,
                p.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreatePaymentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var lease = await db.Leases.FirstOrDefaultAsync(l => l.Id == req.LeaseId && l.PortfolioId == req.PortfolioId);
            if (lease is null) return Results.BadRequest(new { error = "Lease not found" });

            var now = DateTime.UtcNow;
            var payment = new Payment
            {
                PortfolioId = req.PortfolioId,
                LeaseId = req.LeaseId,
                PaymentType = req.Type,
                Status = req.Status ?? PaymentStatus.Scheduled,
                Amount = req.Amount,
                DueDate = req.DueDate,
                PaidDate = req.PaidDate,
                Method = req.Method,
                ExternalReference = req.ExternalReference,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.PaymentRecorded,
                "Payment",
                payment.Id,
                "Created",
                $"Payment entry ({payment.PaymentType}) for {payment.Amount:C} created",
                "manual");

            await sse.BroadcastAsync("payment:created", new { payment.Id, payment.PortfolioId });
            return Results.Created($"/api/payments/{payment.Id}", payment);
        });

        group.MapPatch("/{id:int}", async (int id, UpdatePaymentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var payment = await db.Payments.FindAsync(id);
            if (payment is null) return Results.NotFound();

            if (req.Type.HasValue) payment.PaymentType = req.Type.Value;
            if (req.Status.HasValue) payment.Status = req.Status.Value;
            if (req.Amount.HasValue) payment.Amount = req.Amount.Value;
            if (req.DueDate.HasValue) payment.DueDate = req.DueDate.Value;
            if (req.PaidDate.HasValue) payment.PaidDate = req.PaidDate.Value;
            if (req.Method is not null) payment.Method = req.Method;
            if (req.ExternalReference is not null) payment.ExternalReference = req.ExternalReference;
            if (req.Notes is not null) payment.Notes = req.Notes;
            payment.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("payment:updated", new { payment.Id, payment.PortfolioId });
            return Results.Ok(payment);
        });

        group.MapPost("/{id:int}/mark-paid", async (int id, MarkPaymentPaidRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var payment = await db.Payments.FindAsync(id);
            if (payment is null) return Results.NotFound();

            payment.Status = PaymentStatus.Paid;
            payment.PaidDate = req.PaidDate ?? DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(req.Method)) payment.Method = req.Method;
            if (!string.IsNullOrWhiteSpace(req.ExternalReference)) payment.ExternalReference = req.ExternalReference;
            payment.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("payment:updated", new { payment.Id, payment.PortfolioId });
            return Results.Ok(payment);
        });

        group.MapGet("/summary", async (int portfolioId, LifecycleDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var payments = await db.Payments
                .Where(p => p.PortfolioId == portfolioId)
                .ToListAsync();

            return Results.Ok(new
            {
                TotalExpected = payments.Sum(p => p.Amount),
                TotalCollected = payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount),
                TotalOutstanding = payments.Where(p => p.Status != PaymentStatus.Paid).Sum(p => p.Amount),
                OverdueTotal = payments.Where(p => p.Status != PaymentStatus.Paid && p.DueDate < now).Sum(p => p.Amount),
                ByStatus = payments.GroupBy(p => p.Status.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount))
            });
        });

        return app;
    }
}

public record CreatePaymentRequest(
    int PortfolioId,
    int LeaseId,
    PaymentType Type,
    decimal Amount,
    DateTime DueDate,
    PaymentStatus? Status = null,
    DateTime? PaidDate = null,
    string? Method = null,
    string? ExternalReference = null,
    string? Notes = null);

public record UpdatePaymentRequest(
    PaymentType? Type = null,
    PaymentStatus? Status = null,
    decimal? Amount = null,
    DateTime? DueDate = null,
    DateTime? PaidDate = null,
    string? Method = null,
    string? ExternalReference = null,
    string? Notes = null);

public record MarkPaymentPaidRequest(DateTime? PaidDate = null, string? Method = null, string? ExternalReference = null);
