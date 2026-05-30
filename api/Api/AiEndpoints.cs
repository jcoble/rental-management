using RentalCommand.Data;
using RentalCommand.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Api;

public static class AiEndpoints
{
    public static WebApplication MapAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai");

        group.MapPost("/intake", async (AiIntakeRequest req, RentalCommandDbContext db) =>
        {
            var portfolioExists = await db.Portfolios.AnyAsync(p => p.Id == req.PortfolioId);
            if (!portfolioExists) return Results.BadRequest(new { error = "Portfolio not found" });

            var normalized = req.Message.ToLowerInvariant();
            var suggestions = new List<object>();
            var actions = new List<string>();

            if (normalized.Contains("leak") || normalized.Contains("broken") || normalized.Contains("repair") || normalized.Contains("hvac"))
            {
                suggestions.Add(new
                {
                    Entity = "WorkOrder",
                    Title = "Emergency Maintenance Intake",
                    Defaults = new { Priority = WorkOrderPriority.Emergency.ToString(), Status = WorkOrderStatus.New.ToString(), Category = "Maintenance" }
                });
                actions.Add("Open a work order and assign a preferred vendor immediately.");
            }

            if (normalized.Contains("late") || normalized.Contains("past due") || normalized.Contains("didn't pay") || normalized.Contains("rent due"))
            {
                suggestions.Add(new
                {
                    Entity = "Payment",
                    Title = "Late Rent Follow-up",
                    Defaults = new { Type = PaymentType.Rent.ToString(), Status = PaymentStatus.Late.ToString() }
                });
                actions.Add("Create/mark the rent charge as Late and send a late-rent notice.");
            }

            if (normalized.Contains("showing") || normalized.Contains("tour") || normalized.Contains("prospect"))
            {
                suggestions.Add(new
                {
                    Entity = "Appointment",
                    Title = "Prospect Showing",
                    Defaults = new { Type = AppointmentType.Showing.ToString(), Status = AppointmentStatus.Scheduled.ToString() }
                });
                actions.Add("Schedule a showing appointment and attach prospect contact details.");
            }

            if (normalized.Contains("move in") || normalized.Contains("move-in") || normalized.Contains("move out") || normalized.Contains("move-out"))
            {
                suggestions.Add(new
                {
                    Entity = "Inspection",
                    Title = "Move Inspection",
                    Defaults = new { Type = InspectionType.MoveIn.ToString(), Status = InspectionStatus.Scheduled.ToString() }
                });
                actions.Add("Schedule move-in/move-out inspection with photo checklist.");
            }

            if (suggestions.Count == 0)
            {
                suggestions.Add(new
                {
                    Entity = "General",
                    Title = "Operational Review",
                    Defaults = new { }
                });
                actions.Add("Review the note and log a structured action item (work order, payment, or appointment).");
            }

            return Results.Ok(new
            {
                Summary = "AI intake parsed the note and prepared structured actions.",
                SuggestedRecords = suggestions,
                RecommendedActions = actions
            });
        });

        group.MapGet("/portfolio-summary/{portfolioId:int}", async (int portfolioId, RentalCommandDbContext db) =>
        {
            var now = DateTime.UtcNow;

            var leases = await db.Leases.Where(l => l.PortfolioId == portfolioId).ToListAsync();
            var payments = await db.Payments.Where(p => p.PortfolioId == portfolioId).ToListAsync();
            var workOrders = await db.WorkOrders.Where(w => w.PortfolioId == portfolioId).ToListAsync();
            var appointments = await db.Appointments.Where(a => a.PortfolioId == portfolioId).ToListAsync();

            var risks = new List<string>();

            var expiring = leases.Count(l => l.Status == LeaseStatus.Active && l.EndDate <= now.AddDays(60));
            if (expiring > 0)
                risks.Add($"{expiring} active lease(s) expire within 60 days.");

            var overdue = payments.Where(p => p.Status != PaymentStatus.Paid && p.DueDate < now).Sum(p => p.Amount);
            if (overdue > 0)
                risks.Add($"Overdue receivables total {overdue:C}.");

            var emergency = workOrders.Count(w => w.Status != WorkOrderStatus.Completed && w.Priority == WorkOrderPriority.Emergency);
            if (emergency > 0)
                risks.Add($"{emergency} emergency work order(s) still open.");

            var unscheduledShowings = appointments.Count(a => a.Type == AppointmentType.Showing && a.Status == AppointmentStatus.Cancelled);
            if (unscheduledShowings > 0)
                risks.Add($"{unscheduledShowings} showing appointment(s) were cancelled and may need rebooking.");

            var opportunities = new List<string>
            {
                "Automate recurring rent charges for all active leases.",
                "Use owner statements to summarize NOI by property monthly.",
                "Track vendor W-9 and 1099 eligibility before year-end export."
            };

            return Results.Ok(new
            {
                PortfolioId = portfolioId,
                GeneratedAt = now,
                Risks = risks,
                Opportunities = opportunities,
                NextBestActions = new[]
                {
                    "Review open emergency work orders first.",
                    "Send renewal outreach for leases expiring in the next 60 days.",
                    "Follow up on overdue balances with payment-plan options."
                }
            });
        });

        group.MapPost("/generate-notice", (NoticeGenerationRequest req) =>
        {
            var subject = req.NoticeType switch
            {
                "LateRent" => "Late Rent Notice",
                "Renewal" => "Lease Renewal Offer",
                "Inspection" => "Upcoming Inspection Notice",
                _ => "Resident Notice"
            };

            var body = req.NoticeType switch
            {
                "LateRent" => $"Hello {req.RecipientName},\n\nOur records show an outstanding balance of {req.Amount:C}. Please submit payment by {req.DueDate:MMMM d, yyyy}. If you need a payment plan, contact us immediately.\n\nThank you,\n{req.SenderName}",
                "Renewal" => $"Hello {req.RecipientName},\n\nYour lease is scheduled to end on {req.DueDate:MMMM d, yyyy}. We would like to offer a renewal and can discuss updated terms. Please reply within 7 days.\n\nThank you,\n{req.SenderName}",
                "Inspection" => $"Hello {req.RecipientName},\n\nThis is a reminder of your upcoming inspection scheduled for {req.DueDate:MMMM d, yyyy}. Please ensure access and secure pets prior to arrival.\n\nThank you,\n{req.SenderName}",
                _ => $"Hello {req.RecipientName},\n\n{req.Context}\n\nThank you,\n{req.SenderName}"
            };

            return Results.Ok(new { Subject = subject, Body = body });
        });

        return app;
    }
}

public record AiIntakeRequest(int PortfolioId, string Message);
public record NoticeGenerationRequest(string NoticeType, string RecipientName, string SenderName, DateTime DueDate, decimal Amount = 0, string Context = "");
