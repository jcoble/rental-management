using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

public sealed class ReconcileNativeEsignAgreementFinancialsHandler
    : IAtomicCommandHandler<ReconcileNativeEsignAgreementFinancialsCommand,
        ReconcileNativeEsignAgreementFinancialsResult>
{
    public async Task<ReconcileNativeEsignAgreementFinancialsResult> HandleAsync(
        ReconcileNativeEsignAgreementFinancialsCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.SignatureRequestId <= 0 || command.PublicId == Guid.Empty)
            throw new ArgumentException("The native e-sign reconciliation command is incomplete.");

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.SignatureRequest, command.SignatureRequestId, ct);

        var request = await attempt.Persistence.Query<SignatureRequest>()
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

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, request.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(request.PortfolioId, ct);
        var inserted = await attempt.Leasing.ReconcileInitialSecurityDepositChargeAsync(
            request.PortfolioId, request.LeaseAgreementId, times.EffectiveNowUtc, ct);

        return new(
            request.PublicId,
            request.Id,
            request.LeaseAgreementId,
            inserted);
    }
}

public sealed class ReconcileNativeEsignAgreementFinancialsBatchHandler
    : IAtomicCommandHandler<ReconcileNativeEsignAgreementFinancialsBatchCommand,
        ReconcileNativeEsignAgreementFinancialsBatchResult>
{
    public async Task<ReconcileNativeEsignAgreementFinancialsBatchResult> HandleAsync(
        ReconcileNativeEsignAgreementFinancialsBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.RunToken == Guid.Empty)
            throw new ArgumentException("A native e-sign financial reconciliation run token is required.");
        if (command.BatchSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));

        var charges = await attempt.Leasing
            .ReconcileCompletedNativeEsignInitialSecurityDepositChargesAsync(
                command.BatchSize, ct);
        return new ReconcileNativeEsignAgreementFinancialsBatchResult(charges.Count);
    }
}
