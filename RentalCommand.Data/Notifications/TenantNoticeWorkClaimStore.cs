using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Notifications;

public interface ITenantNoticeWorkClaimStore
{
    Task<IReadOnlyList<ClaimedTenantNoticeWorkItem>> ClaimReadyAsync(
        string owner, Guid token, DateTime nowUtc, DateTime claimExpiresAtUtc, int batchSize, CancellationToken ct);
    Task<bool> CompleteAsync(long id, Guid token, CancellationToken ct);
    Task<bool> ReleaseAsync(long id, Guid token, DateTime retryAtUtc, CancellationToken ct);
    Task<bool> BlockAsync(long id, Guid token, CancellationToken ct);
}

/// <summary>
/// One fenced work item plus the immutable policy facts needed by the Engine. The claim/update and
/// this projection are one PostgreSQL statement, so workers do not materialize a policy dictionary
/// or query policy/template data once per work item.
/// </summary>
public sealed class ClaimedTenantNoticeWorkItem
{
    public long Id { get; init; }
    public int PortfolioId { get; init; }
    public int TenantNoticePolicyId { get; init; }
    public int LeaseManagementId { get; init; }
    public long? TenantLedgerEntryId { get; init; }
    public DateTime DueAtUtc { get; init; }
    public string BusinessKey { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public string AutomationKey { get; init; } = string.Empty;
    public bool IsAuto { get; init; }
    public string FailureBehavior { get; init; } = string.Empty;
    public int WorkspaceNoticeTemplateVersionId { get; init; }
    public bool SendTenantPortal { get; init; }
    public bool SendMobilePush { get; init; }
    public bool SendEmail { get; init; }
    public bool SendSms { get; init; }
}

/// <summary>PostgreSQL atomic selection + lease claim. Policy gates are evaluated in the same statement.</summary>
public sealed class TenantNoticeWorkClaimStore : ITenantNoticeWorkClaimStore
{
    private readonly RentalCommandDbContext _db;
    public TenantNoticeWorkClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ClaimedTenantNoticeWorkItem>> ClaimReadyAsync(
        string owner, Guid token, DateTime nowUtc, DateTime claimExpiresAtUtc, int batchSize, CancellationToken ct)
    {
        var boundedBatch = Math.Clamp(batchSize, 1, 100);
        return await _db.Database.SqlQuery<ClaimedTenantNoticeWorkItem>($"""
            WITH candidates AS (
                SELECT work."Id"
                FROM "TenantNoticeWorkItems" AS work
                INNER JOIN "TenantNoticePolicies" AS policy
                    ON policy."Id" = work."TenantNoticePolicyId"
                   AND policy."PortfolioId" = work."PortfolioId"
                INNER JOIN "WorkspaceNoticeTemplateVersions" AS template
                    ON template."Id" = policy."WorkspaceNoticeTemplateVersionId"
                   AND template."PortfolioId" = policy."PortfolioId"
                WHERE work."DueAtUtc" <= {nowUtc}
                  AND (work."Status" = 'Pending'
                       OR (work."Status" = 'Claimed' AND work."ClaimExpiresAtUtc" <= {nowUtc}))
                  AND policy."Mode" <> 'Off'
                  AND (policy."Mode" <> 'Auto' OR policy."Classification" <> 'Legal'
                       OR (policy."ReviewedJurisdictionCode" IS NOT NULL
                           AND policy."JurisdictionReviewedAtUtc" IS NOT NULL
                           AND template."JurisdictionReviewedAtUtc" IS NOT NULL))
                ORDER BY work."DueAtUtc", work."Id"
                FOR UPDATE OF work SKIP LOCKED
                LIMIT {boundedBatch}
            ), claimed AS (
                UPDATE "TenantNoticeWorkItems" AS work
                SET "Status" = 'Claimed',
                    "ClaimOwner" = {owner},
                    "ClaimToken" = {token},
                    "ClaimExpiresAtUtc" = {claimExpiresAtUtc},
                    "AttemptCount" = work."AttemptCount" + 1
                FROM candidates
                WHERE work."Id" = candidates."Id"
                RETURNING work.*
            )
            SELECT claimed."Id",
                   claimed."PortfolioId",
                   claimed."TenantNoticePolicyId",
                   claimed."LeaseManagementId",
                   claimed."TenantLedgerEntryId",
                   claimed."DueAtUtc",
                   claimed."BusinessKey",
                   claimed."AttemptCount",
                   policy."AutomationKey",
                   (policy."Mode" = 'Auto') AS "IsAuto",
                   policy."FailureBehavior"::text AS "FailureBehavior",
                   policy."WorkspaceNoticeTemplateVersionId",
                   policy."SendTenantPortal",
                   policy."SendMobilePush",
                   policy."SendEmail",
                   policy."SendSms"
            FROM claimed
            INNER JOIN "TenantNoticePolicies" AS policy
              ON policy."Id" = claimed."TenantNoticePolicyId"
             AND policy."PortfolioId" = claimed."PortfolioId"
            ORDER BY claimed."DueAtUtc", claimed."Id"
            """).ToListAsync(ct);
    }

    public async Task<bool> CompleteAsync(long id, Guid token, CancellationToken ct) =>
        await _db.TenantNoticeWorkItems
            .Where(row => row.Id == id && row.Status == TenantNoticeWorkStatus.Claimed && row.ClaimToken == token)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, TenantNoticeWorkStatus.Completed)
                .SetProperty(row => row.ClaimOwner, (string?)null)
                .SetProperty(row => row.ClaimToken, (Guid?)null)
                .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> ReleaseAsync(long id, Guid token, DateTime retryAtUtc, CancellationToken ct) =>
        await _db.TenantNoticeWorkItems
            .Where(row => row.Id == id && row.Status == TenantNoticeWorkStatus.Claimed && row.ClaimToken == token)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, TenantNoticeWorkStatus.Pending)
                .SetProperty(row => row.DueAtUtc, retryAtUtc)
                .SetProperty(row => row.ClaimOwner, (string?)null)
                .SetProperty(row => row.ClaimToken, (Guid?)null)
                .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> BlockAsync(long id, Guid token, CancellationToken ct) =>
        await _db.TenantNoticeWorkItems
            .Where(row => row.Id == id && row.Status == TenantNoticeWorkStatus.Claimed && row.ClaimToken == token)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, TenantNoticeWorkStatus.Blocked)
                .SetProperty(row => row.ClaimOwner, (string?)null)
                .SetProperty(row => row.ClaimToken, (Guid?)null)
                .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct) == 1;
}
