using System.Globalization;
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
    // How many days before an open rent charge's due date the autopilot generates a reminder.
    // Per-portfolio RentChargeLeadDays wiring is out of scope for Plan 3b Task 4 (see report).
    private const int RentReminderLeadDays = 7;
    private static readonly TimeSpan CopyGenerationTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly ILogger<NoticeDraftService> _logger;
    private readonly TimeProvider _timeProvider;

    public NoticeDraftService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        ILogger<NoticeDraftService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
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
        var today = DateOnly.FromDateTime(now);
        var created = new List<NoticeDraft>();

        var recipientTenantId = request?.RecipientTenantId;
        var leaseManagementId = request?.LeaseManagementId;
        var tenantAccountId = request?.TenantAccountId;
        var tenantLedgerEntryId = request?.TenantLedgerEntryId;
        var requestedType = string.IsNullOrWhiteSpace(request?.NoticeType) ? null : request!.NoticeType!.Trim();
        // When a specific type is requested for a tenant, force renewal/move-out even outside the
        // usual trigger window — the landlord explicitly asked for that notice.
        var forced = requestedType != null;

        var policies = await _db.TenantNoticePolicies.AsNoTracking()
            .Where(policy => policy.PortfolioId == portfolioId && policy.Mode != TenantNoticeMode.Off)
            .ToDictionaryAsync(policy => policy.AutomationKey, StringComparer.OrdinalIgnoreCase, ct);
        if (requestedType is not null && !policies.ContainsKey(requestedType))
            throw new InvalidOperationException("This tenant notice automation is disabled or has no policy.");

        bool WantsType(string type) => policies.ContainsKey(type) &&
            (requestedType == null || string.Equals(requestedType, type, StringComparison.OrdinalIgnoreCase));
        var wantsRenewal = WantsType("lease-renewal-offer");
        var wantsMoveOut = WantsType("lease-non-renewal");
        // A tenant-page explicit rent reminder remains the manual flow. Relationship/ledger-scoped
        // reminders are charge-grounded so the draft has a real due date and cannot leak another
        // tenant account's candidate into the current relationship.
        var rentReminderRequested = string.Equals(requestedType, "rent-reminder", StringComparison.OrdinalIgnoreCase);
        var wantsRentReminder = rentReminderRequested &&
            !leaseManagementId.HasValue && !tenantAccountId.HasValue && !tenantLedgerEntryId.HasValue;
        var wantsUpcomingRentReminder = requestedType == null ||
            (rentReminderRequested && (leaseManagementId.HasValue || tenantAccountId.HasValue));
        // MonthToMonth is generated portfolio-wide too (within the lease-end window) so the autopilot can
        // auto-send it; it mirrors renewal's lead time.
        var wantsMonthToMonth = requestedType == null || string.Equals(requestedType, "month-to-month-offer", StringComparison.OrdinalIgnoreCase);

        // Preload this portfolio's active notice templates once (DB-side), keyed by type, so each
        // builder can render the landlord's template without a per-lease query.
        var activeTemplateIds = policies.Values.Select(policy => policy.WorkspaceNoticeTemplateVersionId).ToArray();
        var activeTemplates = await _db.WorkspaceNoticeTemplateVersions
            .AsNoTracking()
            .Where(template => template.PortfolioId == portfolioId && activeTemplateIds.Contains(template.Id))
            .ToDictionaryAsync(template => template.Id, ct);

        NoticeTemplate? TemplateFor(string type) =>
            policies.TryGetValue(type, out var policy) && activeTemplates.TryGetValue(policy.WorkspaceNoticeTemplateVersionId, out var template)
                ? new NoticeTemplate { NoticeType = type, Subject = template.Subject, Body = template.Body }
                : null;

        // Load the portfolio name once so {{portfolio_name}} fills correctly in all templates.
        var portfolioName = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? "";

        if (tenantLedgerEntryId.HasValue)
        {
            return await GenerateLedgerEntryScopedAsync(
                portfolioId,
                tenantLedgerEntryId.Value,
                recipientTenantId,
                leaseManagementId,
                tenantAccountId,
                requestedType,
                now,
                today,
                TemplateFor,
                portfolioName,
                ct);
        }

        if (wantsRenewal || wantsMoveOut || wantsRentReminder || wantsMonthToMonth)
        {
            var relationshipBaseQuery = ActiveNoticeContextQuery(portfolioId);
            if (recipientTenantId.HasValue)
            {
                relationshipBaseQuery = relationshipBaseQuery.Where(row => row.RecipientTenantId == recipientTenantId.Value);
            }
            if (leaseManagementId.HasValue)
            {
                relationshipBaseQuery = relationshipBaseQuery.Where(row => row.LeaseManagementId == leaseManagementId.Value);
            }
            if (tenantAccountId.HasValue)
            {
                relationshipBaseQuery = relationshipBaseQuery.Where(row => row.TenantAccountId == tenantAccountId.Value);
            }

            if (wantsRenewal)
            {
                var renewalQuery = relationshipBaseQuery
                    .Where(row => row.TermEndOn != null &&
                        (forced || (row.TermEndOn >= today && row.TermEndOn < today.AddDays(76))))
                    .Where(row => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseManagementId == row.LeaseManagementId &&
                        d.NoticeType == "lease-renewal-offer" &&
                        d.Status == "Draft"));

                var renewalRelationships = await renewalQuery
                    .OrderBy(row => row.TermEndOn)
                    .ThenBy(row => row.LeaseManagementId)
                    .ToListAsync(ct);

                foreach (var relationship in renewalRelationships)
                {
                    var daysToEnd = relationship.TermEndOn!.Value.DayNumber - today.DayNumber;
                    created.Add(await BuildRenewalDraftAsync(portfolioId, relationship, daysToEnd, now, TemplateFor("lease-renewal-offer"), portfolioName, ct));
                }
            }

            if (wantsMoveOut)
            {
                var moveOutQuery = relationshipBaseQuery
                    .Where(row => row.TermEndOn != null &&
                        (forced || (row.TermEndOn >= today && row.TermEndOn < today.AddDays(31))))
                    .Where(row => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseManagementId == row.LeaseManagementId &&
                        d.NoticeType == "lease-non-renewal" &&
                        d.Status == "Draft"));

                var moveOutRelationships = await moveOutQuery
                    .OrderBy(row => row.TermEndOn)
                    .ThenBy(row => row.LeaseManagementId)
                    .ToListAsync(ct);

                foreach (var relationship in moveOutRelationships)
                {
                    var daysToEnd = relationship.TermEndOn!.Value.DayNumber - today.DayNumber;
                    created.Add(await BuildMoveOutDraftAsync(portfolioId, relationship, daysToEnd, now, TemplateFor("lease-non-renewal"), portfolioName, ct));
                }
            }

            if (wantsRentReminder)
            {
                var reminderRelationships = await relationshipBaseQuery
                    .Where(row => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseManagementId == row.LeaseManagementId &&
                        d.NoticeType == "rent-reminder" &&
                        d.Status == "Draft"))
                    .OrderBy(row => row.TermEndOn)
                    .ThenBy(row => row.LeaseManagementId)
                    .ToListAsync(ct);

                foreach (var relationship in reminderRelationships)
                {
                    created.Add(await BuildRentReminderDraftAsync(portfolioId, relationship, now, TemplateFor("rent-reminder"), portfolioName, ct));
                }
            }

            if (wantsMonthToMonth)
            {
                var monthToMonthQuery = relationshipBaseQuery
                    .Where(row => row.TermEndOn != null &&
                        (forced || (row.TermEndOn >= today && row.TermEndOn < today.AddDays(76))))
                    .Where(row => !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseManagementId == row.LeaseManagementId &&
                        d.NoticeType == "month-to-month-offer" &&
                        d.Status == "Draft"));

                var monthToMonthRelationships = await monthToMonthQuery
                    .OrderBy(row => row.TermEndOn)
                    .ThenBy(row => row.LeaseManagementId)
                    .ToListAsync(ct);

                foreach (var relationship in monthToMonthRelationships)
                {
                    var daysToEnd = relationship.TermEndOn!.Value.DayNumber - today.DayNumber;
                    created.Add(await BuildMonthToMonthDraftAsync(portfolioId, relationship, daysToEnd, now, TemplateFor("month-to-month-offer"), portfolioName, ct));
                }
            }
        }

        // Late-rent notices are always grounded in a real open tenant-ledger charge, so even a
        // forced request only produces one when such a charge exists.
        if (WantsType("late-rent-late-fee"))
        {
            var lateQuery = MoneyNoticeCandidateQuery(portfolioId)
                .Where(row =>
                    row.IsPastDue &&
                    (row.EntryType == nameof(TenantLedgerEntryType.RentCharge) ||
                     row.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge)) &&
                    !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.LeaseManagementId == row.LeaseManagementId &&
                        d.NoticeType == "late-rent-late-fee" &&
                        d.Status == "Draft"));
            if (recipientTenantId.HasValue)
            {
                lateQuery = lateQuery.Where(row => row.RecipientTenantId == recipientTenantId.Value);
            }
            if (leaseManagementId.HasValue)
            {
                lateQuery = lateQuery.Where(row => row.LeaseManagementId == leaseManagementId.Value);
            }
            if (tenantAccountId.HasValue)
            {
                lateQuery = lateQuery.Where(row => row.TenantAccountId == tenantAccountId.Value);
            }

            // Pick one overdue charge per relationship in SQL. Prefer the substantive rent balance,
            // then fold same-due-date late fees into it without client-side grouping.
            var lateLedgerEntryIds = await lateQuery
                .GroupBy(row => row.LeaseManagementId)
                .Select(g => g
                    .OrderBy(row => row.EntryType == nameof(TenantLedgerEntryType.RentCharge) ? 0 : 1)
                    .ThenByDescending(row => row.OpenAmount)
                    .ThenByDescending(row => row.DueOn)
                    .ThenBy(row => row.TenantLedgerEntryId)
                    .Select(row => row.TenantLedgerEntryId)
                    .First())
                .ToListAsync(ct);

            var lateCandidates = await MoneyNoticeCandidateQuery(portfolioId)
                .Where(row => lateLedgerEntryIds.Contains(row.TenantLedgerEntryId))
                .Select(row => new
                {
                    Candidate = row,
                    RelatedLateFeeAmount = row.EntryType == nameof(TenantLedgerEntryType.RentCharge)
                        ? _db.TenantChargeBalanceProjections
                            .Where(fee =>
                                fee.PortfolioId == portfolioId &&
                                fee.TenantAccountId == row.TenantAccountId &&
                                fee.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge) &&
                                fee.DueOn == row.DueOn &&
                                fee.OpenAmount > 0m)
                            .Sum(fee => (decimal?)fee.OpenAmount) ?? 0m
                        : 0m,
                })
                .OrderBy(row => row.Candidate.DueOn)
                .ThenBy(row => row.Candidate.LeaseManagementId)
                .ToListAsync(ct);

            foreach (var row in lateCandidates)
            {
                var candidate = row.Candidate;
                var daysLate = today.DayNumber - candidate.DueOn.DayNumber;
                created.Add(await BuildLateDraftAsync(
                    portfolioId,
                    candidate,
                    daysLate,
                    now,
                    TemplateFor("late-rent-late-fee"),
                    portfolioName,
                    ct,
                    relatedLateFeeAmount: row.RelatedLateFeeAmount));
            }
        }

        // Periodic, charge-grounded rent reminders: one per active relationship with an open rent
        // charge due within the lead window. Per-period idempotent (keyed on the charge's due date)
        // so monthly reminders recur but a given period's reminder is never duplicated/recreated.
        if (wantsUpcomingRentReminder)
        {
            var leadWindowEndExclusive = today.AddDays(RentReminderLeadDays + 1);
            var upcomingQuery = MoneyNoticeCandidateQuery(portfolioId)
                .Where(row =>
                    row.EntryType == nameof(TenantLedgerEntryType.RentCharge) &&
                    row.OpenAmount > 0m &&
                    row.DueOn >= today &&
                    row.DueOn < leadWindowEndExclusive &&
                    !_db.NoticeDrafts.Any(d =>
                        d.PortfolioId == portfolioId &&
                        d.NoticeType == "rent-reminder" &&
                        d.TenantLedgerEntryId == row.TenantLedgerEntryId));
            if (recipientTenantId.HasValue)
            {
                upcomingQuery = upcomingQuery.Where(row => row.RecipientTenantId == recipientTenantId.Value);
            }
            if (leaseManagementId.HasValue)
            {
                upcomingQuery = upcomingQuery.Where(row => row.LeaseManagementId == leaseManagementId.Value);
            }
            if (tenantAccountId.HasValue)
            {
                upcomingQuery = upcomingQuery.Where(row => row.TenantAccountId == tenantAccountId.Value);
            }

            var upcomingLedgerEntryIds = await upcomingQuery
                .GroupBy(row => new { row.LeaseManagementId, row.DueOn })
                .Select(g => g.Min(row => row.TenantLedgerEntryId))
                .ToListAsync(ct);

            var upcomingCharges = await MoneyNoticeCandidateQuery(portfolioId)
                .Where(row => upcomingLedgerEntryIds.Contains(row.TenantLedgerEntryId))
                .OrderBy(row => row.DueOn)
                .ThenBy(row => row.LeaseManagementId)
                .ToListAsync(ct);

            foreach (var charge in upcomingCharges)
            {
                created.Add(await BuildRentReminderDraftAsync(
                    portfolioId,
                    charge,
                    now,
                    TemplateFor("rent-reminder"),
                    portfolioName,
                    ct,
                    charge.DueOn,
                    charge.TenantLedgerEntryId));
            }
        }

        if (created.Count > 0)
        {
            foreach (var draft in created)
            {
                var policy = policies[draft.NoticeType];
                draft.TenantNoticePolicyId = policy.Id;
                draft.WorkspaceNoticeTemplateVersionId = policy.WorkspaceNoticeTemplateVersionId;
            }
            _db.NoticeDrafts.AddRange(created);
            await _db.SaveChangesAsync(ct);
        }

        return new GenerateNoticeDraftsResponse
        {
            CreatedCount = created.Count,
            Drafts = await BuildGenerateResponseDraftsAsync(
                portfolioId,
                recipientTenantId,
                leaseManagementId,
                tenantAccountId,
                null,
                requestedType,
                created,
                ct)
        };
    }

    private async Task<GenerateNoticeDraftsResponse> GenerateLedgerEntryScopedAsync(
        int portfolioId,
        long tenantLedgerEntryId,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        string? requestedType,
        DateTime now,
        DateOnly today,
        Func<string, NoticeTemplate?> templateFor,
        string portfolioName,
        CancellationToken ct)
    {
        var charge = await MoneyNoticeCandidateQuery(portfolioId)
            .Where(row => row.TenantLedgerEntryId == tenantLedgerEntryId)
            .FirstOrDefaultAsync(ct);

        if (charge == null)
        {
            return new GenerateNoticeDraftsResponse();
        }

        if (leaseManagementId.HasValue && charge.LeaseManagementId != leaseManagementId.Value)
        {
            throw new DomainValidationException("Ledger entry does not belong to the selected lease relationship.");
        }

        if (tenantAccountId.HasValue && charge.TenantAccountId != tenantAccountId.Value)
        {
            throw new DomainValidationException("Ledger entry does not belong to the selected tenant account.");
        }

        if (recipientTenantId.HasValue && charge.RecipientTenantId != recipientTenantId.Value)
        {
            throw new DomainValidationException("Ledger entry does not belong to the selected recipient.");
        }

        var noticeType = requestedType;
        if (string.IsNullOrWhiteSpace(noticeType))
        {
            noticeType = InferChargeNoticeType(charge, today);
        }

        if (string.IsNullOrWhiteSpace(noticeType) || !IsEligibleForChargeNotice(charge, noticeType, today))
        {
            return new GenerateNoticeDraftsResponse
            {
                Drafts = await BuildGenerateResponseDraftsAsync(
                    portfolioId,
                    charge.RecipientTenantId,
                    charge.LeaseManagementId,
                    charge.TenantAccountId,
                    charge.TenantLedgerEntryId,
                    noticeType,
                    [],
                    ct)
            };
        }

        var existing = await LedgerEntryScopedExistingDraftsQuery(portfolioId, charge, noticeType)
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
        if (string.Equals(noticeType, "rent-reminder", StringComparison.OrdinalIgnoreCase))
        {
            draft = await BuildRentReminderDraftAsync(
                portfolioId,
                charge,
                now,
                templateFor("rent-reminder"),
                portfolioName,
                ct,
                charge.DueOn,
                charge.TenantLedgerEntryId);
        }
        else
        {
            var relatedLateFeeAmount = await RelatedLateFeeOutstandingAsync(portfolioId, charge, ct);
            draft = await BuildLateDraftAsync(
                portfolioId,
                charge,
                Math.Max(0, today.DayNumber - charge.DueOn.DayNumber),
                now,
                templateFor("late-rent-late-fee"),
                portfolioName,
                ct,
                relatedLateFeeAmount,
                charge.TenantLedgerEntryId);
        }

        var policy = await _db.TenantNoticePolicies.AsNoTracking()
            .SingleAsync(row => row.PortfolioId == portfolioId && row.AutomationKey == noticeType && row.Mode != TenantNoticeMode.Off, ct);
        draft.TenantNoticePolicyId = policy.Id;
        draft.WorkspaceNoticeTemplateVersionId = policy.WorkspaceNoticeTemplateVersionId;
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

    private IQueryable<NoticeDraft> LedgerEntryScopedExistingDraftsQuery(
        int portfolioId,
        MoneyNoticeCandidate charge,
        string noticeType)
    {
        return BaseQuery(portfolioId)
            .Where(d =>
                d.NoticeType == noticeType &&
                (d.Status == "Draft" || d.Status == "Approved") &&
                d.TenantLedgerEntryId == charge.TenantLedgerEntryId);
    }

    private static string? InferChargeNoticeType(MoneyNoticeCandidate charge, DateOnly today)
    {
        if (IsEligibleForChargeNotice(charge, "late-rent-late-fee", today))
        {
            return "late-rent-late-fee";
        }

        if (IsEligibleForChargeNotice(charge, "rent-reminder", today))
        {
            return "rent-reminder";
        }

        return null;
    }

    private static bool IsEligibleForChargeNotice(MoneyNoticeCandidate charge, string noticeType, DateOnly today)
    {
        if (charge.OpenAmount <= 0m)
        {
            return false;
        }

        if (string.Equals(noticeType, "rent-reminder", StringComparison.OrdinalIgnoreCase))
        {
            return charge.EntryType == nameof(TenantLedgerEntryType.RentCharge) &&
                   charge.DueOn >= today;
        }

        if (string.Equals(noticeType, "late-rent-late-fee", StringComparison.OrdinalIgnoreCase))
        {
            return (charge.EntryType == nameof(TenantLedgerEntryType.RentCharge) ||
                    charge.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge)) &&
                   charge.DueOn < today;
        }

        return false;
    }

    private async Task<decimal> RelatedLateFeeOutstandingAsync(
        int portfolioId,
        MoneyNoticeCandidate charge,
        CancellationToken ct)
    {
        if (charge.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge))
        {
            return charge.OpenAmount;
        }

        if (charge.EntryType != nameof(TenantLedgerEntryType.RentCharge))
        {
            return 0m;
        }

        return await _db.TenantChargeBalanceProjections
            .AsNoTracking()
            .Where(fee =>
                fee.PortfolioId == portfolioId &&
                fee.TenantAccountId == charge.TenantAccountId &&
                fee.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge) &&
                fee.DueOn == charge.DueOn &&
                fee.OpenAmount > 0m)
            .SumAsync(fee => (decimal?)fee.OpenAmount, ct) ?? 0m;
    }

    private class NoticeContext
    {
        public int LeaseManagementId { get; init; }
        public int TenantAccountId { get; init; }
        public int RecipientTenantId { get; init; }
        public int PropertyId { get; init; }
        public int UnitId { get; init; }
        public int CurrentAgreementId { get; init; }
        public string TenantName { get; init; } = string.Empty;
        public string PropertyName { get; init; } = string.Empty;
        public string? UnitNumber { get; init; }
        public string RelationshipNumber { get; init; } = string.Empty;
        public DateOnly TermStartOn { get; init; }
        public DateOnly? TermEndOn { get; init; }
        public decimal BaseRentAmount { get; init; }
        public short RentDueDay { get; init; }
    }

    private sealed class MoneyNoticeCandidate : NoticeContext
    {
        public long TenantLedgerEntryId { get; init; }
        public string EntryType { get; init; } = string.Empty;
        public DateOnly DueOn { get; init; }
        public decimal OpenAmount { get; init; }
        public bool IsPastDue { get; init; }
    }

    private IQueryable<NoticeContext> ActiveNoticeContextQuery(int portfolioId) =>
        from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
        join management in _db.LeaseManagements.AsNoTracking()
            on new { lifecycle.PortfolioId, Id = lifecycle.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { lifecycle.PortfolioId, Id = lifecycle.CurrentAgreementId }
            equals new { agreement.PortfolioId, Id = (int?)agreement.Id }
        join account in _db.TenantAccounts.AsNoTracking()
            on new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            equals new { account.PortfolioId, account.LeaseManagementId }
        join tenant in _db.Tenants.AsNoTracking()
            on new { lifecycle.PortfolioId, Id = lifecycle.CurrentPrimaryTenantId }
            equals new { tenant.PortfolioId, Id = (int?)tenant.Id }
        where lifecycle.PortfolioId == portfolioId
            && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
            && !lifecycle.HasReconciliationException
        select new NoticeContext
        {
            LeaseManagementId = management.Id,
            TenantAccountId = account.Id,
            RecipientTenantId = tenant.Id,
            PropertyId = management.PropertyId,
            UnitId = management.UnitId,
            CurrentAgreementId = agreement.Id,
            TenantName = (tenant.FirstName + " " + tenant.LastName).Trim(),
            PropertyName = management.Property!.Name,
            UnitNumber = management.Unit!.UnitNumber,
            RelationshipNumber = management.RelationshipNumber,
            TermStartOn = agreement.TermStartOn,
            TermEndOn = agreement.TermEndOn,
            BaseRentAmount = agreement.BaseRentAmount,
            RentDueDay = agreement.RentDueDay,
        };

    private IQueryable<MoneyNoticeCandidate> MoneyNoticeCandidateQuery(int portfolioId) =>
        from context in ActiveNoticeContextQuery(portfolioId)
        join charge in _db.TenantChargeBalanceProjections.AsNoTracking()
            on new { PortfolioId = portfolioId, context.TenantAccountId }
            equals new { charge.PortfolioId, charge.TenantAccountId }
        join entry in _db.TenantLedgerEntries.AsNoTracking()
            on new
            {
                charge.PortfolioId,
                charge.TenantAccountId,
                Id = charge.TenantLedgerEntryId,
            }
            equals new { entry.PortfolioId, entry.TenantAccountId, entry.Id }
        where charge.DueOn != null
        select new MoneyNoticeCandidate
        {
            LeaseManagementId = context.LeaseManagementId,
            TenantAccountId = context.TenantAccountId,
            RecipientTenantId = context.RecipientTenantId,
            PropertyId = context.PropertyId,
            UnitId = context.UnitId,
            CurrentAgreementId = context.CurrentAgreementId,
            TenantName = context.TenantName,
            PropertyName = context.PropertyName,
            UnitNumber = context.UnitNumber,
            RelationshipNumber = context.RelationshipNumber,
            TermStartOn = context.TermStartOn,
            TermEndOn = context.TermEndOn,
            BaseRentAmount = context.BaseRentAmount,
            RentDueDay = context.RentDueDay,
            TenantLedgerEntryId = entry.Id,
            EntryType = charge.EntryType,
            DueOn = charge.DueOn.Value,
            OpenAmount = charge.OpenAmount,
            IsPastDue = charge.IsPastDue,
        };

    private IQueryable<NoticeDraft> BaseQuery(int portfolioId) =>
        _db.NoticeDrafts
            .Include(d => d.RecipientTenant)
            .Include(d => d.Property)
            .Include(d => d.LeaseManagement).ThenInclude(l => l!.Unit)
            .Where(d => d.PortfolioId == portfolioId);

    private async Task<IReadOnlyList<NoticeDraftResponse>> BuildGenerateResponseDraftsAsync(
        int portfolioId,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? requestedType,
        List<NoticeDraft> created,
        CancellationToken ct)
    {
        if (!recipientTenantId.HasValue && !leaseManagementId.HasValue && !tenantAccountId.HasValue && !tenantLedgerEntryId.HasValue)
        {
            return created.Select(Map).ToList();
        }

        var query = BaseQuery(portfolioId);

        if (tenantLedgerEntryId.HasValue)
        {
            query = query.Where(d =>
                d.TenantLedgerEntryId == tenantLedgerEntryId.Value &&
                (d.Status == "Draft" || d.Status == "Approved"));
        }
        else
        {
            query = query.Where(d => d.Status == "Draft");
        }

        if (recipientTenantId.HasValue)
        {
            query = query.Where(d => d.RecipientTenantId == recipientTenantId.Value);
        }

        if (leaseManagementId.HasValue)
        {
            query = query.Where(d => d.LeaseManagementId == leaseManagementId.Value);
        }

        if (tenantAccountId.HasValue)
        {
            query = query.Where(d => d.TenantAccountId == tenantAccountId.Value);
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
        int portfolioId, NoticeContext context, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
    {
        var propertyName = context.PropertyName;
        var unit = UnitSuffix(context.UnitNumber);
        var tenantName = context.TenantName;
        var endOn = context.TermEndOn!.Value;

        // Renewal terms: escalate the current rent and extend the term by a year.
        var proposedRent = Math.Round(context.BaseRentAmount * (1 + RenewalEscalationPercent / 100m), 0, MidpointRounding.AwayFromZero);
        var newEndDate = endOn.AddMonths(RenewalTermMonths);

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Current lease ends: {endOn:MMMM d, yyyy} ({daysToEnd} days away)\n" +
            $"- Current rent: {Usd(context.BaseRentAmount)}/month\n" +
            $"- Proposed new rent: {Usd(proposedRent)}/month (a {RenewalEscalationPercent:0.#}% increase)\n" +
            $"- Proposed new term: {RenewalTermMonths} months, new end date {newEndDate:MMMM d, yyyy}\n" +
            "- Ask the tenant to reply to accept, decline, or ask questions.";

        var deterministicSubject = $"Lease renewal for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, your current lease for {propertyName}{unit} ends on {endOn:MMMM d, yyyy}. "
            + $"We would like to offer a {RenewalTermMonths}-month renewal at {Usd(proposedRent)} per month "
            + $"(currently {Usd(context.BaseRentAmount)}), running through {newEndDate:MMMM d, yyyy}. "
            + "Please reply here to accept, decline, or ask any questions.";

        var tokens = BuildTokens(context,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        var draft = await ComposeAsync(
            portfolioId, context, "lease-renewal-offer",
            intent: "a friendly lease-renewal offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days. Proposed {Usd(proposedRent)}/mo ({RenewalEscalationPercent:0.#}% increase) through {newEndDate:MMM d, yyyy}.",
            triggerDate: endOn.ToDateTime(TimeOnly.MinValue),
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);

        return draft;
    }

    private async Task<NoticeDraft> BuildMoveOutDraftAsync(
        int portfolioId, NoticeContext context, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
    {
        var propertyName = context.PropertyName;
        var unit = UnitSuffix(context.UnitNumber);
        var tenantName = context.TenantName;
        var endOn = context.TermEndOn!.Value;

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Lease ends: {endOn:MMMM d, yyyy} ({daysToEnd} days away)\n" +
            "- Coordinate: key return, the move-out inspection time, and a forwarding address for the deposit.\n" +
            "- Ask the tenant to reply to set up those details.";

        var deterministicSubject = $"Move-out reminder for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, this is a reminder that your lease for {propertyName}{unit} ends on {endOn:MMMM d, yyyy}. "
            + "Please reply to coordinate keys, inspection timing, and forwarding-address details so we can return your deposit promptly.";

        var tokens = BuildTokens(context,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, context, "lease-non-renewal",
            intent: "a courteous move-out coordination reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: $"Lease ends in {daysToEnd} days.",
            triggerDate: endOn.ToDateTime(TimeOnly.MinValue),
            now: now,
            template: template,
            tokens: tokens,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildLateDraftAsync(
        int portfolioId,
        MoneyNoticeCandidate charge,
        int daysLate,
        DateTime now,
        NoticeTemplate? template,
        string portfolioName,
        CancellationToken ct,
        decimal relatedLateFeeAmount = 0m,
        long? tenantLedgerEntryId = null)
    {
        var propertyName = charge.PropertyName;
        var unit = UnitSuffix(charge.UnitNumber);
        var tenantName = charge.TenantName;
        var isLateFee = charge.EntryType == nameof(TenantLedgerEntryType.LateFeeCharge);
        var lateFeeAmount = isLateFee ? charge.OpenAmount : relatedLateFeeAmount;
        var totalDue = isLateFee ? charge.OpenAmount : charge.OpenAmount + lateFeeAmount;
        var chargeLabel = isLateFee ? "late fee" : "rent";
        var lateFeeClause = lateFeeAmount > 0m && !isLateFee
            ? $" This includes {Usd(lateFeeAmount)} in late fees."
            : "";

        // Escalation: tone hardens the longer rent stays unpaid.
        var (level, levelLabel, tone) = LateEscalationLevel(daysLate);

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Charge type: {chargeLabel}\n" +
            $"- Amount due: {Usd(totalDue)}\n" +
            (lateFeeAmount > 0m ? $"- Late fee amount: {Usd(lateFeeAmount)}\n" : "") +
            $"- Was due: {charge.DueOn:MMMM d, yyyy} ({daysLate} days ago)\n" +
            $"- This is the {levelLabel} reminder. {tone}\n" +
            "- Ask the tenant to reply with payment status or to arrange a plan.";

        var deterministicSubject = isLateFee
            ? $"Late fee notice for {propertyName}{unit}"
            : level switch
            {
                1 => $"Friendly reminder: rent past due for {propertyName}{unit}",
                2 => $"Second notice: rent {daysLate} days past due for {propertyName}{unit}",
                _ => $"Final notice: overdue rent for {propertyName}{unit}"
            };
        var deterministicBody = isLateFee
            ? $"Hi {tenantName}, our records show a late fee of {Usd(totalDue)} due on {charge.DueOn:MMMM d, yyyy} "
              + $"for {propertyName}{unit} remains unpaid. Please reply with your payment status or any questions."
            : level switch
        {
            1 => $"Hi {tenantName}, our records show {Usd(totalDue)} due on {charge.DueOn:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} is now {daysLate} days past due.{lateFeeClause} If you've already paid, thank you - "
                 + "please disregard. Otherwise, please reply with your payment status or any questions.",
            2 => $"Hi {tenantName}, this is a second reminder that {Usd(totalDue)} due on {charge.DueOn:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid ({daysLate} days past due).{lateFeeClause} Please bring the balance current "
                 + "or reply so we can arrange a payment plan.",
            _ => $"Hi {tenantName}, this is a final notice that {Usd(totalDue)} due on {charge.DueOn:MMMM d, yyyy} "
                 + $"for {propertyName}{unit} remains unpaid and is now {daysLate} days past due.{lateFeeClause} Please pay in full "
                 + "immediately or contact us today to avoid further action under your lease."
        };

        var tokens = BuildTokens(charge,
            [(NoticeMergeFields.OverdueAmount, Usd(totalDue)),
             (NoticeMergeFields.RentDueDate, charge.DueOn.ToString("MMMM d, yyyy")),
             (NoticeMergeFields.LateFeeAmount, lateFeeAmount > 0m ? Usd(lateFeeAmount) : ""),
             (NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, charge, "late-rent-late-fee",
            intent: $"a {levelLabel} past-due rent reminder ({tone})",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: lateFeeAmount > 0m && !isLateFee
                ? $"Rent is {daysLate} days past due ({levelLabel} notice), including {Usd(lateFeeAmount)} in late fees."
                : $"{chargeLabel} is {daysLate} days past due ({levelLabel} notice).",
            triggerDate: charge.DueOn.ToDateTime(TimeOnly.MinValue),
            now: now,
            template: template,
            tokens: tokens,
            tenantLedgerEntryId: tenantLedgerEntryId,
            ct: ct);
    }

    /// <summary>
    /// Builds an upcoming-rent reminder. When <paramref name="dueDate"/> is supplied (the periodic,
    /// charge-grounded path) the draft's <see cref="NoticeDraft.TriggerDate"/> and the
    /// <c>rent_due_date</c> token are set to that date; on the explicit/forced path with no upcoming
    /// charge, <paramref name="dueDate"/> is null → TriggerDate falls back to today and the token blanks.
    /// </summary>
    private async Task<NoticeDraft> BuildRentReminderDraftAsync(
        int portfolioId, NoticeContext context, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct,
        DateOnly? dueDate = null,
        long? tenantLedgerEntryId = null)
    {
        var propertyName = context.PropertyName;
        var unit = UnitSuffix(context.UnitNumber);
        var tenantName = context.TenantName;

        var dueOn = dueDate;
        var dueClause = dueOn.HasValue ? $" on {dueOn:MMMM d, yyyy}" : "";

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Monthly rent: {Usd(context.BaseRentAmount)}\n" +
            (dueOn.HasValue ? $"- Rent due date: {dueOn:MMMM d, yyyy}\n" : "") +
            "- This is a friendly heads-up that rent is coming due.\n" +
            "- Ask the tenant to reply with any questions.";

        var deterministicSubject = $"Rent reminder for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, this is a friendly reminder that your rent of {Usd(context.BaseRentAmount)} "
            + $"for {propertyName}{unit} is coming due{dueClause}. Please reach out with any questions. Thank you!";

        var tokens = BuildTokens(context,
            [(NoticeMergeFields.RentDueDate, dueOn.HasValue ? dueOn.Value.ToString("MMMM d, yyyy") : ""),
             (NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, context, "rent-reminder",
            intent: "a friendly upcoming-rent reminder",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: dueOn.HasValue ? $"Rent due {dueOn:MMM d, yyyy}." : "Manual rent reminder.",
            triggerDate: dueOn?.ToDateTime(TimeOnly.MinValue) ?? now.Date,
            now: now,
            template: template,
            tokens: tokens,
            tenantLedgerEntryId: tenantLedgerEntryId,
            ct: ct);
    }

    private async Task<NoticeDraft> BuildMonthToMonthDraftAsync(
        int portfolioId, NoticeContext context, int daysToEnd, DateTime now, NoticeTemplate? template, string portfolioName, CancellationToken ct)
    {
        var propertyName = context.PropertyName;
        var unit = UnitSuffix(context.UnitNumber);
        var tenantName = context.TenantName;
        var endOn = context.TermEndOn!.Value;

        var facts =
            $"- Tenant: {tenantName}\n" +
            $"- Property/unit: {propertyName}{unit}\n" +
            $"- Current lease ends: {endOn:MMMM d, yyyy}\n" +
            $"- Current rent: {Usd(context.BaseRentAmount)}/month\n" +
            "- Offer to continue on a month-to-month basis at the current rent after the lease ends.\n" +
            "- Ask the tenant to reply to accept or ask questions.";

        var deterministicSubject = $"Month-to-month option for {propertyName}{unit}";
        var deterministicBody =
            $"Hi {tenantName}, your lease for {propertyName}{unit} ends on {endOn:MMMM d, yyyy}. "
            + $"We're happy to continue on a month-to-month basis at {Usd(context.BaseRentAmount)} per month. "
            + "Please reply to accept or with any questions.";

        var tokens = BuildTokens(context,
            [(NoticeMergeFields.PortfolioName, portfolioName)]);

        return await ComposeAsync(
            portfolioId, context, "month-to-month-offer",
            intent: "a friendly month-to-month continuation offer",
            facts: facts,
            deterministicSubject: deterministicSubject,
            deterministicBody: deterministicBody,
            reason: "Manual month-to-month conversion offer.",
            triggerDate: endOn.ToDateTime(TimeOnly.MinValue),
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
        NoticeContext context,
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
        long? tenantLedgerEntryId = null)
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
                var raw = await GenerateCopyAsync(prompt, noticeType, context.LeaseManagementId, ct);
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
                _logger.LogWarning(
                    ex,
                    "LLM copy generation failed for {NoticeType} (lease relationship {LeaseManagementId}); using template.",
                    noticeType,
                    context.LeaseManagementId);
            }
        }

        return new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseManagementId = context.LeaseManagementId,
            TenantAccountId = context.TenantAccountId,
            TenantLedgerEntryId = tenantLedgerEntryId,
            RecipientTenantId = context.RecipientTenantId,
            PropertyId = context.PropertyId,
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
        NoticeContext context,
        (string Key, string Value)[] extra)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NoticeMergeFields.TenantName] = context.TenantName,
            [NoticeMergeFields.PropertyAddress] = context.PropertyName,
            [NoticeMergeFields.UnitNumber] = context.UnitNumber ?? "",
            [NoticeMergeFields.LeaseStartDate] = context.TermStartOn.ToString("MMMM d, yyyy"),
            [NoticeMergeFields.LeaseEndDate] = context.TermEndOn?.ToString("MMMM d, yyyy") ?? "",
            [NoticeMergeFields.RentAmount] = Usd(context.BaseRentAmount),
        };
        foreach (var (key, value) in extra) tokens[key] = value;
        return tokens;
    }

    // Tenant notices currently describe U.S. leases and dollar-denominated charges. Their legal
    // and payment copy must not change symbols with the API host's process culture (for example,
    // Azure's invariant culture renders the generic currency sign for the "C" format).
    private static string Usd(decimal amount) =>
        amount.ToString("C0", CultureInfo.GetCultureInfo("en-US"));

    private async Task<string?> GenerateCopyAsync(
        string prompt,
        string noticeType,
        int leaseManagementId,
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
            "LLM copy generation timed out for {NoticeType} (lease relationship {LeaseManagementId}); using template.",
            noticeType,
            leaseManagementId);
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

    private static NoticeDraftResponse Map(NoticeDraft d)
    {
        var tenantName = d.RecipientTenant == null
            ? ""
            : $"{d.RecipientTenant.FirstName} {d.RecipientTenant.LastName}".Trim();
        return new NoticeDraftResponse
        {
            Id = d.Id,
            LeaseManagementId = d.LeaseManagementId,
            TenantAccountId = d.TenantAccountId,
            TenantLedgerEntryId = d.TenantLedgerEntryId,
            RecipientTenantId = d.RecipientTenantId,
            PropertyId = d.PropertyId,
            TenantName = tenantName,
            PropertyName = d.Property?.Name,
            UnitNumber = d.LeaseManagement?.Unit?.UnitNumber,
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
