using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Atomic;

/// <summary>Admits the existing one-statement notice generator into the active receipt transaction.</summary>
internal sealed class AtomicNoticeDraftPersistence : IAtomicNoticeDraftPersistence
{
    private readonly TenantNoticeDraftSetStore _store;
    private readonly AtomicAuditScope _scope;

    public AtomicNoticeDraftPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _store = new TenantNoticeDraftSetStore(db);
        _scope = scope;
    }

    public async Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("NoticeDrafts", AtomicRawDmlOperation.Insert);
        return await _store.GenerateManualAsync(
            scope,
            recipientTenantId,
            leaseManagementId,
            tenantAccountId,
            tenantLedgerEntryId,
            noticeType,
            securityNowUtc,
            ct);
    }
}
