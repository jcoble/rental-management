using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

public sealed class RecordTenantReceiptHandler
    : IAtomicCommandHandler<RecordTenantReceiptCommand, RecordTenantReceiptResult>,
      IAtomicReplayAuthorizer<RecordTenantReceiptCommand>
{
    public async Task<RecordTenantReceiptResult> HandleAsync(
        RecordTenantReceiptCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport.AuthorizedAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new { row.Id, row.Currency })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();

        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, account.Currency, command.Amount, command.PaymentMethodSummary,
            command.ExternalReference, command.PayerName, command.CheckNumber, command.BankName, times.WallClockUtc);
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);

        var receipt = new TenantLedgerEntry
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = command.Amount,
            Currency = account.Currency,
            EffectiveOn = command.EffectiveOn,
            PostedAtUtc = times.WallClockUtc,
            Description = command.Description.Trim(),
            BusinessKey = command.BusinessKey,
            ProviderPaymentAttemptId = paymentAttempt.Id,
            SourceStoredFileId = command.SourceStoredFileId,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(receipt);
        await attempt.FlushBusinessAsync(ct);

        var allocations = command.AllocateOldestCharges
            ? await TenantMoneyCommandSupport.AllocateOldestAsync(
                command.PortfolioId, command.TenantAccountId, receipt.Id, command.Amount,
                command.BusinessKey, command.ActorUserId, times.WallClockUtc, attempt, ct)
            : new AtomicLedgerAllocationSummary();

        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), receipt.Id,
            "Tenant payment receipt recorded", new { receipt.Amount, receipt.EffectiveOn, allocations = allocations.AllocationCount });
        return new RecordTenantReceiptResult(true, account.Id, receipt.Id, paymentAttempt.Id,
            receipt.Amount, allocations.AllocatedAmount, allocations.AllocationCount);
    }

    public Task AuthorizeReplayAsync(RecordTenantReceiptCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class FundSecurityDepositHandler
    : IAtomicCommandHandler<FundSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<FundSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        FundSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                OpenDepositCharges = attempt.Persistence.Query<TenantChargeBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.TenantAccountId == command.TenantAccountId
                        && balance.EntryType == nameof(TenantLedgerEntryType.DepositCharge)
                        && balance.OpenAmount > 0m)
                    .Sum(balance => (decimal?)balance.OpenAmount) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (target.OpenDepositCharges < command.Amount)
        {
            return TenantMoneyCommandSupport.DepositConflict(
                command,
                "The deposit funding amount exceeds the tenant account's open security-deposit charges.");
        }

        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, target.Currency, command.Amount, command.PaymentMethodSummary,
            command.ExternalReference, null, null, null, times.WallClockUtc);
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);

        var receipt = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.PaymentReceipt, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            command.Description, $"{command.BusinessKey}:tenant-receipt", times.WallClockUtc,
            providerAttemptId: paymentAttempt.Id, sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(receipt);
        await attempt.FlushBusinessAsync(ct);

        var allocation = await TenantMoneyCommandSupport.AllocateOldestAsync(
            command.PortfolioId, command.TenantAccountId, receipt.Id, command.Amount,
            $"{command.BusinessKey}:allocation", command.ActorUserId, times.WallClockUtc,
            attempt, ct, TenantLedgerEntryType.DepositCharge);
        if (allocation.AllocatedAmount != command.Amount)
        {
            throw new InvalidOperationException(
                "Security-deposit funding must allocate its full amount to open deposit charges.");
        }

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Receipt, SecurityDepositDirection.Increase,
            command.Amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, times.WallClockUtc, receipt.Id, command.SourceStoredFileId);
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit funded",
            new
            {
                deposit.Amount,
                deposit.EffectiveOn,
                TenantLedgerEntryId = receipt.Id,
                allocation.AllocationCount,
                allocation.AllocatedAmount,
            });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, receipt.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(FundSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

public sealed class DeductSecurityDepositHandler
    : IAtomicCommandHandler<DeductSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<DeductSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        DeductSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = attempt.Persistence.Query<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if ((target.Balance ?? 0m) < command.Amount)
            return TenantMoneyCommandSupport.DepositConflict(command, "The deduction exceeds the held deposit balance.");

        var description = string.IsNullOrWhiteSpace(command.Notes)
            ? command.Reason.Trim()
            : $"{command.Reason.Trim()}: {command.Notes.Trim()}";
        var charge = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit,
            command.Amount, target.Currency, command.EffectiveOn, command.EffectiveOn,
            description, $"{command.BusinessKey}:charge", times.WallClockUtc);
        var credit = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            $"Security deposit applied: {description}", $"{command.BusinessKey}:credit", times.WallClockUtc);
        attempt.Persistence.Add(charge);
        attempt.Persistence.Add(credit);
        await attempt.FlushBusinessAsync(ct);

        attempt.Persistence.Add(new TenantLedgerAllocation
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            DebitEntryId = charge.Id,
            CreditEntryId = credit.Id,
            Amount = command.Amount,
            AllocatedAtUtc = times.WallClockUtc,
            BusinessKey = $"{command.BusinessKey}:allocation",
            CreatedByUserId = command.ActorUserId,
        });
        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Deduction, SecurityDepositDirection.Decrease,
            command.Amount, target.Currency, command.EffectiveOn, description,
            command.BusinessKey, times.WallClockUtc, credit.Id, command.SourceStoredFileId);
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit deduction posted",
            new { deposit.Amount, command.Reason, ChargeEntryId = charge.Id, CreditEntryId = credit.Id });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, charge.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(DeductSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

public sealed class RefundSecurityDepositHandler
    : IAtomicCommandHandler<RefundSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<RefundSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        RefundSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = attempt.Persistence.Query<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        var balance = target.Balance ?? 0m;
        var amount = command.Amount ?? balance;
        if (amount <= 0m || amount > balance)
            return TenantMoneyCommandSupport.DepositConflict(command, "The refund must be positive and cannot exceed the held deposit balance.");

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Refund, SecurityDepositDirection.Decrease,
            amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, times.WallClockUtc, null, null,
            payoutExternalReference: TenantMoneyCommandSupport.Clean(command.ExternalReference));
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit refund posted",
            new { deposit.Amount, deposit.EffectiveOn, deposit.PayoutExternalReference });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, null, amount, null);
    }

    public Task AuthorizeReplayAsync(RefundSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

internal static class TenantMoneyCommandSupport
{
    internal static IQueryable<TenantAccount> AuthorizedAccounts(
        ITenantMoneyCommand command, IAtomicPersistenceSession persistence, DateTime securityNowUtc)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));
        return persistence.Query<TenantAccount>().Where(account =>
            account.Id == command.TenantAccountId && account.PortfolioId == command.PortfolioId
            && account.ClosedAtUtc == null && account.LeaseManagement != null
            && persistence.Query<AuthSession>().Any(session => session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && persistence.Query<WorkspaceAccessContext>().Any(context => context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment => assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope => scope.PortfolioId == command.PortfolioId
                                && scope.PropertyId == account.LeaseManagement.PropertyId)))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == command.RequiredCapability))));
    }

    internal static IQueryable<SecurityDepositAccount> AuthorizedDepositAccounts(
        ISecurityDepositMoneyCommand command, IAtomicPersistenceSession persistence, DateTime securityNowUtc) =>
        from deposit in persistence.Query<SecurityDepositAccount>()
        join account in AuthorizedAccounts(command, persistence, securityNowUtc)
            on new { deposit.TenantAccountId, deposit.PortfolioId }
            equals new { TenantAccountId = account.Id, account.PortfolioId }
        where deposit.Id == command.SecurityDepositAccountId
        select deposit;

    internal static async Task AuthorizeReplayAsync(ITenantMoneyCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedAccounts(command, persistence, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static async Task AuthorizeDepositReplayAsync(ISecurityDepositMoneyCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedDepositAccounts(command, persistence, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static void Validate(ITenantMoneyCommand command)
    {
        if (command.PortfolioId <= 0 || command.TenantAccountId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequiredCapability)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new ArgumentException("Portfolio, tenant account, actor, access, capability, and operation key are required.");
        if (command is ISecurityDepositMoneyCommand deposit && deposit.SecurityDepositAccountId <= 0)
            throw new ArgumentException("Security deposit account is required.");
        var amount = command switch
        {
            RecordTenantReceiptCommand receiptCommand => receiptCommand.Amount,
            FundSecurityDepositCommand fund => fund.Amount,
            DeductSecurityDepositCommand deduction => deduction.Amount,
            RefundSecurityDepositCommand refund => refund.Amount ?? 1m,
            _ => 0m,
        };
        if (amount <= 0m) throw new ArgumentException("Amount must be positive.");
        if (command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException("Operation key cannot exceed 200 characters.");
        if (command is RecordTenantReceiptCommand receipt
            && (receipt.EffectiveOn == default || string.IsNullOrWhiteSpace(receipt.Description)
                || receipt.Description.Trim().Length > 500
                || string.IsNullOrWhiteSpace(receipt.PaymentMethodSummary)
                || receipt.PaymentMethodSummary.Trim().Length > 200))
            throw new ArgumentException("Receipt date, description, and payment method are required and must fit their limits.");
        if (command is RefundSecurityDepositCommand payout
            && payout.ExternalReference?.Trim().Length > 200)
            throw new ArgumentException("Payout reference cannot exceed 200 characters.");
    }

    internal static TenantPaymentAttempt ManualAttempt(ITenantMoneyCommand command, string currency,
        decimal amount, string method, string? externalReference, string? payerName,
        string? checkNumber, string? bankName, DateTime now) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        Provider = "manual",
        ProviderObjectId = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim(),
        IdempotencyKey = command.DeliveryIdempotencyKey,
        AttemptType = TenantPaymentAttemptType.Charge,
        State = TenantPaymentAttemptState.Succeeded,
        Amount = amount,
        Currency = currency,
        PaymentMethodSummary = method.Trim(),
        PayerName = payerName?.Trim(),
        CheckNumber = checkNumber?.Trim(),
        BankName = bankName?.Trim(),
        PreparedAtUtc = now,
        SubmittedAtUtc = now,
        SettledAtUtc = now,
        UpdatedAtUtc = now,
        AttemptCount = 1,
        CreatedByUserId = command.ActorUserId,
    };

    internal static TenantLedgerEntry Ledger(ITenantMoneyCommand command,
        TenantLedgerEntryType type, TenantLedgerDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, DateOnly? dueOn, string description,
        string businessKey, DateTime now, long? providerAttemptId = null,
        int? sourceStoredFileId = null) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        DueOn = dueOn,
        PostedAtUtc = now,
        Description = description.Trim(),
        BusinessKey = businessKey,
        ProviderPaymentAttemptId = providerAttemptId,
        SourceStoredFileId = sourceStoredFileId,
        CreatedByUserId = command.ActorUserId,
    };

    internal static SecurityDepositEntry DepositEntry(ISecurityDepositMoneyCommand command,
        SecurityDepositEntryType type, SecurityDepositDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, string description, string businessKey,
        DateTime now, long? tenantLedgerEntryId, int? sourceStoredFileId,
        string? payoutExternalReference = null) => new()
    {
        PortfolioId = command.PortfolioId,
        SecurityDepositAccountId = command.SecurityDepositAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        PostedAtUtc = now,
        BusinessKey = businessKey,
        Description = description.Trim(),
        TenantLedgerEntryId = tenantLedgerEntryId,
        SourceStoredFileId = sourceStoredFileId,
        PayoutExternalReference = payoutExternalReference,
        CreatedByUserId = command.ActorUserId,
    };

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static Task<AtomicLedgerAllocationSummary> AllocateOldestAsync(
        int portfolioId, int accountId, long receiptId, decimal available, string businessKey,
        int actorUserId, DateTime now, IAtomicWriteAttempt attempt, CancellationToken ct,
        TenantLedgerEntryType? onlyType = null)
    {
        return attempt.TenantMoney.AllocateOldestChargesAsync(
            portfolioId, accountId, receiptId, available, businessKey,
            actorUserId, now, onlyType?.ToString(), ct);
    }

    internal static void StageMutation(IAtomicWriteAttempt attempt, ITenantMoneyCommand command,
        DateTime now, string entityType, long entityId, string reason, object values)
    {
        attempt.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(TenantAccount),
            command.TenantAccountId, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(values), ChangeReason: reason), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType, entityId,
                data = new { command.TenantAccountId, mutation = reason } }),
            IdempotencyKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    internal static SecurityDepositMutationResult DepositConflict(
        ISecurityDepositMoneyCommand command, string error) => new(true, false,
            command.TenantAccountId, command.SecurityDepositAccountId, 0, null, 0, error);

    internal static UnauthorizedAccessException Unauthorized() => new(
        "The tenant account is not authorized in the current access context, capability, and property scope.");
}
