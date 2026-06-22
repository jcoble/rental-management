using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NoticeDraftService : INoticeDraftService
{
    // Renewal terms: propose a modest escalation on the current rent for the new term.
    private const decimal RenewalEscalationPercent = 3.0m;
    private const int RenewalTermMonths = 12;

    private readonly RentalCommandDbContext _db;
    private readonly IConversationService _conversations;
    private readonly ILlmProvider _llm;
    private readonly ILogger<NoticeDraftService> _logger;

    public NoticeDraftService(
        RentalCommandDbContext db,
        IConversationService conversations,
        ILlmProvider llm,
        ILogger<NoticeDraftService> logger)
    {
        _db = db;
        _conversations = conversations;
        _llm = llm;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(
        int portfolioId,
        string? status,
        CancellationToken ct = default)
    {
        var query = BaseQuery(portfolioId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var drafts = await query
            .OrderBy(d => d.Status == "Draft" ? 0 : 1)
            .ThenBy(d => d.TriggerDate)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return drafts.Select(Map).ToList();
    }

    public async Task<GenerateNoticeDraftsResponse> GenerateAsync(
        int portfolioId,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var created = new List<NoticeDraft>();

        var tenantId = request?.TenantId;
        var requestedType = string.IsNullOrWhiteSpace(request?.NoticeType) ? null : request!.NoticeType!.Trim();
        // When a specific type is requested for a tenant, force renewal/move-out even outside the
        // usual trigger window — the landlord explicitly asked for that notice.
        var forced = requestedType != null;

        bool WantsType(string type) => requestedType == null || string.Equals(requestedType, type, StringComparison.OrdinalIgnoreCase);
        var wantsRenewal = WantsType("RenewalOffer");
        var wantsMoveOut = WantsType("MoveOutReminder");

        // Pre-load the existing open ("Draft") notices for this portfolio ONCE as a
        // (leaseId, noticeType) set, so the per-lease / per-payment idempotency check below is an
        // in-memory lookup instead of an AnyAsync round-trip per iteration (N+1). The set is scoped
        // to the requested tenant's leases when a tenant filter is in play.
        var existingDraftsQuery = _db.NoticeDrafts
            .AsNoTracking()
            .Where(d => d.PortfolioId == portfolioId && d.Status == "Draft");
        if (tenantId.HasValue)
        {
            existingDraftsQuery = existingDraftsQuery.Where(d => d.TenantId == tenantId.Value);
        }
        var existingDrafts = (await existingDraftsQuery
                .Select(d => new { d.LeaseId, d.NoticeType })
                .ToListAsync(ct))
            .Select(d => (d.LeaseId, d.NoticeType))
            .ToHashSet();

        if (wantsRenewal || wantsMoveOut)
        {
            var leaseQuery = _db.Leases
                .Include(l => l.Tenant)
                .Include(l => l.Property)
                .Include(l => l.Unit)
                .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active);
            if (tenantId.HasValue)
            {
                leaseQuery = leaseQuery.Where(l => l.TenantId == tenantId.Value);
            }

            if (!forced)
            {
                var maxDays = wantsRenewal ? 75 : 30;
                var windowEndExclusive = today.AddDays(maxDays + 1);
                leaseQuery = leaseQuery.Where(l => l.EndDate >= today && l.EndDate < windowEndExclusive);
            }

            var leases = await leaseQuery.ToListAsync(ct);

            foreach (var lease in leases)
            {
                if (lease.Tenant == null) continue;

                var daysToEnd = (lease.EndDate.Date - today).Days;
                if (wantsRenewal
                    && (forced || (daysToEnd >= 0 && daysToEnd <= 75))
                    && !DraftExists(existingDrafts, lease.Id, "RenewalOffer", created))
                {
                    created.Add(await BuildRenewalDraftAsync(portfolioId, lease, daysToEnd, now, ct));
                }

                if (wantsMoveOut
                    && (forced || (daysToEnd >= 0 && daysToEnd <= 30))
                    && !DraftExists(existingDrafts, lease.Id, "MoveOutReminder", created))
                {
                    created.Add(await BuildMoveOutDraftAsync(portfolioId, lease, daysToEnd, now, ct));
                }
            }
        }

        // Late-rent notices are always grounded in a real overdue payment (we need the amount/due
        // date), so even a forced request only produces one when such a payment exists.
        if (WantsType("LateRentNotice"))
        {
            var lateQuery = _db.Payments
                .Include(p => p.Lease).ThenInclude(l => l!.Tenant)
                .Include(p => p.Lease).ThenInclude(l => l!.Property)
                .Include(p => p.Lease).ThenInclude(l => l!.Unit)
                .Where(p =>
                    p.PortfolioId == portfolioId &&
                    p.DueDate.Date < today &&
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial));
            if (tenantId.HasValue)
            {
                lateQuery = lateQuery.Where(p => p.Lease != null && p.Lease.TenantId == tenantId.Value);
            }
            // Most overdue first so a forced single-tenant request picks the worst payment.
            var latePayments = await lateQuery
                .OrderBy(p => p.DueDate)
                .ToListAsync(ct);

            foreach (var payment in latePayments)
            {
                if (payment.Lease?.Tenant == null) continue;
                var daysLate = (today - payment.DueDate.Date).Days;
                if (!DraftExists(existingDrafts, payment.LeaseId, "LateRentNotice", created))
                {
                    created.Add(await BuildLateDraftAsync(portfolioId, payment, daysLate, now, ct));
                }
            }
        }

        if (created.Count > 0)
        {
            _db.NoticeDrafts.AddRange(created);
            await _db.SaveChangesAsync(ct);
        }

        return new GenerateNoticeDraftsResponse
        {
            CreatedCount = created.Count,
            Drafts = created.Select(Map).ToList()
        };
    }

    public async Task<NoticeDraftResponse?> UpdateAsync(
        int portfolioId,
        int id,
        UpdateNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        if (!string.IsNullOrWhiteSpace(request.Subject)) draft.Subject = request.Subject.Trim();
        if (!string.IsNullOrWhiteSpace(request.Body)) draft.Body = request.Body.Trim();
        draft.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    public async Task<NoticeDraftResponse?> ApproveAsync(
        int portfolioId,
        int id,
        ApproveNoticeDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        var channels = NormalizeChannels(request.Channels);
        if (channels.Count == 0) channels = ["Portal"];

        // Approving a notice IS a send → flows through the same Fair Housing gate as a direct message.
        // A flagged draft throws FairHousingBlockedException (→ 422) unless the landlord acknowledged
        // the review; the draft stays in "Draft" so they can revise and re-approve.
        var conversation = await _conversations.StartAsync(
            portfolioId,
            draft.TenantId,
            draft.Subject,
            draft.Body,
            channels,
            acknowledgedFairHousingReview: request.AcknowledgedFairHousingReview,
            ct: ct);
        if (conversation == null) return null;

        draft.Status = "Approved";
        draft.ApprovedAt = DateTime.UtcNow;
        draft.UpdatedAt = draft.ApprovedAt.Value;
        draft.ConversationId = conversation.Id;
        draft.ApprovedChannels = string.Join(",", channels);
        await _db.SaveChangesAsync(ct);

        return Map(draft);
    }

    public async Task<NoticeDraftResponse?> DismissAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var draft = await BaseQuery(portfolioId).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (draft == null || draft.Status != "Draft") return null;

        draft.Status = "Dismissed";
        draft.DismissedAt = DateTime.UtcNow;
        draft.UpdatedAt = draft.DismissedAt.Value;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    private IQueryable<NoticeDraft> BaseQuery(int portfolioId) =>
        _db.NoticeDrafts
            .Include(d => d.Tenant)
            .Include(d => d.Property)
            .Include(d => d.Lease).ThenInclude(l => l!.Unit)
            .Where(d => d.PortfolioId == portfolioId);

    /// <summary>
    /// True if an open (Draft-status) notice of this type already exists for the lease — either
    /// persisted or already queued this batch. Checked BEFORE composing copy so we never spend an
    /// LLM call (or create a duplicate) for a notice the landlord already has waiting.
    /// </summary>
    /// <summary>
    /// Idempotency check for an open notice of a given (lease, type): true if one was already created
    /// in THIS run (<paramref name="created"/>) or pre-existed in the DB. The persisted set is loaded
    /// once up front (<c>existingDrafts</c>), so this is an in-memory lookup — no per-iteration query.
    /// </summary>
    private static bool DraftExists(
        HashSet<(int LeaseId, string NoticeType)> existingDrafts,
        int leaseId, string noticeType, List<NoticeDraft> created)
    {
        if (existingDrafts.Contains((leaseId, noticeType)))
        {
            return true;
        }

        return created.Any(d =>
            d.LeaseId == leaseId &&
            d.NoticeType == noticeType &&
            d.Status == "Draft");
    }

    // ===========================================================================================
    // Proactive copy generation. Each builder computes the grounded facts, asks the LLM to write a
    // warm, professional subject + body from ONLY those facts, and falls back to a deterministic
    // template when the LLM is a no-op (no API key) or returns nothing usable. The exact prompt is
    // stored on the draft for audit/provenance.
    // ===========================================================================================

    private async Task<NoticeDraft> BuildRenewalDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, CancellationToken ct)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        // Renewal terms: escalate the current rent and extend the term by a year.
        var proposedRent = Math.Round(lease.MonthlyRent * (1 + RenewalEscalationPercent / 100m), 0, MidpointRounding.AwayFromZero);
        var newEndDate = lease.EndDate.Date.AddMonths(RenewalTermMonths);

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Current lease ends: {lease.EndDate:MMMM d, yyyy} ({daysToEnd} days away)\n" +
            $"- Current rent: {lease.MonthlyRent:C0}/month\n" +
            $"- Proposed new rent: {proposedRent:C0}/month (a {RenewalEscalationPercent:0.#}% increase)\n" +
            $"- Proposed new term: {RenewalTermMonths} months, new end date {newEndDate:MMMM d, yyyy}\n" +
            "- Ask the tenant to reply to accept, decline, or ask questions.";

        var deterministicSubject = $"Lease renewal for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, your current lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
            + $"We would like to offer a {RenewalTermMonths}-month renewal at {proposedRent:C0} per month "
            + $"(currently {lease.MonthlyRent:C0}), running through {newEndDate:MMMM d, yyyy}. "
            + "Please reply here to accept, decline, or ask any questions.";

        var draft = await ComposeAsync(
            portfolioId, lease, "RenewalOffer",
            intent: "a friendly lease-renewal offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days. Proposed {proposedRent:C0}/mo ({RenewalEscalationPercent:0.#}% increase) through {newEndDate:MMM d, yyyy}.",
            triggerDate: lease.EndDate.Date,
            now: now,
            ct: ct);

        return draft;
    }

    private async Task<NoticeDraft> BuildMoveOutDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, CancellationToken ct)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Lease ends: {lease.EndDate:MMMM d, yyyy} ({daysToEnd} days away)\n" +
            "- Coordinate: key return, the move-out inspection time, and a forwarding address for the deposit.\n" +
            "- Ask the tenant to reply to set up those details.";

        var deterministicSubject = $"Move-out reminder for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, this is a reminder that your lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
            + "Please reply to coordinate keys, inspection timing, and forwarding-address details so we can return your deposit promptly.";

        return await ComposeAsync(
            portfolioId, lease, "MoveOutReminder",
            intent: "a courteous move-out coordination reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days.",
            triggerDate: lease.EndDate.Date,
            now: now,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildLateDraftAsync(
        int portfolioId, Payment payment, int daysLate, DateTime now, CancellationToken ct)
    {
        var lease = payment.Lease!;
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        // Escalation: tone hardens the longer rent stays unpaid.
        var (level, levelLabel, tone) = LateEscalationLevel(daysLate);

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Amount due: {payment.Amount:C0}\n" +
            $"- Was due: {payment.DueDate:MMMM d, yyyy} ({daysLate} days ago)\n" +
            $"- This is the {levelLabel} reminder. {tone}\n" +
            "- Ask the tenant to reply with payment status or to arrange a plan.";

        var deterministicSubject = level switch
        {
            1 => $"Friendly reminder: rent past due for {propertyName}{unit}",
            2 => $"Second notice: rent {daysLate} days past due for {propertyName}{unit}",
            _ => $"Final notice: overdue rent for {propertyName}{unit}"
        };
        var deterministicBody = level switch
        {
            1 => $"Hi {tenantName}, our records show {payment.Amount:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} is now {daysLate} days past due. If you've already paid, thank you — "
                 + "please disregard. Otherwise, please reply with your payment status or any questions.",
            2 => $"Hi {tenantName}, this is a second reminder that {payment.Amount:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid ({daysLate} days past due). Please bring the balance current "
                 + "or reply so we can arrange a payment plan.",
            _ => $"Hi {tenantName}, this is a final notice that {payment.Amount:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid and is now {daysLate} days past due. Please pay in full "
                 + "immediately or contact us today to avoid further action under your lease."
        };

        return await ComposeAsync(
            portfolioId, lease, "LateRentNotice",
            intent: $"a {levelLabel} past-due rent reminder ({tone})",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Payment is {daysLate} days past due ({levelLabel} notice).",
            triggerDate: payment.DueDate.Date,
            now: now,
            ct: ct);
    }

    /// <summary>
    /// Late-rent escalation ladder driven by days past due. Returns (level 1/2/3, human label, tone hint).
    /// </summary>
    private static (int Level, string Label, string Tone) LateEscalationLevel(int daysLate) => daysLate switch
    {
        <= 7 => (1, "first", "Keep it warm and assume good faith — they may have simply forgotten."),
        <= 20 => (2, "second", "Be firm but polite; the balance still needs to be brought current."),
        _ => (3, "final", "Be serious and direct: this is the last reminder before lease remedies, while staying professional.")
    };

    private static string UnitSuffix(string? unitNumber) =>
        string.IsNullOrWhiteSpace(unitNumber) ? "" : $" Unit {unitNumber}";

    /// <summary>
    /// Builds the LLM prompt from grounded facts, asks for a subject+body JSON, and falls back to the
    /// deterministic template whenever the LLM is a no-op (empty/blank) or unparseable. The prompt is
    /// always stored on the draft; when the fallback is used, <see cref="NoticeDraft.GenerationPrompt"/>
    /// is left null to mark the copy as template-generated.
    /// </summary>
    private async Task<NoticeDraft> ComposeAsync(
        int portfolioId,
        Lease lease,
        string noticeType,
        string intent,
        string facts,
        string deterministicSubject,
        string deterministicBody,
        string reason,
        DateTime triggerDate,
        DateTime now,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(intent, facts);

        var subject = deterministicSubject;
        var body = deterministicBody;
        string? usedPrompt = null;

        try
        {
            var raw = await _llm.ChatAsync(prompt, ct);
            if (TryParseCopy(raw, out var llmSubject, out var llmBody))
            {
                subject = Truncate(llmSubject, 200);
                body = Truncate(llmBody, 4000);
                usedPrompt = Truncate(prompt, 8000);
            }
            // else: empty (no-op / no key) or malformed → keep deterministic template.
        }
        catch (Exception ex)
        {
            // Never let a copy-generation failure block the proactive draft — fall back to the template.
            _logger.LogWarning(ex, "LLM copy generation failed for {NoticeType} (lease {LeaseId}); using template.", noticeType, lease.Id);
        }

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = noticeType,
            Subject = subject,
            Body = body,
            Reason = reason,
            GenerationPrompt = usedPrompt,
            TriggerDate = triggerDate,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string BuildPrompt(string intent, string facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a property manager writing a short message to a tenant on behalf of the landlord.");
        sb.AppendLine($"Write {intent}.");
        sb.AppendLine();
        sb.AppendLine("Use ONLY these facts — do not invent names, amounts, dates, fees, or legal threats:");
        sb.AppendLine(facts);
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Warm, clear, professional. Plain language a non-lawyer tenant understands.");
        sb.AppendLine("- 2–4 short sentences in the body. No placeholders like [Name]; use the real tenant name.");
        sb.AppendLine("- Do not include a signature, contact block, or subject-line label inside the body.");
        sb.AppendLine("- Respond with ONLY a JSON object: {\"subject\": \"...\", \"body\": \"...\"}. No markdown, no extra text.");
        return sb.ToString();
    }

    /// <summary>
    /// Parses the model's <c>{"subject","body"}</c> JSON (tolerating ```json fences). Returns false on
    /// empty/blank input (the no-op fallback) or when either field is missing/blank.
    /// </summary>
    private static bool TryParseCopy(string? raw, out string subject, out string body)
    {
        subject = "";
        body = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        // Strip a ```json … ``` fence if the model added one.
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        // Be lenient: pull out the first {...} block if there's surrounding prose.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        text = text[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String)
                subject = s.GetString()?.Trim() ?? "";
            if (root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
                body = b.GetString()?.Trim() ?? "";
        }
        catch (JsonException)
        {
            return false;
        }

        return subject.Length > 0 && body.Length > 0;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static List<string> NormalizeChannels(List<string>? channels) =>
        (channels ?? [])
            .Select(c => c.Trim().ToLowerInvariant() switch
            {
                "portal" => "Portal",
                "email" => "Email",
                "sms" => "Sms",
                _ => ""
            })
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

    private static NoticeDraftResponse Map(NoticeDraft d)
    {
        var tenantName = d.Tenant == null ? "" : $"{d.Tenant.FirstName} {d.Tenant.LastName}".Trim();
        return new NoticeDraftResponse
        {
            Id = d.Id,
            LeaseId = d.LeaseId,
            TenantId = d.TenantId,
            PropertyId = d.PropertyId,
            TenantName = tenantName,
            PropertyName = d.Property?.Name,
            UnitNumber = d.Lease?.Unit?.UnitNumber,
            NoticeType = d.NoticeType,
            Status = d.Status,
            Subject = d.Subject,
            Body = d.Body,
            Reason = d.Reason,
            TriggerDate = d.TriggerDate,
            ConversationId = d.ConversationId,
            ApprovedChannels = d.ApprovedChannels,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            ApprovedAt = d.ApprovedAt,
            DismissedAt = d.DismissedAt
        };
    }
}
