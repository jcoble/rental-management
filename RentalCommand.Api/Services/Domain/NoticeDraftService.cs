using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NoticeDraftService : INoticeDraftService
{
    // Renewal terms: propose a modest escalation on the current rent for the new term.
    private const decimal RenewalEscalationPercent = 3.0m;
    private const int RenewalTermMonths = 12;
    // How many days before a scheduled rent payment's due date the autopilot generates a reminder.
    // Per-portfolio RentChargeLeadDays wiring is out of scope for Plan 3b Task 4 (see report).
    private const int RentReminderLeadDays = 7;
    private static readonly TimeSpan CopyGenerationTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly RentalCommandDbContext _db;
    private readonly IConversationService _conversations;
    private readonly ILlmProvider _llm;
    private readonly ILogger<NoticeDraftService> _logger;
    private readonly TimeProvider _timeProvider;

    public NoticeDraftService(
        RentalCommandDbContext db,
        IConversationService conversations,
        ILlmProvider llm,
        ILogger<NoticeDraftService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _conversations = conversations;
        _llm = llm;
        _logger = logger;
        _timeProvider = timeProvider;
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
        var now = _timeProvider.UtcNow();
        var today = now.Date;
        var created = new List<NoticeDraft>();

        var tenantId = request?.TenantId;
        var leaseId = request?.LeaseId;
        var paymentId = request?.PaymentId;
        var requestedType = string.IsNullOrWhiteSpace(request?.NoticeType) ? null : request!.NoticeType!.Trim();
        // When a specific type is requested for a tenant, force renewal/move-out even outside the
        // usual trigger window — the landlord explicitly asked for that notice.
        var forced = requestedType != null;

        bool WantsType(string type) => requestedType == null || string.Equals(requestedType, type, StringComparison.OrdinalIgnoreCase);
        var wantsRenewal = WantsType("RenewalOffer");
        var wantsMoveOut = WantsType("MoveOutReminder");
        // A tenant-page explicit RentReminder remains the Plan-1 manual flow. Lease/payment scoped
        // reminders are payment-grounded so the draft has a real due date and cannot leak another
        // lease's candidate into the current lease tab.
        var rentReminderRequested = string.Equals(requestedType, "RentReminder", StringComparison.OrdinalIgnoreCase);
        var wantsRentReminder = rentReminderRequested && !leaseId.HasValue && !paymentId.HasValue;
        var wantsUpcomingRentReminder = requestedType == null || (rentReminderRequested && leaseId.HasValue);
        // MonthToMonth is generated portfolio-wide too (within the lease-end window) so the autopilot can
        // auto-send it; it mirrors renewal's lead time.
        var wantsMonthToMonth = requestedType == null || string.Equals(requestedType, "MonthToMonthConversion", StringComparison.OrdinalIgnoreCase);

        // Preload this portfolio's active notice templates once (DB-side), keyed by type, so each
        // builder can render the landlord's template without a per-lease query.
        var activeTemplates = await _db.NoticeTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.IsActive)
            .ToDictionaryAsync(t => t.NoticeType, ct);

        NoticeTemplate? TemplateFor(string type) =>
            activeTemplates.TryGetValue(type, out var t) ? t : null;

        // Load the portfolio name once so {{portfolio_name}} fills correctly in all templates.
        var portfolioName = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? "";

        if (paymentId.HasValue)
        {
            return await GeneratePaymentScopedAsync(
                portfolioId,
                paymentId.Value,
                tenantId,
                leaseId,
                requestedType,
                now,
                today,
                TemplateFor,
                portfolioName,
                ct);
        }

        if (wantsRenewal || wantsMoveOut || wantsRentReminder || wantsMonthToMonth)
        {
            var leaseBaseQuery = _db.Leases
                .Include(l => l.Tenant)
                .Include(l => l.Property)
                .Include(l => l.Unit)
                .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active);
            if (tenantId.HasValue)
            {
                leaseBaseQuery = leaseBaseQuery.Where(l => l.TenantId == tenantId.Value);
            }
            if (leaseId.HasValue)
            {
                leaseBaseQuery = leaseBaseQuery.Where(l => l.Id == leaseId.Value);
            }

            if (wantsRenewal)
            {
                var renewalQuery = leaseBaseQuery
                    .Where(l => forced || (l.EndDate >= today && l.EndDate < today.AddDays(76)))
                    .Where(l => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseId == l.Id &&
                        d.NoticeType == "RenewalOffer" &&
                        d.Status == "Draft"));

                var renewalLeases = await renewalQuery
                    .OrderBy(l => l.EndDate)
                    .ThenBy(l => l.Id)
                    .ToListAsync(ct);

                foreach (var lease in renewalLeases)
                {
                    if (lease.Tenant == null) continue;

                    var daysToEnd = (lease.EndDate.Date - today).Days;
                    created.Add(await BuildRenewalDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("RenewalOffer"), portfolioName, ct));
                }
            }

            if (wantsMoveOut)
            {
                var moveOutQuery = leaseBaseQuery
                    .Where(l => forced || (l.EndDate >= today && l.EndDate < today.AddDays(31)))
                    .Where(l => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseId == l.Id &&
                        d.NoticeType == "MoveOutReminder" &&
                        d.Status == "Draft"));

                var moveOutLeases = await moveOutQuery
                    .OrderBy(l => l.EndDate)
                    .ThenBy(l => l.Id)
                    .ToListAsync(ct);

                foreach (var lease in moveOutLeases)
                {
                    if (lease.Tenant == null) continue;

                    var daysToEnd = (lease.EndDate.Date - today).Days;
                    created.Add(await BuildMoveOutDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("MoveOutReminder"), portfolioName, ct));
                }
            }

            if (wantsRentReminder)
            {
                var reminderLeases = await leaseBaseQuery
                    .Where(l => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseId == l.Id &&
                        d.NoticeType == "RentReminder" &&
                        d.Status == "Draft"))
                    .OrderBy(l => l.EndDate)
                    .ThenBy(l => l.Id)
                    .ToListAsync(ct);

                foreach (var lease in reminderLeases)
                {
                    if (lease.Tenant == null) continue;

                    created.Add(await BuildRentReminderDraftAsync(portfolioId, lease, now, TemplateFor("RentReminder"), portfolioName, ct));
                }
            }

            if (wantsMonthToMonth)
            {
                var monthToMonthQuery = leaseBaseQuery
                    .Where(l => forced || (l.EndDate >= today && l.EndDate < today.AddDays(76)))
                    .Where(l => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseId == l.Id &&
                        d.NoticeType == "MonthToMonthConversion" &&
                        d.Status == "Draft"));

                var monthToMonthLeases = await monthToMonthQuery
                    .OrderBy(l => l.EndDate)
                    .ThenBy(l => l.Id)
                    .ToListAsync(ct);

                foreach (var lease in monthToMonthLeases)
                {
                    if (lease.Tenant == null) continue;

                    var daysToEnd = (lease.EndDate.Date - today).Days;
                    created.Add(await BuildMonthToMonthDraftAsync(portfolioId, lease, daysToEnd, now, TemplateFor("MonthToMonthConversion"), portfolioName, ct));
                }
            }
        }

        // Late-rent notices are always grounded in a real overdue payment (we need the amount/due
        // date), so even a forced request only produces one when such a payment exists.
        if (WantsType("LateRentNotice"))
        {
            var lateQuery = _db.Payments
                .AsNoTracking()
                .ForCurrentLeaseAttention(today)
                .Where(p =>
                    p.PortfolioId == portfolioId &&
                    p.DueDate < today &&
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial) &&
                    (p.PaymentType == PaymentType.Rent || p.PaymentType == PaymentType.LateFee) &&
                    !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseId == p.LeaseId &&
                        d.NoticeType == "LateRentNotice" &&
                        d.Status == "Draft"));
            if (tenantId.HasValue)
            {
                lateQuery = lateQuery.Where(p => p.Lease != null && p.Lease.TenantId == tenantId.Value);
            }
            if (leaseId.HasValue)
            {
                lateQuery = lateQuery.Where(p => p.LeaseId == leaseId.Value);
            }

            // Pick one overdue row per lease in SQL so a rent row and its late-fee row do not create
            // duplicate-looking late notices for the same lease context. Prefer the substantive rent
            // balance over older prorates or standalone late-fee rows, then fold same-period fees into it.
            var latePaymentIds = await lateQuery
                .GroupBy(p => p.LeaseId)
                .Select(g => g
                    .OrderBy(p => p.PaymentType == PaymentType.Rent ? 0 : 1)
                    .ThenByDescending(p => p.Status == PaymentStatus.Partial
                        ? p.Amount - (p.AmountPaid ?? 0m)
                        : p.Amount)
                    .ThenByDescending(p => p.DueDate)
                    .ThenBy(p => p.Id)
                    .Select(p => p.Id)
                    .First())
                .ToListAsync(ct);

            var lateCandidates = await _db.Payments
                .AsNoTracking()
                .Include(p => p.Lease).ThenInclude(l => l!.Tenant)
                .Include(p => p.Lease).ThenInclude(l => l!.Property)
                .Include(p => p.Lease).ThenInclude(l => l!.Unit)
                .Where(p => latePaymentIds.Contains(p.Id))
                .Select(p => new
                {
                    Payment = p,
                    RelatedLateFeeAmount = p.PaymentType == PaymentType.Rent && p.PeriodKey != null
                        ? _db.Payments
                            .Where(f =>
                                f.PortfolioId == portfolioId &&
                                f.LeaseId == p.LeaseId &&
                                f.PaymentType == PaymentType.LateFee &&
                                f.PeriodKey == p.PeriodKey &&
                                (f.Status == PaymentStatus.Scheduled || f.Status == PaymentStatus.Late || f.Status == PaymentStatus.Partial))
                            .Sum(f => (decimal?)(f.Status == PaymentStatus.Partial
                                ? f.Amount - (f.AmountPaid ?? 0m)
                                : f.Amount)) ?? 0m
                        : 0m,
                })
                .OrderBy(c => c.Payment.DueDate)
                .ToListAsync(ct);

            foreach (var candidate in lateCandidates)
            {
                var payment = candidate.Payment;
                if (payment.Lease?.Tenant == null) continue;
                var daysLate = (today - payment.DueDate.Date).Days;
                created.Add(await BuildLateDraftAsync(
                    portfolioId,
                    payment,
                    daysLate,
                    now,
                    TemplateFor("LateRentNotice"),
                    portfolioName,
                    ct,
                    relatedLateFeeAmount: candidate.RelatedLateFeeAmount));
            }
        }

        // Periodic, payment-grounded rent reminders: one per active in-portfolio lease with a scheduled
        // rent payment due within the lead window. Per-period idempotent (keyed on the payment's due date)
        // so monthly reminders recur but a given period's reminder is never duplicated/recreated.
        if (wantsUpcomingRentReminder)
        {
            var leadWindowEndExclusive = today.AddDays(RentReminderLeadDays + 1);
            var upcomingQuery = _db.Payments
                .AsNoTracking()
                .Where(p =>
                    p.PortfolioId == portfolioId &&
                    p.PaymentType == PaymentType.Rent &&
                    p.Status == PaymentStatus.Scheduled &&
                    p.DueDate >= today &&
                    p.DueDate < leadWindowEndExclusive &&
                    p.Lease != null &&
                    p.Lease.Status == LeaseStatus.Active &&
                    !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.NoticeType == "RentReminder" &&
                        ((d.PaymentId != null && d.PaymentId == p.Id) ||
                         (d.PaymentId == null && d.LeaseId == p.LeaseId && d.TriggerDate == p.DueDate.Date))));
            if (tenantId.HasValue)
            {
                upcomingQuery = upcomingQuery.Where(p => p.Lease != null && p.Lease.TenantId == tenantId.Value);
            }
            if (leaseId.HasValue)
            {
                upcomingQuery = upcomingQuery.Where(p => p.LeaseId == leaseId.Value);
            }

            var upcomingPaymentIds = await upcomingQuery
                .GroupBy(p => new { p.LeaseId, p.DueDate })
                .Select(g => g.Min(p => p.Id))
                .ToListAsync(ct);

            var upcomingPayments = await _db.Payments
                .AsNoTracking()
                .Include(p => p.Lease).ThenInclude(l => l!.Tenant)
                .Include(p => p.Lease).ThenInclude(l => l!.Property)
                .Include(p => p.Lease).ThenInclude(l => l!.Unit)
                .Where(p => upcomingPaymentIds.Contains(p.Id))
                .OrderBy(p => p.DueDate)
                .ThenBy(p => p.LeaseId)
                .ToListAsync(ct);

            foreach (var payment in upcomingPayments)
            {
                if (payment.Lease?.Tenant == null) continue;
                var dueDate = payment.DueDate.Date;
                created.Add(await BuildRentReminderDraftAsync(
                    portfolioId, payment.Lease, now, TemplateFor("RentReminder"), portfolioName, ct, dueDate, payment.Id));
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
            Drafts = await BuildGenerateResponseDraftsAsync(portfolioId, tenantId, leaseId, null, requestedType, created, ct)
        };
    }

    private async Task<GenerateNoticeDraftsResponse> GeneratePaymentScopedAsync(
        int portfolioId,
        int paymentId,
        int? tenantId,
        int? leaseId,
        string? requestedType,
        DateTime now,
        DateTime today,
        Func<string, NoticeTemplate?> templateFor,
        string portfolioName,
        CancellationToken ct)
    {
        var payment = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Lease).ThenInclude(l => l!.Tenant)
            .Include(p => p.Lease).ThenInclude(l => l!.Property)
            .Include(p => p.Lease).ThenInclude(l => l!.Unit)
            .Where(p => p.PortfolioId == portfolioId && p.Id == paymentId)
            .FirstOrDefaultAsync(ct);

        if (payment?.Lease?.Tenant == null)
        {
            return new GenerateNoticeDraftsResponse();
        }

        if (leaseId.HasValue && payment.LeaseId != leaseId.Value)
        {
            throw new DomainValidationException("Payment does not belong to the selected lease.");
        }

        if (tenantId.HasValue && payment.Lease.TenantId != tenantId.Value)
        {
            throw new DomainValidationException("Payment does not belong to the selected tenant.");
        }

        var noticeType = requestedType;
        if (string.IsNullOrWhiteSpace(noticeType))
        {
            noticeType = InferPaymentNoticeType(payment, today);
        }

        if (string.IsNullOrWhiteSpace(noticeType) || !IsEligibleForPaymentNotice(payment, noticeType, today))
        {
            return new GenerateNoticeDraftsResponse
            {
                Drafts = await BuildGenerateResponseDraftsAsync(
                    portfolioId,
                    payment.Lease.TenantId,
                    payment.LeaseId,
                    payment.Id,
                    noticeType,
                    [],
                    ct)
            };
        }

        var existing = await PaymentScopedExistingDraftsQuery(portfolioId, payment, noticeType)
            .OrderBy(d => d.Status == "Draft" ? 0 : 1)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            return new GenerateNoticeDraftsResponse
            {
                CreatedCount = 0,
                Drafts = existing.Select(Map).ToList()
            };
        }

        NoticeDraft draft;
        if (string.Equals(noticeType, "RentReminder", StringComparison.OrdinalIgnoreCase))
        {
            draft = await BuildRentReminderDraftAsync(
                portfolioId,
                payment.Lease,
                now,
                templateFor("RentReminder"),
                portfolioName,
                ct,
                payment.DueDate.Date,
                payment.Id);
        }
        else
        {
            var relatedLateFeeAmount = await RelatedLateFeeOutstandingAsync(portfolioId, payment, ct);
            draft = await BuildLateDraftAsync(
                portfolioId,
                payment,
                Math.Max(0, (today - payment.DueDate.Date).Days),
                now,
                templateFor("LateRentNotice"),
                portfolioName,
                ct,
                relatedLateFeeAmount,
                payment.Id);
        }

        _db.NoticeDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return new GenerateNoticeDraftsResponse
        {
            CreatedCount = 1,
            Drafts = [Map(draft)]
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
        draft.UpdatedAt = _timeProvider.UtcNow();
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
            operationKey: $"notice-draft:{draft.Id}:approve-conversation",
            acknowledgedFairHousingReview: request.AcknowledgedFairHousingReview,
            ct: ct);
        if (conversation == null) return null;

        draft.Status = "Approved";
        draft.ApprovedAt = _timeProvider.UtcNow();
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
        draft.DismissedAt = _timeProvider.UtcNow();
        draft.UpdatedAt = draft.DismissedAt.Value;
        await _db.SaveChangesAsync(ct);
        return Map(draft);
    }

    private IQueryable<NoticeDraft> PaymentScopedExistingDraftsQuery(
        int portfolioId,
        Payment payment,
        string noticeType)
    {
        var triggerDate = payment.DueDate.Date;
        return BaseQuery(portfolioId)
            .Where(d =>
                d.NoticeType == noticeType &&
                (d.Status == "Draft" || d.Status == "Approved") &&
                ((d.PaymentId != null && d.PaymentId == payment.Id) ||
                 (d.PaymentId == null &&
                  d.LeaseId == payment.LeaseId &&
                  d.TriggerDate == triggerDate)));
    }

    private static string? InferPaymentNoticeType(Payment payment, DateTime today)
    {
        if (IsEligibleForPaymentNotice(payment, "LateRentNotice", today))
        {
            return "LateRentNotice";
        }

        if (IsEligibleForPaymentNotice(payment, "RentReminder", today))
        {
            return "RentReminder";
        }

        return null;
    }

    private static bool IsEligibleForPaymentNotice(Payment payment, string noticeType, DateTime today)
    {
        var activeCharge = payment.Status is PaymentStatus.Scheduled or PaymentStatus.Late or PaymentStatus.Partial;
        if (!activeCharge)
        {
            return false;
        }

        if (string.Equals(noticeType, "RentReminder", StringComparison.OrdinalIgnoreCase))
        {
            return payment.PaymentType == PaymentType.Rent &&
                   payment.Status == PaymentStatus.Scheduled &&
                   payment.DueDate >= today;
        }

        if (string.Equals(noticeType, "LateRentNotice", StringComparison.OrdinalIgnoreCase))
        {
            return (payment.PaymentType == PaymentType.Rent || payment.PaymentType == PaymentType.LateFee) &&
                   (payment.Status is PaymentStatus.Late or PaymentStatus.Partial || payment.DueDate < today);
        }

        return false;
    }

    private async Task<decimal> RelatedLateFeeOutstandingAsync(
        int portfolioId,
        Payment payment,
        CancellationToken ct)
    {
        if (payment.PaymentType == PaymentType.LateFee)
        {
            return OutstandingAmount(payment);
        }

        if (payment.PaymentType != PaymentType.Rent || string.IsNullOrWhiteSpace(payment.PeriodKey))
        {
            return 0m;
        }

        return await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.LeaseId == payment.LeaseId &&
                p.PaymentType == PaymentType.LateFee &&
                p.PeriodKey == payment.PeriodKey &&
                (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late || p.Status == PaymentStatus.Partial))
            .SumAsync(p => (decimal?)(p.Status == PaymentStatus.Partial
                ? p.Amount - (p.AmountPaid ?? 0m)
                : p.Amount), ct) ?? 0m;
    }

    private static decimal OutstandingAmount(Payment payment) =>
        payment.Status == PaymentStatus.Partial
            ? Math.Max(0m, payment.Amount - (payment.AmountPaid ?? 0m))
            : payment.Amount;

    private IQueryable<NoticeDraft> BaseQuery(int portfolioId) =>
        _db.NoticeDrafts
            .Include(d => d.Tenant)
            .Include(d => d.Property)
            .Include(d => d.Payment)
            .Include(d => d.Lease).ThenInclude(l => l!.Unit)
            .Where(d => d.PortfolioId == portfolioId);

    private async Task<IReadOnlyList<NoticeDraftResponse>> BuildGenerateResponseDraftsAsync(
        int portfolioId,
        int? tenantId,
        int? leaseId,
        int? paymentId,
        string? requestedType,
        List<NoticeDraft> created,
        CancellationToken ct)
    {
        if (!tenantId.HasValue && !leaseId.HasValue && !paymentId.HasValue)
        {
            return created.Select(Map).ToList();
        }

        var query = BaseQuery(portfolioId);

        if (paymentId.HasValue)
        {
            query = query.Where(d => d.PaymentId == paymentId.Value && (d.Status == "Draft" || d.Status == "Approved"));
        }
        else
        {
            query = query.Where(d => d.Status == "Draft");
        }

        if (tenantId.HasValue)
        {
            query = query.Where(d => d.TenantId == tenantId.Value);
        }

        if (leaseId.HasValue)
        {
            query = query.Where(d => d.LeaseId == leaseId.Value);
        }

        if (!string.IsNullOrWhiteSpace(requestedType))
        {
            query = query.Where(d => d.NoticeType == requestedType);
        }

        var drafts = await query
            .OrderBy(d => d.TriggerDate)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return drafts.Select(Map).ToList();
    }

    // ===========================================================================================
    // Proactive copy generation. Each builder computes the grounded facts, asks the LLM to write a
    // warm, professional subject + body from ONLY those facts, and falls back to a deterministic
    // template when the LLM is a no-op (no API key) or returns nothing usable. The exact prompt is
    // stored on the draft for audit/provenance.
    // ===========================================================================================

    private async Task<NoticeDraft> BuildRenewalDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
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

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        var draft = await ComposeAsync(
            portfolioId, lease, "RenewalOffer",
            intent: "a friendly lease-renewal offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days. Proposed {proposedRent:C0}/mo ({RenewalEscalationPercent:0.#}% increase) through {newEndDate:MMM d, yyyy}.",
            triggerDate: lease.EndDate.Date,
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);

        return draft;
    }

    private async Task<NoticeDraft> BuildMoveOutDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
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

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, lease, "MoveOutReminder",
            intent: "a courteous move-out coordination reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days.",
            triggerDate: lease.EndDate.Date,
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildLateDraftAsync(
        int portfolioId,
        Payment payment,
        int daysLate,
        DateTime now,
        NoticeTemplate? template,
        string portfolioName,
        CancellationToken ct,
        decimal relatedLateFeeAmount = 0m,
        int? paymentId = null)
    {
        var lease = payment.Lease!;
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();
        var baseOutstanding = OutstandingAmount(payment);
        var lateFeeAmount = payment.PaymentType == PaymentType.LateFee ? baseOutstanding : relatedLateFeeAmount;
        var totalDue = payment.PaymentType == PaymentType.Rent ? baseOutstanding + lateFeeAmount : baseOutstanding;
        var chargeLabel = payment.PaymentType == PaymentType.LateFee ? "late fee" : "rent";
        var lateFeeClause = lateFeeAmount > 0m && payment.PaymentType == PaymentType.Rent
            ? $" This includes {lateFeeAmount:C0} in late fees."
            : "";

        // Escalation: tone hardens the longer rent stays unpaid.
        var (level, levelLabel, tone) = LateEscalationLevel(daysLate);

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Charge type: {chargeLabel}\n" +
            $"- Amount due: {totalDue:C0}\n" +
            (lateFeeAmount > 0m ? $"- Late fee amount: {lateFeeAmount:C0}\n" : "") +
            $"- Was due: {payment.DueDate:MMMM d, yyyy} ({daysLate} days ago)\n" +
            $"- This is the {levelLabel} reminder. {tone}\n" +
            "- Ask the tenant to reply with payment status or to arrange a plan.";

        var deterministicSubject = payment.PaymentType == PaymentType.LateFee
            ? $"Late fee notice for {propertyName}{unit}"
            : level switch
            {
                1 => $"Friendly reminder: rent past due for {propertyName}{unit}",
                2 => $"Second notice: rent {daysLate} days past due for {propertyName}{unit}",
                _ => $"Final notice: overdue rent for {propertyName}{unit}"
            };
        var deterministicBody = payment.PaymentType == PaymentType.LateFee
            ? $"Hi {tenantName}, our records show a late fee of {totalDue:C0} due on {payment.DueDate:MMMM d, yyyy} "
              + $"for {propertyName}{unit} remains unpaid. Please reply with your payment status or any questions."
            : level switch
        {
            1 => $"Hi {tenantName}, our records show {totalDue:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} is now {daysLate} days past due.{lateFeeClause} If you've already paid, thank you - "
                 + "please disregard. Otherwise, please reply with your payment status or any questions.",
            2 => $"Hi {tenantName}, this is a second reminder that {totalDue:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid ({daysLate} days past due).{lateFeeClause} Please bring the balance current "
                 + "or reply so we can arrange a payment plan.",
            _ => $"Hi {tenantName}, this is a final notice that {totalDue:C0} due on {payment.DueDate:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid and is now {daysLate} days past due.{lateFeeClause} Please pay in full "
                 + "immediately or contact us today to avoid further action under your lease."
        };

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.OverdueAmount, totalDue.ToString("C0")),
             (NoticeMergeFields.RentDueDate, payment.DueDate.ToString("MMMM d, yyyy")),
             (NoticeMergeFields.LateFeeAmount, lateFeeAmount > 0m ? lateFeeAmount.ToString("C0") : ""),
             (NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, lease, "LateRentNotice",
            intent: $"a {levelLabel} past-due rent reminder ({tone})",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: lateFeeAmount > 0m && payment.PaymentType == PaymentType.Rent
                ? $"Payment is {daysLate} days past due ({levelLabel} notice), including {lateFeeAmount:C0} in late fees."
                : $"{chargeLabel} payment is {daysLate} days past due ({levelLabel} notice).",
            triggerDate: payment.DueDate.Date,
            now: now,
            template: template,
            tokens: tokens,
            paymentId: paymentId,
            ct: ct);
    }

    /// <summary>
    /// Builds an upcoming-rent reminder. When <paramref name="dueDate"/> is supplied (the periodic,
    /// payment-grounded path) the draft's <see cref="NoticeDraft.TriggerDate"/> and the
    /// <c>rent_due_date</c> token are set to that date; on the explicit/forced path with no upcoming
    /// payment, <paramref name="dueDate"/> is null → TriggerDate falls back to today and the token blanks.
    /// </summary>
    private async Task<NoticeDraft> BuildRentReminderDraftAsync(
        int portfolioId, Lease lease, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct,
        DateTime? dueDate = null,
        int? paymentId = null)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        var dueOn = dueDate?.Date;
        var dueClause = dueOn.HasValue ? $" on {dueOn:MMMM d, yyyy}" : "";

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Monthly rent: {lease.MonthlyRent:C0}\n" +
            (dueOn.HasValue ? $"- Rent due date: {dueOn:MMMM d, yyyy}\n" : "") +
            "- This is a friendly heads-up that rent is coming due.\n" +
            "- Ask the tenant to reply with any questions.";

        var deterministicSubject = $"Rent reminder for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, this is a friendly reminder that your rent of {lease.MonthlyRent:C0} "
            + $"for {propertyName}{unit} is coming due{dueClause}. Please reach out with any questions. Thank you!";

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.RentDueDate, dueOn.HasValue ? dueOn.Value.ToString("MMMM d, yyyy") : ""),
             (NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, lease, "RentReminder",
            intent: "a friendly upcoming-rent reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: dueOn.HasValue ? $"Rent due {dueOn:MMM d, yyyy}." : "Manual rent reminder.",
            triggerDate: dueOn ?? now.Date,
            now: now,
            template: template,
            tokens: tokens,
            paymentId: paymentId,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildMonthToMonthDraftAsync(
        int portfolioId, Lease lease, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
    {
        var tenant = lease.Tenant!;
        var propertyName = lease.Property?.Name ?? "your home";
        var unit = UnitSuffix(lease.Unit?.UnitNumber);
        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Current lease ends: {lease.EndDate:MMMM d, yyyy}\n" +
            $"- Current rent: {lease.MonthlyRent:C0}/month\n" +
            "- Offer to continue on a month-to-month basis at the current rent after the lease ends.\n" +
            "- Ask the tenant to reply to accept or ask questions.";

        var deterministicSubject = $"Month-to-month option for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, your lease for {propertyName}{unit} ends on {lease.EndDate:MMMM d, yyyy}. "
            + $"We're happy to continue on a month-to-month basis at {lease.MonthlyRent:C0} per month. "
            + "Please reply to accept or with any questions.";

        var tokens = BuildTokens(lease, tenantName, propertyName, lease.Unit?.UnitNumber,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, lease, "MonthToMonthConversion",
            intent: "a friendly month-to-month continuation offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: "Manual month-to-month conversion offer.",
            triggerDate: lease.EndDate.Date,
            now: now,
            template: template,
            tokens: tokens,
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
    /// deterministic template whenever the LLM is a no-op (empty/blank) or unparseable. When a landlord
    /// <paramref name="template"/> is present it takes full precedence — the LLM is skipped and
    /// <see cref="NoticeDraft.GenerationPrompt"/> is left null to mark non-LLM provenance.
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
        NoticeTemplate? template,
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken ct,
        int? paymentId = null)
    {
        var subject = deterministicSubject;
        var body = deterministicBody;
        string? usedPrompt = null;

        // Try the landlord template first; fall through to LLM/deterministic copy if the rendered
        // result is blank (e.g. an empty template that bypassed the service guard).
        var templateTaken = false;
        if (template != null)
        {
            var rendered = NoticeTemplateRenderer.Render(template.Subject, template.Body, tokens);
            var renderedSubject = Truncate(rendered.Subject, 200);
            var renderedBody = Truncate(rendered.Body, 4000);
            if (!string.IsNullOrWhiteSpace(renderedSubject) && !string.IsNullOrWhiteSpace(renderedBody))
            {
                // Landlord template takes precedence. No LLM call; GenerationPrompt stays null to mark non-LLM provenance.
                subject = renderedSubject;
                body = renderedBody;
                templateTaken = true;
            }
        }

        if (!templateTaken)
        {
            var prompt = BuildPrompt(intent, facts);
            try
            {
                var raw = await GenerateCopyAsync(prompt, noticeType, lease.Id, ct);
                if (TryParseCopy(raw, out var llmSubject, out var llmBody))
                {
                    subject = Truncate(llmSubject, 200);
                    body = Truncate(llmBody, 4000);
                    usedPrompt = Truncate(prompt, 8000);
                }
                // else: empty (no-op / no key) or malformed → keep deterministic template.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never let a copy-generation failure block the proactive draft — fall back to the template.
                _logger.LogWarning(ex, "LLM copy generation failed for {NoticeType} (lease {LeaseId}); using template.", noticeType, lease.Id);
            }
        }

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseId = lease.Id,
            PaymentId = paymentId,
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

    /// <summary>
    /// Builds the merge-token values for a draft from grounded facts. Only includes tokens that have a
    /// real value; the renderer blanks anything missing. Currency/dates are pre-formatted strings.
    /// </summary>
    private static Dictionary<string, string> BuildTokens(
        Lease lease,
        string tenantName,
        string propertyName,
        string? unitNumber,
        (string Key, string Value)[] extra)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NoticeMergeFields.TenantName] = tenantName,
            [NoticeMergeFields.PropertyAddress] = propertyName,
            [NoticeMergeFields.UnitNumber] = unitNumber ?? "",
            [NoticeMergeFields.LeaseStartDate] = lease.StartDate.ToString("MMMM d, yyyy"),
            [NoticeMergeFields.LeaseEndDate] = lease.EndDate.ToString("MMMM d, yyyy"),
            [NoticeMergeFields.RentAmount] = lease.MonthlyRent.ToString("C0"),
        };
        foreach (var (key, value) in extra) tokens[key] = value;
        return tokens;
    }

    private async Task<string?> GenerateCopyAsync(
        string prompt,
        string noticeType,
        int leaseId,
        CancellationToken ct)
    {
        using var copyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var chatTask = _llm.ChatAsync(prompt, copyCts.Token);
        var timeoutTask = Task.Delay(CopyGenerationTimeout, CancellationToken.None);
        var completed = await Task.WhenAny(chatTask, timeoutTask);
        ct.ThrowIfCancellationRequested();

        if (completed == chatTask)
        {
            return await chatTask;
        }

        await copyCts.CancelAsync();
        _ = chatTask.ContinueWith(t =>
        {
            _ = t.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);
        _logger.LogInformation(
            "LLM copy generation timed out for {NoticeType} (lease {LeaseId}); using template.",
            noticeType,
            leaseId);
        return null;
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
            PaymentId = d.PaymentId,
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
