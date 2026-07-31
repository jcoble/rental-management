using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Payments;

public sealed class AtomicProviderPaymentPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    private AtomicProviderPaymentPersistence(RentalCommandDbContext db, AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }


    private static AtomicAuditScope RequireAuditScope(RentalCommandDbContext db, IAtomicCommandContext context)
    {
        if (context is not AtomicCommandContext owner || !owner.Owns(db))
        {
            throw new AtomicArchitectureException(
                "Atomic helper requires the exact scoped DbContext and active command context.");
        }

        return owner.AuditScope;
    }
public static Task<Guid?> ClaimExactAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string claimOwner,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderPaymentPersistence(db, scope)
            .ClaimExactAsync(paymentAttemptId, tenantAccountId, portfolioId, claimOwner, ct);
    }

    public static Task<bool> TransitionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderPaymentPersistence(db, scope).TransitionAsync(
            paymentAttemptId, tenantAccountId, portfolioId, claimToken, state,
            providerObjectId, failureCode, failureReason, nextAttemptAtUtc, ct);
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
