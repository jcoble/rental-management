using RentalCommand.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Api;

public static class AccountingEndpoints
{
    public static WebApplication MapAccountingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/expenses");

        group.MapGet("/", async (int portfolioId, string? status, RentalCommandDbContext db) =>
        {
            var query = db.Expenses
                .Where(e => e.PortfolioId == portfolioId)
                .Include(e => e.Property)
                .Include(e => e.Vendor)
                .Include(e => e.WorkOrder)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ExpenseStatus>(status, out var parsedStatus))
                query = query.Where(e => e.Status == parsedStatus);

            var items = await query.OrderByDescending(e => e.IncurredAt).ToListAsync();

            return Results.Ok(items.Select(e => new
            {
                e.Id,
                e.PortfolioId,
                e.PropertyId,
                e.VendorId,
                e.WorkOrderId,
                e.Category,
                e.Description,
                Status = e.Status.ToString(),
                e.Amount,
                e.IncurredAt,
                e.DueDate,
                e.PaidAt,
                e.BillableToOwner,
                e.Notes,
                PropertyName = e.Property?.Name,
                VendorName = e.Vendor?.Name,
                WorkOrderTitle = e.WorkOrder?.Title,
                e.CreatedAt,
                e.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateExpenseRequest req, RentalCommandDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var expense = new Expense
            {
                PortfolioId = req.PortfolioId,
                PropertyId = req.PropertyId,
                VendorId = req.VendorId,
                WorkOrderId = req.WorkOrderId,
                Category = req.Category,
                Description = req.Description,
                Status = req.Status ?? ExpenseStatus.Pending,
                Amount = req.Amount,
                IncurredAt = req.IncurredAt,
                DueDate = req.DueDate,
                PaidAt = req.PaidAt,
                BillableToOwner = req.BillableToOwner,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Expenses.Add(expense);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(
                db,
                req.PortfolioId,
                RentalActivityType.ExpenseRecorded,
                "Expense",
                expense.Id,
                "Created",
                $"Expense '{expense.Category}' recorded for {expense.Amount:C}",
                "manual");

            await sse.BroadcastAsync("expense:created", new { expense.Id, expense.PortfolioId });
            return Results.Created($"/api/expenses/{expense.Id}", expense);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateExpenseRequest req, RentalCommandDbContext db, SseService sse) =>
        {
            var expense = await db.Expenses.FindAsync(id);
            if (expense is null) return Results.NotFound();

            if (req.PropertyId.HasValue) expense.PropertyId = req.PropertyId;
            if (req.VendorId.HasValue) expense.VendorId = req.VendorId;
            if (req.WorkOrderId.HasValue) expense.WorkOrderId = req.WorkOrderId;
            if (req.Category is not null) expense.Category = req.Category;
            if (req.Description is not null) expense.Description = req.Description;
            if (req.Status.HasValue) expense.Status = req.Status.Value;
            if (req.Amount.HasValue) expense.Amount = req.Amount.Value;
            if (req.IncurredAt.HasValue) expense.IncurredAt = req.IncurredAt.Value;
            if (req.DueDate.HasValue) expense.DueDate = req.DueDate;
            if (req.PaidAt.HasValue) expense.PaidAt = req.PaidAt;
            if (req.BillableToOwner.HasValue) expense.BillableToOwner = req.BillableToOwner.Value;
            if (req.Notes is not null) expense.Notes = req.Notes;
            expense.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("expense:updated", new { expense.Id, expense.PortfolioId });

            return Results.Ok(expense);
        });

        group.MapGet("/summary", async (int portfolioId, RentalCommandDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var items = await db.Expenses.Where(e => e.PortfolioId == portfolioId).ToListAsync();

            return Results.Ok(new
            {
                Total = items.Sum(e => e.Amount),
                Paid = items.Where(e => e.Status == ExpenseStatus.Paid).Sum(e => e.Amount),
                Pending = items.Where(e => e.Status != ExpenseStatus.Paid).Sum(e => e.Amount),
                ThisMonth = items.Where(e => e.IncurredAt.Month == now.Month && e.IncurredAt.Year == now.Year).Sum(e => e.Amount),
                ByCategory = items.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount))
            });
        });

        return app;
    }
}

public record CreateExpenseRequest(
    int PortfolioId,
    string Category,
    string Description,
    decimal Amount,
    DateTime IncurredAt,
    int? PropertyId = null,
    int? VendorId = null,
    int? WorkOrderId = null,
    ExpenseStatus? Status = null,
    DateTime? DueDate = null,
    DateTime? PaidAt = null,
    bool BillableToOwner = false,
    string? Notes = null);

public record UpdateExpenseRequest(
    int? PropertyId = null,
    int? VendorId = null,
    int? WorkOrderId = null,
    string? Category = null,
    string? Description = null,
    ExpenseStatus? Status = null,
    decimal? Amount = null,
    DateTime? IncurredAt = null,
    DateTime? DueDate = null,
    DateTime? PaidAt = null,
    bool? BillableToOwner = null,
    string? Notes = null);
