using RentalCommand.Data.Notifications;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due lease-lifecycle and rent-balance automations into the canonical tenant-notice work queue.
/// Candidate eligibility, channel/recipient eligibility, deduplication, and insertion are one
/// PostgreSQL statement over authoritative lease projections. This service never sends a message.
/// </summary>
public sealed class TenantNoticeCandidateGenerationService : ITenantNoticeCandidateGenerationService
{
    private readonly ITenantNoticeCandidateStore _candidates;

    public TenantNoticeCandidateGenerationService(
        ITenantNoticeCandidateStore candidates)
    {
        _candidates = candidates;
    }

    public Task<int> GenerateDueAsync(CancellationToken ct = default) =>
        _candidates.GenerateDueAsync(ct);
}
