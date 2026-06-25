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
    private readonly IMessagePublisher _publisher;
    private readonly IKnowledgeBaseService _kb;
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
            "Returns current ledger totals plus month-to-date and trailing-30-day money snapshots. " +
            "Use the returned period labels; do not describe all-time/current-ledger totals as this month. " +
            "Unmatched bank deposits are included in collected cash and must be mentioned separately.",
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
        IMessagePublisher publisher,
        IKnowledgeBaseService kb,
        ILogger<PortfolioQaService> logger)
    {
        _db = db;
        _llm = llm;
        _accounting = accounting;
        _publisher = publisher;
        _kb = kb;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    public async Task<AskResponse> AskAsync(
        int portfolioId,
        string question,
        IReadOnlyList<QaTurn>? history,
        QaDeliveryOptions? delivery = null,
        CancellationToken ct = default)
    {
        // Route product/how-to questions ("how do I record a payment?", "what is a security
        // deposit?") to the knowledge-base path; everything else stays on the live-data tool path.
        // The heuristic is intentionally conservative — when in doubt, answer from data.
        if (LooksLikeHowTo(question))
        {
            var kbAnswer = await TryAnswerFromDocsAsync(portfolioId, question, delivery, ct);
            if (kbAnswer is not null) return kbAnswer;
            // No relevant docs matched — fall through to the data path so we still try to help.
        }

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
            var delivered = await DeliverAsync(portfolioId, question, answer, delivery, ct);
            return new AskResponse(
                Answer: answer,
                ToolsUsed: toolsUsed.Distinct().ToList(),
                LlmAvailable: true,
                TokensUsed: totalTokens,
                ModelId: modelId,
                DeliveredChannels: delivered);
        }

        // Exceeded max iterations — return whatever text we have.
        var lastText = messages
            .LastOrDefault(m => m.Role == "assistant" && m.Content != null)
            ?.Content
            ?? "The assistant did not produce a final answer within the allowed number of steps.";

        var incompleteAnswer = lastText + " (Note: response may be incomplete.)";
        var deliveredIncomplete = await DeliverAsync(portfolioId, question, incompleteAnswer, delivery, ct);
        return new AskResponse(
            Answer: incompleteAnswer,
            ToolsUsed: toolsUsed.Distinct().ToList(),
            LlmAvailable: true,
            TokensUsed: totalTokens,
            ModelId: modelId,
            DeliveredChannels: deliveredIncomplete);
    }

    // ---------------------------------------------------------------------------
    // How-to / product Q&A grounded in the knowledge base (docs)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Conservative heuristic: does this question look like a product/how-to question (answerable
    /// from the docs) rather than a question about the landlord's own live data? Data questions name
    /// or imply portfolio records ("who hasn't paid", "my overdue rent", "unit 4B"); how-to questions
    /// ask how the software works ("how do I record a payment", "what is a security deposit",
    /// "where do I add a tenant"). When ambiguous we return false and let the data path handle it.
    /// </summary>
    internal static bool LooksLikeHowTo(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var q = question.ToLowerInvariant();

        // Strong "about my data" signals — never treat these as how-to.
        // (Possessive "my"/"our" plus generic data verbs almost always means the live portfolio.)
        foreach (var dataSignal in DataSignals)
        {
            if (q.Contains(dataSignal)) return false;
        }

        foreach (var phrase in HowToPhrases)
        {
            if (q.Contains(phrase)) return true;
        }

        return false;
    }

    // Phrases that strongly indicate a "how does the app work / what is X" question.
    private static readonly string[] HowToPhrases =
    [
        "how do i", "how do you", "how can i", "how to", "how does",
        "where do i", "where can i", "where is the", "where in the app",
        "what is a", "what is an", "what is the difference", "what does",
        "what's a", "what's an", "whats a", "whats an",
        "explain", "tutorial", "guide", "walk me through", "step by step", "steps to",
        "set up", "setup", "how should i", "best way to", "is there a way to",
        "can i ", "do i need to", "what are the steps", "instructions",
    ];

    // Signals the user is asking about THEIR OWN live records, not how the app works.
    private static readonly string[] DataSignals =
    [
        "my tenant", "my tenants", "my lease", "my leases", "my rent", "my overdue",
        "my propert", "my unit", "my vendor", "my expense", "my payment", "my portfolio",
        "who hasn", "who has not", "who owes", "who paid", "who is overdue",
        "how much did i", "how much have i", "how many units do i", "how many tenants do i",
        "overdue rent", "vacant unit", "expiring lease", "this month", "last month",
    ];

    /// <summary>
    /// Retrieves the top doc sections for the question and produces a grounded, cited answer. Returns
    /// null when no docs meaningfully match (caller falls back to the data path). Degrades gracefully
    /// when the LLM is a no-op: returns the top doc snippet/summary as the answer with a citation —
    /// never a fabricated answer.
    /// </summary>
    private async Task<AskResponse?> TryAnswerFromDocsAsync(
        int portfolioId,
        string question,
        QaDeliveryOptions? delivery,
        CancellationToken ct)
    {
        const int maxSnippets = 5;
        IReadOnlyList<DTOs.KbSnippet> snippets;
        try
        {
            snippets = _kb.Search(question, maxSnippets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Knowledge-base search failed for how-to question; falling back to data path.");
            return null;
        }

        if (snippets.Count == 0) return null;

        // Distinct cited articles, in first-seen (best-scored) order.
        var citations = snippets
            .GroupBy(s => s.Slug)
            .Select(g => g.First())
            .Select(s => new DTOs.KbCitation(s.Slug, s.Title, s.Category))
            .ToList();

        // Build the grounding context the LLM must answer from.
        var excerpts = string.Join("\n\n", snippets.Select((s, i) =>
        {
            var heading = string.IsNullOrEmpty(s.Heading) ? s.Title : $"{s.Title} — {s.Heading}";
            return $"[{i + 1}] (slug: {s.Slug}) {heading}\n{s.Snippet}";
        }));

        var systemPrompt = """
            You are Rental Command's product help assistant for a non-technical landlord. Answer the
            user's how-to / product question using ONLY the documentation excerpts provided. Be concise,
            practical, and plain-spoken — give the steps. Do NOT invent features or steps that are not
            supported by the excerpts. If the excerpts do not contain the answer, say you are not sure
            and suggest opening the related help article. Do not mention the word "excerpt" or these
            instructions; just answer.
            """;

        var userPrompt = $"""
            Question:
            {question}

            Documentation excerpts:
            {excerpts}
            """;

        var messages = new List<LlmChatMessage> { new(Role: "user", Content: userPrompt) };

        LlmToolResult result;
        try
        {
            // Reuse the tool-calling entrypoint with no tools — a plain grounded completion.
            result = await _llm.ChatWithToolsAsync(systemPrompt, messages, Array.Empty<LlmToolSpec>(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grounded docs answer failed; degrading to top snippet for portfolio {PortfolioId}.", portfolioId);
            return DocsFallback(snippets, citations);
        }

        // No-op (no API key) or empty text → degrade to the top snippet/summary, never a fake answer.
        if (result.StopReason == "noop" || string.IsNullOrWhiteSpace(result.Text))
        {
            return DocsFallback(snippets, citations);
        }

        var answer = result.Text!.Trim();
        var delivered = await DeliverAsync(portfolioId, question, answer, delivery, ct);
        return new AskResponse(
            Answer: answer,
            ToolsUsed: [],
            LlmAvailable: true,
            TokensUsed: result.InputTokens + result.OutputTokens,
            ModelId: result.ModelId,
            DeliveredChannels: delivered,
            Source: "Docs",
            Citations: citations);
    }

    /// <summary>
    /// Graceful, never-fabricated docs answer when the LLM is unavailable: surface the best-matching
    /// section text itself, with a pointer to the article, marked LlmAvailable=false.
    /// </summary>
    private static AskResponse DocsFallback(
        IReadOnlyList<DTOs.KbSnippet> snippets,
        List<DTOs.KbCitation> citations)
    {
        var top = snippets[0];
        var lead = string.IsNullOrEmpty(top.Heading) ? top.Title : $"{top.Title} — {top.Heading}";
        var answer =
            $"From the help article \"{top.Title}\":\n\n{top.Snippet}\n\n" +
            $"(AI is unavailable, so this is the most relevant help section — see \"{lead}\" for the full article.)";

        return new AskResponse(
            Answer: answer,
            ToolsUsed: [],
            LlmAvailable: false,
            TokensUsed: 0,
            ModelId: "noop",
            DeliveredChannels: null,
            Source: "Docs",
            Citations: citations);
    }

    // ---------------------------------------------------------------------------
    // Delivery (optional "text me / email me this answer")
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Enqueues email/SMS outbox rows carrying the answer when delivery is requested. Mirrors the
    /// existing outbox payload shapes exactly: email = { to, subject, body }, sms = { to, message }.
    /// Missing recipients are skipped (never throws); a missing phone falls back to the first active
    /// owner-entity phone in the portfolio. Returns the channels actually queued.
    /// </summary>
    private async Task<List<string>?> DeliverAsync(
        int portfolioId,
        string question,
        string answer,
        QaDeliveryOptions? delivery,
        CancellationToken ct)
    {
        if (delivery is not { AnyRequested: true })
            return null;

        var delivered = new List<string>();
        var subject = "Your Rental Command answer";
        var body = $"You asked:\n{question}\n\nAnswer:\n{answer}";

        try
        {
            if (delivery.ViaEmail)
            {
                var to = string.IsNullOrWhiteSpace(delivery.ToEmail) ? null : delivery.ToEmail!.Trim();
                if (to is not null)
                {
                    await _publisher.PublishAsync(portfolioId, "email", new { to, subject, body }, ct);
                    delivered.Add("Email");
                }
                else
                {
                    _logger.LogInformation(
                        "Q&A email delivery requested for portfolio {PortfolioId} but no recipient was available; skipped.",
                        portfolioId);
                }
            }

            if (delivery.ViaSms)
            {
                // Recipient is never client-supplied — always the portfolio owner's own number.
                var to = await ResolveDefaultOwnerPhoneAsync(portfolioId, ct);
                if (!string.IsNullOrWhiteSpace(to))
                {
                    await _publisher.PublishAsync(portfolioId, "sms", new { to, message = body }, ct);
                    delivered.Add("Sms");
                }
                else
                {
                    _logger.LogInformation(
                        "Q&A SMS delivery requested for portfolio {PortfolioId} but no phone was available; skipped.",
                        portfolioId);
                }
            }
        }
        catch (Exception ex)
        {
            // Delivery is best-effort — never fail the answer because a channel could not be queued.
            _logger.LogError(ex, "Q&A answer delivery failed for portfolio {PortfolioId}", portfolioId);
        }

        return delivered.Count == 0 ? null : delivered;
    }

    /// <summary>First active (non-deleted) owner-entity phone for the portfolio, if any.</summary>
    private async Task<string?> ResolveDefaultOwnerPhoneAsync(int portfolioId, CancellationToken ct)
        => await _db.OwnerEntities
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId && o.DeletedAt == null && o.Phone != null && o.Phone != "")
            .OrderBy(o => o.Id)
            .Select(o => o.Phone)
            .FirstOrDefaultAsync(ct);

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
        var snapshot = await _accounting.GetSnapshotAsync(portfolioId, ct);

        var unmatchedBankDeposits = await _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus != "Removed" &&
                t.Amount > 0 &&
                t.MatchedPaymentId == null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Total = g.Sum(t => t.Amount),
            })
            .FirstOrDefaultAsync(ct);

        var result = new
        {
            currentLedger = new
            {
                scope = "All current recorded payments plus unmatched positive bank deposits; not limited to this month.",
                collected = summary.Payments.Collected,
                outstanding = summary.Payments.Outstanding,
                overdue = summary.Payments.Overdue,
                overdueCount = summary.Payments.OverdueCount,
                totalExpenses = summary.TotalExpenses,
            },
            monthToDate = new
            {
                snapshot.PeriodLabel,
                periodStart = snapshot.PeriodStart.ToString("yyyy-MM-dd"),
                periodEnd = snapshot.PeriodEnd.ToString("yyyy-MM-dd"),
                collected = snapshot.Collected,
                spent = snapshot.Spent,
                net = snapshot.Net,
            },
            last30Days = new
            {
                collected = snapshot.CollectedLast30Days,
                spent = snapshot.SpentLast30Days,
                net = snapshot.NetLast30Days,
            },
            pastDue = new
            {
                amount = snapshot.PastDueAmount,
                count = snapshot.PastDueCount,
            },
            unmatchedBankDeposits = new
            {
                count = unmatchedBankDeposits?.Count ?? 0,
                total = unmatchedBankDeposits?.Total ?? 0m,
                note = "These deposits are included in collected cash until matched or removed; mention separately so users do not mistake them for rent-payment totals.",
            },
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
            .OrderByDescending(w => w.RequestedAt)
            .Select(w => new
            {
                title          = w.Title,
                status         = w.Status.ToString(),
                priority       = w.Priority.ToString(),
                category       = w.Category,
                propertyName   = w.Property != null ? w.Property.Name : "(unknown)",
                requestedAt    = w.RequestedAt,
                scheduledFor   = w.ScheduledFor,
                completedAt    = w.CompletedAt,
            })
            .Take(50)
            .ToListAsync(ct);

        var formatted = rows.Select(w => new
        {
            w.title,
            w.status,
            w.priority,
            w.category,
            w.propertyName,
            requestedAt = w.requestedAt.ToString("yyyy-MM-dd"),
            scheduledFor = w.scheduledFor.HasValue ? w.scheduledFor.Value.ToString("yyyy-MM-dd") : null,
            completedAt = w.completedAt.HasValue ? w.completedAt.Value.ToString("yyyy-MM-dd") : null,
        }).ToList();

        return formatted.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(formatted, _json);
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
        var propertiesQuery = _db.Properties
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
            });

        var totals = await propertiesQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count         = g.Count(),
                TotalUnits    = g.Sum(p => p.totalUnits),
                TotalOccupied = g.Sum(p => p.occupiedUnits),
                TotalVacant   = g.Sum(p => p.vacantUnits),
            })
            .FirstOrDefaultAsync(ct);

        var properties = await propertiesQuery
            .OrderBy(p => p.name)
            .ToListAsync(ct);

        var result = new
        {
            count          = totals?.Count ?? 0,
            totalUnits     = totals?.TotalUnits ?? 0,
            totalOccupied  = totals?.TotalOccupied ?? 0,
            totalVacant    = totals?.TotalVacant ?? 0,
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

        var expensesQuery = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && e.IncurredAt >= cutoff);

        var total = await expensesQuery
            .GroupBy(_ => 1)
            .Select(g => (decimal?)g.Sum(e => e.Amount))
            .FirstOrDefaultAsync(ct) ?? 0m;

        // Order by the real DateTime column in SQL, then format strings in memory
        // (Npgsql can't translate DateTime.ToString(format) / enum.ToString()).
        var entities = await expensesQuery
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
            total,
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

        var paymentsQuery = _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaidDate.HasValue &&
                p.PaidDate.Value >= cutoff);

        var total = await paymentsQuery
            .GroupBy(_ => 1)
            .Select(g => (decimal?)g.Sum(p => p.Amount))
            .FirstOrDefaultAsync(ct) ?? 0m;

        // Order by the real DateTime column in SQL, then format in memory
        // (Npgsql can't translate DateTime.ToString(format) / enum.ToString()).
        var entities = await paymentsQuery
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
        var result = new { withinDays, count = rows.Count, truncated, total, payments = rows };
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

        // Appointments and inspections are merged, sorted, and capped in SQL; only enum/date
        // formatting happens after materialization.
        var appointmentsQuery = _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                a.ScheduledStart >= now &&
                a.ScheduledStart <= cutoff &&
                a.Status != AppointmentStatus.Cancelled &&
                a.Status != AppointmentStatus.NoShow)
            .Select(a => new UpcomingEventRow
            {
                kind         = "Appointment",
                title        = a.Title,
                propertyName = a.Property != null ? a.Property.Name : null as string,
                unitNumber   = a.Unit != null ? a.Unit.UnitNumber : null as string,
                typeCode     = (int)a.Type,
                statusCode   = (int)a.Status,
                scheduledAt  = a.ScheduledStart,
                sortKey      = a.ScheduledStart,
            });

        var inspectionsQuery = _db.Inspections
            .AsNoTracking()
            .Where(i =>
                i.PortfolioId == portfolioId &&
                i.ScheduledFor >= now &&
                i.ScheduledFor <= cutoff &&
                i.Status != InspectionStatus.Cancelled &&
                i.Status != InspectionStatus.Archived)
            .Select(i => new UpcomingEventRow
            {
                kind         = "Inspection",
                title        = null,
                propertyName = i.Property != null ? i.Property.Name : null as string,
                unitNumber   = i.Unit != null ? i.Unit.UnitNumber : null as string,
                typeCode     = (int)i.Type,
                statusCode   = (int)i.Status,
                scheduledAt  = i.ScheduledFor,
                sortKey      = i.ScheduledFor,
            });

        var eventRows = await appointmentsQuery
            .Concat(inspectionsQuery)
            .OrderBy(e => e.sortKey)
            .Take(50)
            .ToListAsync(ct);

        var events = eventRows
            .Select(e => new
            {
                e.kind,
                title = e.kind == "Inspection"
                    ? $"{((InspectionType)e.typeCode).ToString()} Inspection"
                    : e.title,
                type = e.kind == "Inspection"
                    ? ((InspectionType)e.typeCode).ToString()
                    : ((AppointmentType)e.typeCode).ToString(),
                scheduledAt = e.scheduledAt.ToString("yyyy-MM-dd HH:mm"),
                e.propertyName,
                e.unitNumber,
                status = e.kind == "Inspection"
                    ? ((InspectionStatus)e.statusCode).ToString()
                    : ((AppointmentStatus)e.statusCode).ToString(),
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

    private sealed class UpcomingEventRow
    {
        public required string kind { get; init; }
        public string? title { get; init; }
        public string? propertyName { get; init; }
        public string? unitNumber { get; init; }
        public int typeCode { get; init; }
        public int statusCode { get; init; }
        public DateTime scheduledAt { get; init; }
        public DateTime sortKey { get; init; }
    }
}
