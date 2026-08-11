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
        CancellationToken ct = default,
        Guid? providerFenceToken = null)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderPaymentPersistence(db, scope).TransitionAsync(
            paymentAttemptId, tenantAccountId, portfolioId, claimToken, state,
            providerObjectId, failureCode, failureReason, nextAttemptAtUtc, ct, providerFenceToken);
    }

    public static Task<ProviderPaymentSubmission?> SubmitAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string provider,
        string idempotencyKey,
        DateTime submittedAtUtc,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderPaymentPersistence(db, scope).SubmitAsync(
            paymentAttemptId, tenantAccountId, portfolioId, provider, idempotencyKey, submittedAtUtc, ct);
    }

    public static Task AssertFenceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        Guid providerFenceToken,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderPaymentPersistence(db, scope).AssertFenceAsync(
            paymentAttemptId, tenantAccountId, portfolioId, providerFenceToken, ct);
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
        CancellationToken ct = default,
        Guid? providerFenceToken = null)
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
                {nextAttemptAtUtc}, {providerFenceToken}) AS "Applied"
            """).Select(row => row.Applied).SingleAsync(ct);
    }

    private async Task AssertFenceAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        Guid providerFenceToken,
        CancellationToken ct)
    {
        if (paymentAttemptId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentAttemptId));
        if (tenantAccountId <= 0) throw new ArgumentOutOfRangeException(nameof(tenantAccountId));
        if (portfolioId <= 0) throw new ArgumentOutOfRangeException(nameof(portfolioId));
        if (providerFenceToken == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(providerFenceToken));

        await _db.Database.SqlQuery<long>($"""
            SELECT rc_assert_provider_payment_fence(
                {paymentAttemptId}, {tenantAccountId}, {portfolioId}, {providerFenceToken}) AS "Value"
            """).SingleAsync(ct);
    }

    private async Task<ProviderPaymentSubmission?> SubmitAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string provider,
        string idempotencyKey,
        DateTime submittedAtUtc,
        CancellationToken ct)
    {
        var attempt = await _db.Set<RentalCommand.Core.Entities.TenantPaymentAttempt>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == paymentAttemptId
                && row.TenantAccountId == tenantAccountId
                && row.PortfolioId == portfolioId
                && row.Provider == provider
                && row.IdempotencyKey == idempotencyKey, ct);
        if (attempt is null) return null;

        var wasNewSubmission = false;
        if (attempt.State == TenantPaymentAttemptState.Prepared)
        {
            wasNewSubmission = true;
            var fence = Guid.NewGuid();
            var claim = await ClaimExactAsync(paymentAttemptId, tenantAccountId, portfolioId,
                $"provider-submit:{paymentAttemptId}", ct);
            if (claim is null)
                throw new AtomicReceiptInvariantException(
                    $"Tenant payment context {paymentAttemptId} could not acquire its submit claim.");
            var applied = await TransitionAsync(paymentAttemptId, tenantAccountId, portfolioId,
                claim.Value, TenantPaymentAttemptState.Submitted, null, null, null, null, ct, fence);
            if (!applied)
                throw new AtomicReceiptInvariantException(
                    $"Tenant payment context {paymentAttemptId} lost its submit claim.");
            attempt = await _db.Set<RentalCommand.Core.Entities.TenantPaymentAttempt>()
                .AsNoTracking().SingleAsync(row => row.Id == paymentAttemptId, ct);
        }

        return new ProviderPaymentSubmission(
            attempt.State, attempt.Amount, attempt.Currency, attempt.Provider,
            attempt.IdempotencyKey, attempt.ProviderFenceToken, attempt.ProviderObjectId,
            wasNewSubmission, attempt.PreparedAtUtc);
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

public sealed record ProviderPaymentSubmission(
    TenantPaymentAttemptState State,
    decimal Amount,
    string Currency,
    string Provider,
    string IdempotencyKey,
    Guid? ProviderFenceToken,
    string? ProviderPaymentId,
    bool WasNewSubmission,
    DateTime PreparedAtUtc);
