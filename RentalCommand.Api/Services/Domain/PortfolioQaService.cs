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

        new LlmToolSpec(
            "list_tenants",
            "Returns all tenants in the portfolio: first and last name, email, phone, " +
            "their current unit number and property name (from their active lease, if any), " +
            "and whether they currently hold an active lease. " +
            "Use for 'who are my tenants?', 'what is tenant X's phone number?'.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_properties",
            "Returns each property in the portfolio: name, city, state, total unit count, " +
            "occupied unit count, and vacant unit count. " +
            "Use for 'how many units do I have?', 'what is my occupancy?'.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_recent_expenses",
            "Returns expenses with IncurredAt within the last N days, most recent first: " +
            "date, description, vendor name (if linked), Schedule-E category, amount, " +
            "property name (if linked), and status. Also returns a total amount. " +
            "Use for 'what did I spend on repairs?', 'recent expenses'.",
            """{"type":"object","properties":{"withinDays":{"type":"integer","description":"Number of days to look back. Defaults to 90."}},"required":[]}"""),

        new LlmToolSpec(
            "list_recent_payments",
            "Returns payments with PaidDate within the last N days, most recent first: " +
            "tenant name, unit number, amount, paid date, payment type, and status. " +
            "Use for 'recent rent payments', 'did unit 4B pay this month?'.",
            """{"type":"object","properties":{"withinDays":{"type":"integer","description":"Number of days to look back. Defaults to 30."}},"required":[]}"""),

        new LlmToolSpec(
            "list_upcoming_events",
            "Returns Appointments and Inspections scheduled within the next N days, " +
            "soonest first, merged into one list. Each item includes a kind field " +
            "('Appointment' or 'Inspection'), title or type, date/time, and property " +
            "or unit name. Use for 'what is on my schedule?', 'any inspections coming up?'.",
            """{"type":"object","properties":{"withinDays":{"type":"integer","description":"Number of days to look ahead. Defaults to 14."}},"required":[]}"""),

        new LlmToolSpec(
            "list_vendors",
            "Returns all vendors in the portfolio: name, service type, phone, and email. " +
            "Use for 'who is my plumber?', 'vendor contact info'.",
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
        // Only the last few turns are kept, and roles are allowlisted to user/assistant — a
        // caller must never be able to inject a "system" turn (which would override our prompt)
        // or run up unbounded token cost via a huge history.
        const int maxHistoryTurns = 20;
        var messages = new List<LlmChatMessage>();
        if (history is { Count: > 0 })
        {
            foreach (var turn in history.TakeLast(maxHistoryTurns))
            {
                if (turn.Role is not ("user" or "assistant")) continue;
                var content = turn.Content?.Length > 4000 ? turn.Content[..4000] : turn.Content;
                messages.Add(new LlmChatMessage(Role: turn.Role, Content: content));
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
                "list_tenants"           => await ListTenantsAsync(portfolioId, ct),
                "list_properties"        => await ListPropertiesAsync(portfolioId, ct),
                "list_recent_expenses"   => await ListRecentExpensesAsync(call.ArgumentsJson, portfolioId, ct),
                "list_recent_payments"   => await ListRecentPaymentsAsync(call.ArgumentsJson, portfolioId, ct),
                "list_upcoming_events"   => await ListUpcomingEventsAsync(call.ArgumentsJson, portfolioId, ct),
                "list_vendors"           => await ListVendorsAsync(portfolioId, ct),
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

    private async Task<string> ListTenantsAsync(int portfolioId, CancellationToken ct)
    {
        const int maxRows = 50;

        // Load tenants with their active lease (if any) for unit + property info.
        var tenants = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId)
            .Select(t => new
            {
                name         = t.FirstName + " " + t.LastName,
                email        = t.Email,
                phone        = t.Phone,
                activeLease  = t.Leases
                    .Where(l => l.Status == LeaseStatus.Active)
                    .Select(l => new
                    {
                        unitNumber   = l.Unit != null ? l.Unit.UnitNumber : "(unknown)",
                        propertyName = l.Property != null ? l.Property.Name : "(unknown)",
                    })
                    .FirstOrDefault(),
            })
            .OrderBy(t => t.name)
            .Take(maxRows + 1)
            .ToListAsync(ct);

        var truncated = tenants.Count > maxRows;
        var rows = tenants.Take(maxRows).Select(t => new
        {
            t.name,
            t.email,
            t.phone,
            unitNumber   = t.activeLease != null ? t.activeLease.unitNumber : null as string,
            propertyName = t.activeLease != null ? t.activeLease.propertyName : null as string,
            hasActiveLease = t.activeLease != null,
        }).ToList();

        var result = new { count = rows.Count, truncated, tenants = rows };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListPropertiesAsync(int portfolioId, CancellationToken ct)
    {
        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new
            {
                name         = p.Name,
                city         = p.City,
                state        = p.State,
                totalUnits   = p.Units.Count,
                occupiedUnits = p.Units.Count(u => u.Status == UnitStatus.Occupied),
                vacantUnits  = p.Units.Count(u => u.Status == UnitStatus.Vacant),
            })
            .OrderBy(p => p.name)
            .ToListAsync(ct);

        var result = new
        {
            count          = properties.Count,
            totalUnits     = properties.Sum(p => p.totalUnits),
            totalOccupied  = properties.Sum(p => p.occupiedUnits),
            totalVacant    = properties.Sum(p => p.vacantUnits),
            properties,
        };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListRecentExpensesAsync(string argsJson, int portfolioId, CancellationToken ct)
    {
        // Parse optional withinDays (default 90).
        var withinDays = 90;
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("withinDays", out var v) &&
                    v.TryGetInt32(out var d) && d > 0)
                    withinDays = d;
            }
            catch { /* default: 90 */ }
        }

        const int maxRows = 50;
        var cutoff = DateTime.UtcNow.AddDays(-withinDays);

        // Order by the real DateTime column in SQL, then format strings in memory
        // (Npgsql can't translate DateTime.ToString(format) / enum.ToString()).
        var entities = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && e.IncurredAt >= cutoff)
            .Include(e => e.Property)
            .Include(e => e.Vendor)
            .OrderByDescending(e => e.IncurredAt)
            .Take(maxRows + 1)
            .ToListAsync(ct);

        var truncated = entities.Count > maxRows;
        var rows = entities.Take(maxRows).Select(e => new
        {
            date         = e.IncurredAt.ToString("yyyy-MM-dd"),
            description  = e.Description,
            vendor       = e.Vendor?.Name,
            category     = e.Category.ToString(),
            amount       = e.Amount,
            propertyName = e.Property?.Name,
            status       = e.Status.ToString(),
        }).ToList();
        var result = new
        {
            withinDays,
            count     = rows.Count,
            truncated,
            total     = rows.Sum(e => e.amount),
            expenses  = rows,
        };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListRecentPaymentsAsync(string argsJson, int portfolioId, CancellationToken ct)
    {
        // Parse optional withinDays (default 30).
        var withinDays = 30;
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("withinDays", out var v) &&
                    v.TryGetInt32(out var d) && d > 0)
                    withinDays = d;
            }
            catch { /* default: 30 */ }
        }

        const int maxRows = 50;
        var cutoff = DateTime.UtcNow.AddDays(-withinDays);

        // Order by the real DateTime column in SQL, then format in memory
        // (Npgsql can't translate DateTime.ToString(format) / enum.ToString()).
        var entities = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaidDate.HasValue &&
                p.PaidDate.Value >= cutoff)
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Tenant)
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Unit)
            .OrderByDescending(p => p.PaidDate)
            .Take(maxRows + 1)
            .ToListAsync(ct);

        var truncated = entities.Count > maxRows;
        var rows = entities.Take(maxRows).Select(p => new
        {
            tenantName   = p.Lease?.Tenant != null
                               ? p.Lease.Tenant.FirstName + " " + p.Lease.Tenant.LastName
                               : "(unknown)",
            unitNumber   = p.Lease?.Unit != null ? p.Lease.Unit.UnitNumber : "(unknown)",
            amount       = p.Amount,
            paidDate     = p.PaidDate?.ToString("yyyy-MM-dd"),
            paymentType  = p.PaymentType.ToString(),
            status       = p.Status.ToString(),
        }).ToList();
        var result = new { withinDays, count = rows.Count, truncated, total = rows.Sum(p => p.amount), payments = rows };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListUpcomingEventsAsync(string argsJson, int portfolioId, CancellationToken ct)
    {
        // Parse optional withinDays (default 14).
        var withinDays = 14;
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("withinDays", out var v) &&
                    v.TryGetInt32(out var d) && d > 0)
                    withinDays = d;
            }
            catch { /* default: 14 */ }
        }

        var now    = DateTime.UtcNow;
        var cutoff = now.AddDays(withinDays);

        // Appointments within window (not Cancelled/NoShow).
        var appointments = await _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                a.ScheduledStart >= now &&
                a.ScheduledStart <= cutoff &&
                a.Status != AppointmentStatus.Cancelled &&
                a.Status != AppointmentStatus.NoShow)
            .Include(a => a.Property)
            .Include(a => a.Unit)
            .Select(a => new
            {
                kind         = "Appointment",
                title        = a.Title,
                type         = a.Type.ToString(),
                scheduledAt  = a.ScheduledStart.ToString("yyyy-MM-dd HH:mm"),
                propertyName = a.Property != null ? a.Property.Name : null as string,
                unitNumber   = a.Unit != null ? a.Unit.UnitNumber : null as string,
                status       = a.Status.ToString(),
                sortKey      = a.ScheduledStart,
            })
            .ToListAsync(ct);

        // Inspections within window (not Cancelled/Archived).
        var inspections = await _db.Inspections
            .AsNoTracking()
            .Where(i =>
                i.PortfolioId == portfolioId &&
                i.ScheduledFor >= now &&
                i.ScheduledFor <= cutoff &&
                i.Status != InspectionStatus.Cancelled &&
                i.Status != InspectionStatus.Archived)
            .Include(i => i.Property)
            .Include(i => i.Unit)
            .Select(i => new
            {
                kind         = "Inspection",
                title        = i.Type.ToString() + " Inspection",
                type         = i.Type.ToString(),
                scheduledAt  = i.ScheduledFor.ToString("yyyy-MM-dd HH:mm"),
                propertyName = i.Property != null ? i.Property.Name : null as string,
                unitNumber   = i.Unit != null ? i.Unit.UnitNumber : null as string,
                status       = i.Status.ToString(),
                sortKey      = i.ScheduledFor,
            })
            .ToListAsync(ct);

        var events = appointments
            .Select(a => new
            {
                a.kind, a.title, a.type, a.scheduledAt,
                a.propertyName, a.unitNumber, a.status, a.sortKey,
            })
            .Concat(inspections.Select(i => new
            {
                i.kind, i.title, i.type, i.scheduledAt,
                i.propertyName, i.unitNumber, i.status, i.sortKey,
            }))
            .OrderBy(e => e.sortKey)
            .Take(50)
            .Select(e => new
            {
                e.kind, e.title, e.type, e.scheduledAt,
                e.propertyName, e.unitNumber, e.status,
            })
            .ToList();

        var result = new { withinDays, count = events.Count, events };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListVendorsAsync(int portfolioId, CancellationToken ct)
    {
        const int maxRows = 50;

        var vendors = await _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId)
            .Select(v => new
            {
                name        = v.Name,
                serviceType = v.ServiceType,
                phone       = v.Phone,
                email       = v.Email,
                preferred   = v.Preferred,
            })
            .OrderBy(v => v.serviceType)
            .ThenBy(v => v.name)
            .Take(maxRows + 1)
            .ToListAsync(ct);

        var truncated = vendors.Count > maxRows;
        var rows = vendors.Take(maxRows).ToList();
        var result = new { count = rows.Count, truncated, vendors = rows };
        return JsonSerializer.Serialize(result, _json);
    }
}
