using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

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
    private readonly TimeProvider _timeProvider;

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
            "Unassigned bank transactions are excluded because they do not have a property authorization target.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_overdue_rent",
            "Returns open tenant charges whose due date is in the past for currently occupied " +
            "or ending tenancies. Each item includes the agreement or account number, unit number, " +
            "tenant name, open amount, due date, and how many days overdue.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_active_leases",
            "Returns governing agreements for currently occupied units with tenant name, unit number, " +
            "monthly rent, rent due day, lease start and end dates.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_work_orders",
            "Returns work orders for properties the caller may read. Pass openOnly=true to see only open " +
            "(non-completed, non-cancelled, non-archived) orders. Each item includes title, " +
            "status, priority, category, property name, and scheduled/completed dates.",
            """{"type":"object","properties":{"openOnly":{"type":"boolean","description":"When true, exclude Completed, Cancelled, and Archived work orders. Defaults to false."}},"required":[]}"""),

        new LlmToolSpec(
            "list_expiring_leases",
            "Returns governing agreements for currently occupied units whose end date falls within the next N days. " +
            "Useful for 'any leases expiring soon?' questions.",
            """{"type":"object","properties":{"withinDays":{"type":"integer","description":"Number of days to look ahead. Defaults to 60."}},"required":[]}"""),

        new LlmToolSpec(
            "list_vacant_units",
            "Returns rent-ready units with no current or scheduled possession across the portfolio, including the " +
            "unit number, property name, and market rent.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_tenants",
            "Returns tenants currently occupying properties the caller may read: first and last name, email, phone, " +
            "their current unit number and property name (from current possession, if any), " +
            "and whether they currently belong to an occupying household. " +
            "Use for 'who are my tenants?', 'what is tenant X's phone number?'.",
            """{"type":"object","properties":{},"required":[]}"""),

        new LlmToolSpec(
            "list_properties",
            "Returns each property the caller may read: name, city, state, total unit count, " +
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
            "Returns posted tenant-ledger payment receipts from the last N days, most recent first: " +
            "tenant name, unit number, amount, received date, payment method, and provider status. " +
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
            "Returns vendors linked to work orders on properties the caller may read: name, service type, phone, and email. " +
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
        ILogger<PortfolioQaService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _llm = llm;
        _accounting = accounting;
        _publisher = publisher;
        _kb = kb;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    public async Task<AskResponse> AskAsync(
        WorkspaceReadScope scope,
        string question,
        IReadOnlyList<QaTurn>? history,
        QaDeliveryOptions? delivery = null,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        // Route product/how-to questions ("how do I record a payment?", "what is a security
        // deposit?") to the knowledge-base path; everything else stays on the live-data tool path.
        // The heuristic is intentionally conservative — when in doubt, answer from data.
        if (LooksLikeHowTo(question))
        {
            var kbAnswer = await TryAnswerFromDocsAsync(scope, question, delivery, ct);
            if (kbAnswer is not null) return kbAnswer;
            // No relevant docs matched — fall through to the data path so we still try to help.
        }

        var today = _timeProvider.UtcNow().Date;
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
                    var toolResult = await ExecuteToolAsync(call, scope, ct);
                    messages.Add(new LlmChatMessage(
                        Role: "tool",
                        Content: toolResult,
                        ToolCallId: call.Id));
                }

                continue; // next iteration
            }

            // Final answer path.
            var answer = result.Text ?? "(The assistant returned no text.)";
            var delivered = await DeliverAsync(scope, question, answer, delivery, ct);
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
        var deliveredIncomplete = await DeliverAsync(scope, question, incompleteAnswer, delivery, ct);
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
        WorkspaceReadScope scope,
        string question,
        QaDeliveryOptions? delivery,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
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
        var delivered = await DeliverAsync(scope, question, answer, delivery, ct);
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
    /// Missing recipients are skipped (never throws). Returns the channels actually queued.
    /// </summary>
    private async Task<List<string>?> DeliverAsync(
        WorkspaceReadScope scope,
        string question,
        string answer,
        QaDeliveryOptions? delivery,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
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
                    await _publisher.PublishAsync(
                        portfolioId,
                        "email",
                        RentalCommand.Core.Outbox.OutboxIdempotency.Create(
                            "portfolio-qa", portfolioId, "email", to, question, answer),
                        new { to, subject, body },
                        ct);
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
                var to = string.IsNullOrWhiteSpace(delivery.ToSms) ? null : delivery.ToSms!.Trim();
                if (!string.IsNullOrWhiteSpace(to))
                {
                    await _publisher.PublishAsync(
                        portfolioId,
                        "sms",
                        RentalCommand.Core.Outbox.OutboxIdempotency.Create(
                            "portfolio-qa", portfolioId, "sms", to, question, answer),
                        new { to, message = body },
                        ct);
                    delivered.Add("Sms");
                }
                else
                {
                    _logger.LogInformation(
                        "Q&A SMS delivery requested for portfolio {PortfolioId} but no phone was available; skipped.",
                        portfolioId);
                }
            }

            if (delivered.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Delivery is best-effort — never fail the answer because a channel could not be queued.
            _logger.LogError(ex, "Q&A answer delivery failed for portfolio {PortfolioId}", portfolioId);
            delivered.Clear();
        }

        return delivered.Count == 0 ? null : delivered;
    }

    // ---------------------------------------------------------------------------
    // Tool dispatcher
    // ---------------------------------------------------------------------------

    private async Task<string> ExecuteToolAsync(
        LlmToolCall call,
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "get_financial_summary"  => await GetFinancialSummaryAsync(scope, ct),
                "list_overdue_rent"      => await ListOverdueRentAsync(scope, ct),
                "list_active_leases"     => await ListActiveLeasesAsync(scope, ct),
                "list_work_orders"       => await ListWorkOrdersAsync(call.ArgumentsJson, scope, ct),
                "list_expiring_leases"   => await ListExpiringLeasesAsync(call.ArgumentsJson, scope, ct),
                "list_vacant_units"      => await ListVacantUnitsAsync(scope, ct),
                "list_tenants"           => await ListTenantsAsync(scope, ct),
                "list_properties"        => await ListPropertiesAsync(scope, ct),
                "list_recent_expenses"   => await ListRecentExpensesAsync(call.ArgumentsJson, scope, ct),
                "list_recent_payments"   => await ListRecentPaymentsAsync(call.ArgumentsJson, scope, ct),
                "list_upcoming_events"   => await ListUpcomingEventsAsync(call.ArgumentsJson, scope, ct),
                "list_vendors"           => await ListVendorsAsync(scope, ct),
                _                        => $"Error: unknown tool '{call.Name}'.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Q&A tool {Tool} failed for portfolio {PortfolioId}", call.Name, scope.PortfolioId);
            return $"Error: tool '{call.Name}' could not retrieve data right now. Tell the user this information is temporarily unavailable.";
        }
    }

    // ---------------------------------------------------------------------------
    // Tool implementations
    // ---------------------------------------------------------------------------

    private async Task<string> GetFinancialSummaryAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var summary = await _accounting.GetSummaryAsync(scope, ct);
        var snapshot = await _accounting.GetSnapshotAsync(scope, ct);

        var result = new
        {
            currentLedger = new
            {
                scope = "Current recorded payments and balances for authorized properties; not limited to this month.",
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
            unassignedBankTransactionsExcluded = true,
            totalExpenses = summary.TotalExpenses,
            expensesByCategory = summary.ExpensesByCategory
                .Select(e => new { category = e.CategoryName, total = e.Total, count = e.Count })
                .ToList(),
        };

        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListOverdueRentAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead);
        var today = DateOnly.FromDateTime(_timeProvider.UtcNow());

        // Cap the tool result like every sibling list tool; both the returned page and the truncation
        // check stay DB-side so we do not materialize an unbounded overdue set into the LLM context.
        const int maxRows = 50;
        var overdueQuery =
            from charge in _db.TenantChargeBalanceProjections.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { charge.PortfolioId, charge.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join property in authorizedProperties
                on new { lifecycle.PortfolioId, Id = lifecycle.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, Id = (int?)agreement.Id }
                into agreementRows
            from agreement in agreementRows.DefaultIfEmpty()
            where charge.PortfolioId == portfolioId
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && charge.IsPastDue
                && charge.OpenAmount > 0m
                && charge.DueOn != null
            orderby charge.DueOn, charge.TenantLedgerEntryId
            select new
            {
                leaseNumber = agreement != null ? agreement.AgreementNumber : account.AccountNumber,
                unitNumber = unit.UnitNumber,
                tenantName = lifecycle.CurrentPrimaryTenantName,
                amount = charge.OpenAmount,
                dueDate = charge.DueOn,
            };

        var paymentRows = await overdueQuery
            .Take(maxRows)
            .ToListAsync(ct);
        var pageCount = await overdueQuery.Take(maxRows).CountAsync(ct);
        var truncated = await overdueQuery
            .Skip(maxRows)
            .AnyAsync(ct);

        var rows = paymentRows
            .Select(p => new
            {
                p.leaseNumber,
                p.unitNumber,
                p.tenantName,
                p.amount,
                dueDate = p.dueDate!.Value.ToString("yyyy-MM-dd"),
                daysOverdue = today.DayNumber - p.dueDate.Value.DayNumber,
                status = "PastDue",
            })
            .ToList();

        var result = new { count = pageCount, truncated, overdue = rows };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListActiveLeasesAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var rows = await (
            from occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { occupancy.PortfolioId, LeaseManagementId = occupancy.CurrentLeaseManagementId }
                equals new { lifecycle.PortfolioId, LeaseManagementId = (int?)lifecycle.LeaseManagementId }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, AgreementId = (int?)agreement.Id }
            join agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
                on new { agreement.PortfolioId, AgreementId = agreement.Id }
                equals new { agreementStatus.PortfolioId, agreementStatus.AgreementId }
            join property in authorizedProperties
                on new { occupancy.PortfolioId, Id = occupancy.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { occupancy.PortfolioId, Id = occupancy.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            where occupancy.PortfolioId == portfolioId
                && occupancy.IsOccupied
                && agreementStatus.IsGoverning
            orderby lifecycle.CurrentPrimaryTenantName, agreement.AgreementNumber
            select new
            {
                leaseNumber = agreement.AgreementNumber,
                tenantName = lifecycle.CurrentPrimaryTenantName ?? "(unknown)",
                unitNumber = unit.UnitNumber,
                monthlyRent = agreement.BaseRentAmount,
                rentDueDay = agreement.RentDueDay,
                startDate = agreement.TermStartOn,
                endDate = agreement.TermEndOn,
            })
            .Take(50)
            .ToListAsync(ct);

        return JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListWorkOrdersAsync(
        string argsJson,
        WorkspaceReadScope scope,
        CancellationToken ct)
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
            .Where(w =>
                w.PortfolioId == scope.PortfolioId &&
                AuthorizedProperties(scope, CapabilityKeys.WorkRead)
                    .Any(property => property.Id == w.PropertyId));

        if (openOnly)
            query = query.Where(w => openStatuses.Contains(w.Status));

        var rows = await query
            .OrderByDescending(w => w.RequestedAt)
            .Select(w => new
            {
                title          = w.Title,
                statusCode     = (int)w.Status,
                priorityCode   = (int)w.Priority,
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
            status = ((WorkOrderStatus)w.statusCode).ToString(),
            priority = ((WorkOrderPriority)w.priorityCode).ToString(),
            w.category,
            w.propertyName,
            requestedAt = w.requestedAt.ToString("yyyy-MM-dd"),
            scheduledFor = w.scheduledFor.HasValue ? w.scheduledFor.Value.ToString("yyyy-MM-dd") : null,
            completedAt = w.completedAt.HasValue ? w.completedAt.Value.ToString("yyyy-MM-dd") : null,
        }).ToList();

        return JsonSerializer.Serialize(formatted, _json);
    }

    private async Task<string> ListExpiringLeasesAsync(
        string argsJson,
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
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

        var rows = await (
            from occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { occupancy.PortfolioId, LeaseManagementId = occupancy.CurrentLeaseManagementId }
                equals new { lifecycle.PortfolioId, LeaseManagementId = (int?)lifecycle.LeaseManagementId }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, AgreementId = (int?)agreement.Id }
            join agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
                on new { agreement.PortfolioId, AgreementId = agreement.Id }
                equals new { agreementStatus.PortfolioId, agreementStatus.AgreementId }
            join property in authorizedProperties
                on new { occupancy.PortfolioId, Id = occupancy.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { occupancy.PortfolioId, Id = occupancy.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            where occupancy.PortfolioId == portfolioId
                && occupancy.IsOccupied
                && agreementStatus.IsGoverning
                && agreement.TermEndOn != null
                && agreement.TermEndOn >= agreementStatus.BusinessDate
                && agreement.TermEndOn <= agreementStatus.BusinessDate.AddDays(withinDays)
            orderby agreement.TermEndOn, agreement.AgreementNumber
            select new
            {
                leaseNumber = agreement.AgreementNumber,
                tenantName = lifecycle.CurrentPrimaryTenantName ?? "(unknown)",
                unitNumber = unit.UnitNumber,
                monthlyRent = agreement.BaseRentAmount,
                endDate = agreement.TermEndOn!.Value,
                daysLeft = agreement.TermEndOn.Value.DayNumber - agreementStatus.BusinessDate.DayNumber,
            })
            .Take(50)
            .ToListAsync(ct);

        return JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListVacantUnitsAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var rows = await (
            from occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            join unit in _db.Units.AsNoTracking()
                on new { occupancy.PortfolioId, Id = occupancy.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join property in authorizedProperties
                on new { occupancy.PortfolioId, Id = occupancy.PropertyId }
                equals new { property.PortfolioId, property.Id }
            where occupancy.PortfolioId == portfolioId
                && !occupancy.IsOccupied
                && !occupancy.HasScheduledMoveIn
                && !occupancy.IsInTurnover
                && !occupancy.IsOutOfService
                && !occupancy.IsOnManagementHold
            orderby property.Name, unit.UnitNumber
            select new
            {
                unitNumber = unit.UnitNumber,
                propertyName = property.Name,
                marketRent = unit.MarketRent,
                bedrooms = unit.Bedrooms,
                bathrooms = unit.Bathrooms,
            })
            .Take(50)
            .ToListAsync(ct);

        return JsonSerializer.Serialize(rows, _json);
    }

    private async Task<string> ListTenantsAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        const int maxRows = 50;

        // Current Unit context is a correlated SQL subquery over the effective-dated household
        // and the canonical possession projection; legacy Lease.Status is intentionally absent.
        var tenantsQuery = _db.Tenants
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                (from party in _db.LeaseManagementParties
                 join occupancy in _db.UnitOccupancyProjections
                     on new { party.PortfolioId, LeaseManagementId = (int?)party.LeaseManagementId }
                     equals new { occupancy.PortfolioId, LeaseManagementId = occupancy.CurrentLeaseManagementId }
                 join property in authorizedProperties
                     on new { occupancy.PortfolioId, Id = occupancy.PropertyId }
                     equals new { property.PortfolioId, property.Id }
                 where party.PortfolioId == portfolioId &&
                       party.TenantId == t.Id &&
                       party.Role != LeaseManagementPartyRole.Guarantor &&
                       occupancy.IsOccupied
                 select party.Id).Any())
            .Select(t => new
            {
                name         = t.FirstName + " " + t.LastName,
                email        = t.Email,
                phone        = t.Phone,
                currentOccupancy = (
                    from party in _db.LeaseManagementParties
                    join occupancy in _db.UnitOccupancyProjections
                        on new { party.PortfolioId, LeaseManagementId = (int?)party.LeaseManagementId }
                        equals new { occupancy.PortfolioId, LeaseManagementId = occupancy.CurrentLeaseManagementId }
                    join lifecycle in _db.LeaseManagementLifecycleProjections
                        on new { party.PortfolioId, party.LeaseManagementId }
                        equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                    join unit in _db.Units
                        on new { occupancy.PortfolioId, Id = occupancy.UnitId }
                        equals new { unit.PortfolioId, unit.Id }
                    join property in authorizedProperties
                        on new { occupancy.PortfolioId, Id = occupancy.PropertyId }
                        equals new { property.PortfolioId, property.Id }
                    where party.PortfolioId == portfolioId
                        && party.TenantId == t.Id
                        && party.Role != LeaseManagementPartyRole.Guarantor
                        && occupancy.IsOccupied
                        && party.EffectiveFrom <= lifecycle.BusinessDate
                        && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
                    orderby party.Id
                    select new
                    {
                        unitNumber = unit.UnitNumber,
                        propertyName = property.Name,
                    }).FirstOrDefault(),
            })
            .OrderBy(t => t.name);

        var tenants = await tenantsQuery.Take(maxRows).ToListAsync(ct);
        var pageCount = await tenantsQuery.Take(maxRows).CountAsync(ct);
        var truncated = await tenantsQuery.Skip(maxRows).AnyAsync(ct);
        var rows = tenants.Select(t => new
        {
            t.name,
            t.email,
            t.phone,
            unitNumber = t.currentOccupancy != null ? t.currentOccupancy.unitNumber : null as string,
            propertyName = t.currentOccupancy != null ? t.currentOccupancy.propertyName : null as string,
            hasCurrentOccupancy = t.currentOccupancy != null,
        }).ToList();

        var result = new { count = pageCount, truncated, tenants = rows };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListPropertiesAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var propertiesQuery = _db.Properties
            .AsNoTracking()
            .Where(property => property.DeletedAt == null)
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.RentalsRead,
                _timeProvider.GetUtcNow().UtcDateTime)
            .Select(p => new
            {
                name         = p.Name,
                city         = p.City,
                state        = p.State,
                totalUnits = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id),
                occupiedUnits = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.PropertyId == p.Id
                    && occupancy.IsOccupied),
                vacantUnits = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.PropertyId == p.Id
                    && !occupancy.IsOccupied
                    && !occupancy.HasScheduledMoveIn
                    && !occupancy.IsInTurnover
                    && !occupancy.IsOutOfService
                    && !occupancy.IsOnManagementHold),
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

        const int maxRows = 50;
        var orderedProperties = propertiesQuery.OrderBy(p => p.name);
        var properties = await orderedProperties.Take(maxRows).ToListAsync(ct);
        var truncated = await orderedProperties.Skip(maxRows).AnyAsync(ct);

        var result = new
        {
            count          = totals?.Count ?? 0,
            totalUnits     = totals?.TotalUnits ?? 0,
            totalOccupied  = totals?.TotalOccupied ?? 0,
            totalVacant    = totals?.TotalVacant ?? 0,
            truncated,
            properties,
        };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListRecentExpensesAsync(
        string argsJson,
        WorkspaceReadScope scope,
        CancellationToken ct)
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
        var cutoff = _timeProvider.UtcNow().AddDays(-withinDays);

        var expensesQuery = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == scope.PortfolioId &&
                e.IncurredAt >= cutoff &&
                e.PropertyId != null &&
                AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead)
                    .Any(property => property.Id == e.PropertyId));

        var aggregate = await expensesQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(expense => expense.Amount),
                PageCount = g.Count() > maxRows ? maxRows : g.Count(),
            })
            .FirstOrDefaultAsync(ct);

        var orderedExpenses =
            from expense in expensesQuery
            join property in _db.Properties.AsNoTracking()
                on new { expense.PortfolioId, Id = expense.PropertyId }
                equals new { property.PortfolioId, Id = (int?)property.Id }
            join vendor in _db.Vendors.AsNoTracking()
                on new { expense.PortfolioId, Id = expense.VendorId }
                equals new { vendor.PortfolioId, Id = (int?)vendor.Id }
                into vendorRows
            from vendor in vendorRows.DefaultIfEmpty()
            orderby expense.IncurredAt descending, expense.Id descending
            select new
            {
                expense.IncurredAt,
                expense.Description,
                VendorName = vendor != null ? vendor.Name : null,
                expense.Category,
                expense.Amount,
                PropertyName = property.Name,
                expense.Status,
            };

        // Order and page by typed columns in SQL; only date/enum display formatting is in memory.
        var entities = await orderedExpenses.Take(maxRows).ToListAsync(ct);
        var truncated = await orderedExpenses.Skip(maxRows).AnyAsync(ct);

        var rows = entities.Select(e => new
        {
            date         = e.IncurredAt.ToString("yyyy-MM-dd"),
            description  = e.Description,
            vendor       = e.VendorName,
            category     = e.Category.ToString(),
            amount       = e.Amount,
            propertyName = e.PropertyName,
            status       = e.Status.ToString(),
        }).ToList();
        var result = new
        {
            withinDays,
            count     = aggregate?.PageCount ?? 0,
            truncated,
            total     = aggregate?.Total ?? 0m,
            expenses  = rows,
        };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListRecentPaymentsAsync(
        string argsJson,
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead);
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
        var cutoff = DateOnly.FromDateTime(_timeProvider.UtcNow().AddDays(-withinDays));

        var paymentsQuery =
            from entry in _db.TenantLedgerEntries.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { entry.PortfolioId, Id = entry.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join property in authorizedProperties
                on new { management.PortfolioId, Id = management.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join unit in _db.Units.AsNoTracking()
                on new { management.PortfolioId, Id = management.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            where entry.PortfolioId == portfolioId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                && entry.EffectiveOn >= cutoff
            select new
            {
                entry,
                tenantName = entry.ProviderPaymentAttempt != null
                    ? entry.ProviderPaymentAttempt.PayerName
                    : lifecycle.CurrentPrimaryTenantName,
                unitNumber = unit.UnitNumber,
                providerState = entry.ProviderPaymentAttempt != null
                    ? (TenantPaymentAttemptState?)entry.ProviderPaymentAttempt.State
                    : null,
                paymentMethod = entry.ProviderPaymentAttempt != null
                    ? entry.ProviderPaymentAttempt.PaymentMethodSummary
                    : null,
                isSecurityDeposit = _db.SecurityDepositEntries.Any(deposit =>
                    deposit.PortfolioId == entry.PortfolioId
                    && deposit.TenantLedgerEntryId == entry.Id
                    && deposit.EntryType == SecurityDepositEntryType.Receipt),
            };

        var aggregate = await paymentsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(payment => payment.entry.Amount),
                PageCount = g.Count() > maxRows ? maxRows : g.Count(),
            })
            .FirstOrDefaultAsync(ct);

        // Order and cap by the typed ledger date in SQL, then format DateOnly/enums in memory.
        var orderedPayments = paymentsQuery
            .OrderByDescending(p => p.entry.EffectiveOn)
            .ThenByDescending(p => p.entry.Id);
        var entities = await orderedPayments.Take(maxRows).ToListAsync(ct);
        var truncated = await orderedPayments.Skip(maxRows).AnyAsync(ct);

        var rows = entities.Select(p => new
        {
            tenantName = p.tenantName ?? "(unknown)",
            p.unitNumber,
            amount = p.entry.Amount,
            paidDate = p.entry.EffectiveOn.ToString("yyyy-MM-dd"),
            paymentType = p.isSecurityDeposit
                ? "SecurityDeposit"
                : p.paymentMethod ?? "PaymentReceipt",
            status = p.providerState?.ToString() ?? "Posted",
        }).ToList();
        var result = new
        {
            withinDays,
            count = aggregate?.PageCount ?? 0,
            truncated,
            total = aggregate?.Total ?? 0m,
            payments = rows,
        };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListUpcomingEventsAsync(
        string argsJson,
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var workProperties = AuthorizedProperties(scope, CapabilityKeys.WorkRead);
        var rentalProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var showingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingShowingsManage);
        var onboardingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingOnboardingManage);
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

        var now    = _timeProvider.UtcNow();
        var cutoff = now.AddDays(withinDays);

        // Appointments and inspections are merged, sorted, and capped in SQL; only enum/date
        // formatting happens after materialization.
        var appointmentsQuery = _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                a.PropertyId != null &&
                ((a.Type == AppointmentType.Showing &&
                  showingProperties.Any(property => property.Id == a.PropertyId)) ||
                 ((a.Type == AppointmentType.MoveIn || a.Type == AppointmentType.MoveOut) &&
                  onboardingProperties.Any(property => property.Id == a.PropertyId)) ||
                 ((a.Type == AppointmentType.Inspection || a.Type == AppointmentType.MaintenanceVisit) &&
                  workProperties.Any(property => property.Id == a.PropertyId)) ||
                 (a.Type == AppointmentType.OwnerMeeting &&
                  rentalProperties.Any(property => property.Id == a.PropertyId))) &&
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
                workProperties.Any(property => property.Id == i.PropertyId) &&
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

        var eventsQuery = appointmentsQuery.Concat(inspectionsQuery);
        var eventRows = await eventsQuery
            .OrderBy(e => e.sortKey)
            .Take(50)
            .ToListAsync(ct);
        var eventCount = await eventsQuery.Take(50).CountAsync(ct);

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

        var result = new { withinDays, count = eventCount, events };
        return JsonSerializer.Serialize(result, _json);
    }

    private async Task<string> ListVendorsAsync(WorkspaceReadScope scope, CancellationToken ct)
    {
        const int maxRows = 50;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.WorkRead);

        var vendorsQuery = _db.Vendors
            .AsNoTracking()
            .Where(v =>
                v.PortfolioId == scope.PortfolioId &&
                _db.WorkOrders.Any(workOrder =>
                    workOrder.PortfolioId == scope.PortfolioId &&
                    workOrder.VendorId == v.Id &&
                    authorizedProperties.Any(property => property.Id == workOrder.PropertyId)))
            .Select(v => new
            {
                name        = v.Name,
                serviceType = v.ServiceType,
                phone       = v.Phone,
                email       = v.Email,
                preferred   = v.Preferred,
            })
            .OrderBy(v => v.serviceType)
            .ThenBy(v => v.name);

        var rows = await vendorsQuery.Take(maxRows).ToListAsync(ct);
        var pageCount = await vendorsQuery.Take(maxRows).CountAsync(ct);
        var truncated = await vendorsQuery.Skip(maxRows).AnyAsync(ct);
        var result = new { count = pageCount, truncated, vendors = rows };
        return JsonSerializer.Serialize(result, _json);
    }

    private IQueryable<RentalCommand.Core.Entities.Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        string capabilityKey) =>
        _db.Properties
            .AsNoTracking()
            .Where(property => property.DeletedAt == null)
            .WhereAuthorized(
                _db,
                scope,
                capabilityKey,
                _timeProvider.GetUtcNow().UtcDateTime);

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
