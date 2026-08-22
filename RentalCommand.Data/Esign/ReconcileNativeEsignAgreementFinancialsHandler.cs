using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Leasing;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

public sealed class ReconcileNativeEsignAgreementFinancialsRule
{
    private readonly RentalCommandDbContext _db;

    public ReconcileNativeEsignAgreementFinancialsRule(RentalCommandDbContext db) => _db = db;

    public async Task<ReconcileNativeEsignAgreementFinancialsResult> ExecuteAsync(
        ReconcileNativeEsignAgreementFinancialsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.SignatureRequestId <= 0 || command.PublicId == Guid.Empty)
            throw new ArgumentException("The native e-sign reconciliation command is incomplete.");

        var request = await _db.Set<SignatureRequest>()
            .Where(candidate => candidate.Id == command.SignatureRequestId
                && candidate.PublicId == command.PublicId
                && candidate.Status == SignatureRequestStatus.Completed
                && candidate.ExecutedArtifactId != null
                && candidate.LeaseAgreementId != null
                && candidate.LeaseAddendumId == null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.PublicId,
                candidate.PortfolioId,
                LeaseAgreementId = candidate.LeaseAgreementId!.Value,
                LeaseManagementId = candidate.LeaseAgreement!.LeaseManagementId,
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException(
                "Only a completed native Agreement signature request can be reconciled.");

        await context.AcquireLockAsync(
            "LeaseManagement", request.LeaseManagementId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
        var inserted = await AtomicLeaseMutationPersistence.ReconcileInitialSecurityDepositChargeAsync(_db,
            context, request.PortfolioId, request.LeaseAgreementId, times.EffectiveNowUtc, ct);

        return new(
            request.PublicId,
            request.Id,
            request.LeaseAgreementId,
            inserted);
    }

    public async Task AuthorizeReplayAsync(
        ReconcileNativeEsignAgreementFinancialsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        ReconcileNativeEsignAgreementFinancialsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.SignatureRequestId <= 0 || command.PublicId == Guid.Empty)
            throw new ArgumentException("The native e-sign reconciliation command is incomplete.");

        var requestExists = await _db.Set<SignatureRequest>()
            .AsNoTracking()
            .AnyAsync(request =>
                request.Id == command.SignatureRequestId &&
                request.PublicId == command.PublicId &&
                request.Status == SignatureRequestStatus.Completed &&
                request.ExecutedArtifactId != null &&
                request.LeaseAgreementId != null &&
                request.LeaseAddendumId == null,
                ct);
        if (!requestExists)
        {
            throw new UnauthorizedAccessException("The completed native agreement signature request is unavailable.");
        }
    }
}

public sealed class ReconcileNativeEsignAgreementFinancialsBatchRule
{
    private readonly RentalCommandDbContext _db;

    public ReconcileNativeEsignAgreementFinancialsBatchRule(RentalCommandDbContext db) => _db = db;

    public async Task<ReconcileNativeEsignAgreementFinancialsBatchResult> ExecuteAsync(
        ReconcileNativeEsignAgreementFinancialsBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.RunToken == Guid.Empty)
            throw new ArgumentException("A native e-sign financial reconciliation run token is required.");
        if (command.BatchSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));

        var charges = await AtomicLeaseMutationPersistence.ReconcileCompletedNativeEsignInitialSecurityDepositChargesAsync(_db,
                context, command.BatchSize, ct);
        return new ReconcileNativeEsignAgreementFinancialsBatchResult(charges.Count);
    }

    public async Task AuthorizeReplayAsync(
        ReconcileNativeEsignAgreementFinancialsBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        ReconcileNativeEsignAgreementFinancialsBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.RunToken == Guid.Empty)
            throw new ArgumentException("A native e-sign financial reconciliation run token is required.");
        if (command.BatchSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));

        // This is a system-owned reconciliation batch. The replay check is intentionally explicit:
        // the run token and bounded batch shape must be valid, and the same completed Agreement
        // request surface must be queryable under the active DB context.
        await _db.Set<SignatureRequest>()
            .AsNoTracking()
            .Where(request =>
                request.Status == SignatureRequestStatus.Completed &&
                request.ExecutedArtifactId != null &&
                request.LeaseAgreementId != null &&
                request.LeaseAddendumId == null)
            .Select(request => request.Id)
            .Take(1)
            .ToListAsync(ct);
    }
}
