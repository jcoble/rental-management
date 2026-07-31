using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

public sealed class ApplyAccountingPullResultHandler
    : IAtomicCommandHandler<ApplyAccountingPullResultCommand, ApplyAccountingPullResult>
{
    private readonly RentalCommandDbContext _db;

    public ApplyAccountingPullResultHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ApplyAccountingPullResult> HandleAsync(
        ApplyAccountingPullResultCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.PortfolioId <= 0 || command.AccountingConnectionId <= 0
            || command.PullClaimToken == Guid.Empty
            || string.IsNullOrWhiteSpace(command.ProviderBatchIdentity))
            throw new ArgumentException("A portfolio, connection, pull claim, and provider batch identity are required.");

        await context.AcquireLockAsync(
            "AccountingConnection",
            command.AccountingConnectionId,
            ct);
        var result = await AtomicAccountingPullPersistence.ApplyPullAsync(_db, context, command, ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
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

    public async Task AuthorizeReplayAsync(
        ApplyAccountingPullResultCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.PortfolioId <= 0 || command.AccountingConnectionId <= 0
            || command.PullClaimToken == Guid.Empty
            || string.IsNullOrWhiteSpace(command.ProviderBatchIdentity))
            throw new ArgumentException("A portfolio, connection, pull claim, and provider batch identity are required.");

        var connectionExists = await _db.Set<AccountingConnection>()
            .AsNoTracking()
            .AnyAsync(connection =>
                connection.Id == command.AccountingConnectionId &&
                connection.PortfolioId == command.PortfolioId,
                ct);
        if (!connectionExists)
        {
            throw new UnauthorizedAccessException("The accounting pull connection is unavailable.");
        }
    }
}
