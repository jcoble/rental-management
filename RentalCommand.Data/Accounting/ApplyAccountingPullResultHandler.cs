using System.Text.Json;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

public sealed class ApplyAccountingPullResultHandler
    : IAtomicCommandHandler<ApplyAccountingPullResultCommand, ApplyAccountingPullResult>
{
    public async Task<ApplyAccountingPullResult> HandleAsync(
        ApplyAccountingPullResultCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.PortfolioId <= 0 || command.AccountingConnectionId <= 0
            || command.PullClaimToken == Guid.Empty
            || string.IsNullOrWhiteSpace(command.ProviderBatchIdentity))
            throw new ArgumentException("A portfolio, connection, pull claim, and provider batch identity are required.");

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AccountingConnection,
            command.AccountingConnectionId,
            ct);
        var result = await attempt.Accounting.ApplyPullAsync(command, ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            "AccountingPull",
            command.AccountingConnectionId,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new
            {
                command.ProviderBatchIdentity,
                command.PullClaimToken,
                result.CustomersMapped,
                result.VendorsMapped,
                result.AccountsMapped,
                result.PaymentsImported,
                result.ExpensesImported,
                result.NeedsReview,
            }),
            ChangeReason: "Bounded provider pull applied and claim released atomically."));
        return result;
    }
}
