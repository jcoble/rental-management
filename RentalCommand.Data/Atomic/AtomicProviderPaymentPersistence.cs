using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicProviderPaymentPersistence : IAtomicProviderPaymentPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public AtomicProviderPaymentPersistence(RentalCommandDbContext db, AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public async Task<Guid?> ClaimExactAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string claimOwner,
        CancellationToken ct = default)
    {
        if (paymentAttemptId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentAttemptId));
        if (tenantAccountId <= 0) throw new ArgumentOutOfRangeException(nameof(tenantAccountId));
        if (portfolioId <= 0) throw new ArgumentOutOfRangeException(nameof(portfolioId));
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        using var lease = _auditScope.BeginInternalRawDml(
            "TenantPaymentAttempts", AtomicRawDmlOperation.Update);
        return await _db.Database.SqlQuery<ProviderPaymentClaimRow>($"""
            SELECT rc_claim_exact_tenant_payment_attempt(
                {paymentAttemptId}, {tenantAccountId}, {portfolioId}, {claimOwner}) AS "ClaimToken"
            """).Select(row => row.ClaimToken).SingleAsync(ct);
    }

    public async Task<bool> TransitionAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        Guid claimToken,
        TenantPaymentAttemptState state,
        string? providerObjectId,
        string? failureCode,
        string? failureReason,
        DateTime? nextAttemptAtUtc,
        CancellationToken ct = default)
    {
        if (paymentAttemptId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentAttemptId));
        if (tenantAccountId <= 0) throw new ArgumentOutOfRangeException(nameof(tenantAccountId));
        if (portfolioId <= 0) throw new ArgumentOutOfRangeException(nameof(portfolioId));
        if (claimToken == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(claimToken));

        using var lease = _auditScope.BeginInternalRawDml(
            "TenantPaymentAttempts", AtomicRawDmlOperation.Update);
        return await _db.Database.SqlQuery<ProviderPaymentTransitionRow>($"""
            SELECT rc_transition_tenant_payment_attempt(
                {paymentAttemptId}, {tenantAccountId}, {portfolioId}, {claimToken},
                {state.ToString()}, {providerObjectId}, {failureCode}, {failureReason},
                {nextAttemptAtUtc}) AS "Applied"
            """).Select(row => row.Applied).SingleAsync(ct);
    }

    private sealed class ProviderPaymentClaimRow
    {
        public Guid? ClaimToken { get; set; }
    }

    private sealed class ProviderPaymentTransitionRow
    {
        public bool Applied { get; set; }
    }
}
