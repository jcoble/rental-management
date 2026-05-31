using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortfolioQaService"/>
public class PortfolioQaService : IPortfolioQaService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly IAccountingService _accounting;
    private readonly ILogger<PortfolioQaService> _logger;

    // Compact JSON serializer — no indentation to minimise tokens.
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // ---------------------------------------------------------------------------
    // Tool specs (read-only, portfolio-scoped — portfolioId is injected server-side)
    // ---------------------------------------------------------------------------

    private static readonly IReadOnlyList<LlmToolSpec> Tools =
    [
        new LlmToolSpec(
            "get_financial_summary",
            "Returns collected rent, outstanding rent, overdue rent (amount + count), " +
            "and total expenses broken down by IRS Schedule E category for the portfolio.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_overdue_rent",
            "Returns all payments that are overdue: status is Scheduled, Partial, or Late " +
            "and the due date is in the past. Each item includes the lease number, unit number, " +
            "tenant name, amount owed, due date, and how many days overdue.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_active_leases",
            "Returns all active leases in the portfolio with tenant name, unit number, " +
            "monthly rent, rent due day, lease start and end dates.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_work_orders",
            "Returns work orders for the portfolio. Pass openOnly=true to see only open " +
            "(non-completed, non-cancelled, non-archived) orders. Each item includes title, " +
            "status, priority, category, property name, and scheduled/completed dates.",
            """{"type":"object","properties":{"openOnly":{"type":"boolean","description":"When true, exclude Completed, Cancelled, and Archived work orders. Defaults to false."}},"required":[]}"""),

        new LlmToolSpec(
            "list_expiring_leases",
            "Returns active leases whose end date falls within the next N days. " +
            "Useful for 'any leases expiring soon?' questions.",
            """{"type":"object","properties":{"withinDays":{"type":"integer","description":"Number of days to look ahead. Defaults to 60."}},"required":[]}"""),

        new LlmToolSpec(
            "list_vacant_units",
            "Returns all units with Vacant status across the portfolio, including the " +
            "unit number, property name, and market rent.",
            """{"type":"object","properties":{},"required":[]}"""),
    ];

    // ---------------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------------

    public PortfolioQaService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        IAccountingService accounting,
        ILogger<PortfolioQaService> logger)
    {
        _db = db;
        _llm = llm;
        _accounting = accounting;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    public async Task<AskResponse> AskAsync(
        int portfolioId,
        string question,
        IReadOnlyList<QaTurn>? history,
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var systemPrompt = $"""
            You are a helpful rental-portfolio assistant for portfolio {portfolioId}.
            Answer in plain English suitable for a non-technical landlord who manages their
            properties from a phone. Be concise and direct — no jargon.

            Always use the provided tools to fetch real data before answering. Never invent
            dollar amounts, dates, names, or counts. If a tool returns an empty list, say so.

            Today's date is {today:yyyy-MM-dd}.
            """;

        // Build initial message list from history + current question.
        var messages = new List<LlmChatMessage>();
        if (history is { Count: > 0 })
        {
            foreach (var turn in history)
            {
                messages.Add(new LlmChatMessage(Role: turn.Role, Content: turn.Content));
            }
        }
        messages.Add(new LlmChatMessage(Role: "user", Content: question));

        var toolsUsed = new List<string>();
        var totalTokens = 0;
        var modelId = string.Empty;
        const int maxIterations = 5;

        for (var i = 0; i < maxIterations; i++)
        {
            LlmToolResult result;
            try
            {
                result = await _llm.ChatWithToolsAsync(systemPrompt, messages, Tools, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Portfolio Q&A failed for portfolio {PortfolioId}", portfolioId);
                return new AskResponse(
                    Answer: "The assistant is temporarily unavailable. Please try again in a moment.",
                    ToolsUsed: toolsUsed.Distinct().ToList(),
                    LlmAvailable: false,
                    TokensUsed: totalTokens,
                    ModelId: modelId);
            }

            // Track tokens and model across iterations.
            totalTokens += result.InputTokens + result.OutputTokens;
            if (!string.IsNullOrEmpty(result.ModelId))
                modelId = result.ModelId;

            // No-op path: LLM provider not configured.
            if (result.StopReason == "noop")
            {
                return new AskResponse(
                    Answer: result.Text ?? "AI is unavailable — no API key is configured.",
                    ToolsUsed: [],
                    LlmAvailable: false,
                    TokensUsed: 0,
                    ModelId: result.ModelId);
            }

            // Tool-call path: execute each tool and feed results back.
            if (result.ToolCalls is { Count: > 0 })
            {
                // Append the assistant message that carries the tool calls.
                messages.Add(new LlmChatMessage(
                    Role: "assistant",
                    Content: result.Text,
                    ToolCalls: result.ToolCalls));

                // Execute each tool and append its result.
                foreach (var call in result.ToolCalls)
                {
                    toolsUsed.Add(call.Name);
                    var toolResult = await ExecuteToolAsync(call, portfolioId, ct);
                    messages.Add(new LlmChatMessage(
                        Role: "tool",
                        Content: toolResult,
                        ToolCallId: call.Id));
                }

                continue; // next iteration
            }

            // Final answer path.
            var answer = result.Text ?? "(The assistant returned no text.)";
            return new AskResponse(
                Answer: answer,
                ToolsUsed: toolsUsed.Distinct().ToList(),
                LlmAvailable: true,
                TokensUsed: totalTokens,
                ModelId: modelId);
        }

        // Exceeded max iterations — return whatever text we have.
        var lastText = messages
            .LastOrDefault(m => m.Role == "assistant" && m.Content != null)
            ?.Content
            ?? "The assistant did not produce a final answer within the allowed number of steps.";

        return new AskResponse(
            Answer: lastText + " (Note: response may be incomplete.)",
            ToolsUsed: toolsUsed.Distinct().ToList(),
            LlmAvailable: true,
            TokensUsed: totalTokens,
            ModelId: modelId);
    }

    // ---------------------------------------------------------------------------
    // Tool dispatcher
    // ---------------------------------------------------------------------------

    private async Task<string> ExecuteToolAsync(
        LlmToolCall call,
        int portfolioId,
        CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "get_financial_summary"  => await GetFinancialSummaryAsync(portfolioId, ct),
                "list_overdue_rent"      => await ListOverdueRentAsync(portfolioId, ct),
                "list_active_leases"     => await ListActiveLeasesAsync(portfolioId, ct),
                "list_work_orders"       => await ListWorkOrdersAsync(call.ArgumentsJson, portfolioId, ct),
                "list_expiring_leases"   => await ListExpiringLeasesAsync(call.ArgumentsJson, portfolioId, ct),
                "list_vacant_units"      => await ListVacantUnitsAsync(portfolioId, ct),
                _                        => $"Error: unknown tool '{call.Name}'.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Q&A tool {Tool} failed for portfolio {PortfolioId}", call.Name, portfolioId);
            return $"Error: tool '{call.Name}' could not retrieve data right now. Tell the user this information is temporarily unavailable.";
        }
    }

    // ---------------------------------------------------------------------------
    // Tool implementations
    // ---------------------------------------------------------------------------

    private async Task<string> GetFinancialSummaryAsync(int portfolioId, CancellationToken ct)
    {
        var summary = await _accounting.GetSummaryAsync(portfolioId, ct);

        var result = new
        {
            collected = summary.Payments.Collected,
            outstanding = summary.Payments.Outstanding,
            overdue = summary.Payments.Overdue,
            overdueCount = summary.Payments.OverdueCount,
            totalExpenses = summary.TotalExpenses,
            expensesByCategory = summary.ExpensesByCategory
                .Select(e => new { category = e.CategoryName, total = e.Total, count = e.Count })
                .ToList(),
        };

        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListOverdueRentAsync(int portfolioId, CancellationToken ct)
    {
        var today = DateTime.UtcNow;

        var rows = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Partial ||
                 p.Status == PaymentStatus.Late) &&
                p.DueDate < today)
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Tenant)
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Unit)
            .Select(p => new
            {
                leaseNumber  = p.Lease != null ? p.Lease.LeaseNumber : $"lease-{p.LeaseId}",
                unitNumber   = p.Lease != null && p.Lease.Unit != null ? p.Lease.Unit.UnitNumber : "(unknown)",
                tenantName   = p.Lease != null && p.Lease.Tenant != null
                                   ? p.Lease.Tenant.FirstName + " " + p.Lease.Tenant.LastName
                                   : "(unknown)",
                amount       = p.Amount,
                dueDate      = p.DueDate.ToString("yyyy-MM-dd"),
                daysOverdue  = (int)(today - p.DueDate).TotalDays,
                status       = p.Status.ToString(),
            })
            .OrderByDescending(r => r.daysOverdue)
            .ToListAsync(ct);

        return rows.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListActiveLeasesAsync(int portfolioId, CancellationToken ct)
    {
        var rows = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Select(l => new
            {
                leaseNumber  = l.LeaseNumber,
                tenantName   = l.Tenant != null
                                   ? l.Tenant.FirstName + " " + l.Tenant.LastName
                                   : "(unknown)",
                unitNumber   = l.Unit != null ? l.Unit.UnitNumber : "(unknown)",
                monthlyRent  = l.MonthlyRent,
                rentDueDay   = l.RentDueDay,
                startDate    = l.StartDate.ToString("yyyy-MM-dd"),
                endDate      = l.EndDate.ToString("yyyy-MM-dd"),
            })
            .OrderBy(r => r.tenantName)
            .ToListAsync(ct);

        return rows.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListWorkOrdersAsync(string argsJson, int portfolioId, CancellationToken ct)
    {
        // Parse optional openOnly flag defensively.
        var openOnly = false;
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("openOnly", out var v))
                    openOnly = v.ValueKind == JsonValueKind.True;
            }
            catch { /* default: false */ }
        }

        var openStatuses = new[]
        {
            WorkOrderStatus.New,
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.InProgress,
            WorkOrderStatus.WaitingParts,
            WorkOrderStatus.OnHold,
        };

        var query = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId);

        if (openOnly)
            query = query.Where(w => openStatuses.Contains(w.Status));

        var rows = await query
            .Include(w => w.Property)
            .Select(w => new
            {
                title          = w.Title,
                status         = w.Status.ToString(),
                priority       = w.Priority.ToString(),
                category       = w.Category,
                propertyName   = w.Property != null ? w.Property.Name : "(unknown)",
                requestedAt    = w.RequestedAt.ToString("yyyy-MM-dd"),
                scheduledFor   = w.ScheduledFor.HasValue ? w.ScheduledFor.Value.ToString("yyyy-MM-dd") : null,
                completedAt    = w.CompletedAt.HasValue ? w.CompletedAt.Value.ToString("yyyy-MM-dd") : null,
            })
            .OrderByDescending(w => w.requestedAt)
            .ToListAsync(ct);

        return rows.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListExpiringLeasesAsync(string argsJson, int portfolioId, CancellationToken ct)
    {
        // Parse optional withinDays (default 60).
        var withinDays = 60;
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("withinDays", out var v) &&
                    v.TryGetInt32(out var d) && d > 0)
                    withinDays = d;
            }
            catch { /* default: 60 */ }
        }

        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(withinDays);

        var rows = await _db.Leases
            .AsNoTracking()
            .Where(l =>
                l.PortfolioId == portfolioId &&
                l.Status == LeaseStatus.Active &&
                l.EndDate >= today &&
                l.EndDate <= cutoff)
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Select(l => new
            {
                leaseNumber = l.LeaseNumber,
                tenantName  = l.Tenant != null
                                  ? l.Tenant.FirstName + " " + l.Tenant.LastName
                                  : "(unknown)",
                unitNumber  = l.Unit != null ? l.Unit.UnitNumber : "(unknown)",
                monthlyRent = l.MonthlyRent,
                endDate     = l.EndDate.ToString("yyyy-MM-dd"),
                daysLeft    = (int)(l.EndDate.Date - today).TotalDays,
            })
            .OrderBy(r => r.endDate)
            .ToListAsync(ct);

        return rows.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListVacantUnitsAsync(int portfolioId, CancellationToken ct)
    {
        var rows = await _db.Units
            .AsNoTracking()
            .Where(u => u.Status == UnitStatus.Vacant && u.Property != null && u.Property.PortfolioId == portfolioId)
            .Include(u => u.Property)
            .Select(u => new
            {
                unitNumber   = u.UnitNumber,
                propertyName = u.Property != null ? u.Property.Name : "(unknown)",
                marketRent   = u.MarketRent,
                bedrooms     = u.Bedrooms,
                bathrooms    = u.Bathrooms,
            })
            .OrderBy(u => u.propertyName)
            .ThenBy(u => u.unitNumber)
            .ToListAsync(ct);

        return rows.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rows, _json);
    }
}
