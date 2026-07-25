using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Banking;

public sealed class PreparePlaidTokenExchangeHandler
    : IAtomicCommandHandler<PreparePlaidTokenExchangeCommand, PreparePlaidTokenExchangeResult>
{
    public async Task<PreparePlaidTokenExchangeResult> HandleAsync(
        PreparePlaidTokenExchangeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.BankConnection,
            ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "plaid-exchange", command.ClientOperationId),
            ct);
        var existing = await attempt.Persistence.Query<PlaidTokenExchangeAttempt>()
            .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                && row.ClientOperationId == command.ClientOperationId, ct);
        if (existing is not null)
        {
            return new PreparePlaidTokenExchangeResult(
                existing.RequestHash == command.RequestHash
                    ? PreparePlaidTokenExchangeOutcome.Existing
                    : PreparePlaidTokenExchangeOutcome.Conflict,
                existing.Id);
        }

        var exchange = new PlaidTokenExchangeAttempt
        {
            Id = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            ClientOperationId = command.ClientOperationId,
            RequestHash = command.RequestHash,
            PublicTokenHash = command.PublicTokenHash,
            InstitutionName = command.InstitutionName,
            AccountName = command.AccountName,
            AccountMask = command.AccountMask,
            AccountType = command.AccountType,
            AccountSubtype = command.AccountSubtype,
            ExternalAccountIdCipherText = command.ExternalAccountIdCipherText,
            ExternalAccountIdHash = command.ExternalAccountIdHash,
            Status = "Prepared",
            PreparedAtUtc = command.PreparedAtUtc,
        };
        attempt.Persistence.Add(exchange);
        return new PreparePlaidTokenExchangeResult(PreparePlaidTokenExchangeOutcome.Prepared, exchange.Id);
    }
}

public sealed class AdmitPlaidTokenExchangeHandler
    : IAtomicCommandHandler<AdmitPlaidTokenExchangeCommand, AdmitPlaidTokenExchangeResult>
{
    public async Task<AdmitPlaidTokenExchangeResult> HandleAsync(
        AdmitPlaidTokenExchangeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankConnection, command.ExchangeAttemptId, ct);
        var exchange = await attempt.Persistence.Query<PlaidTokenExchangeAttempt>()
            .SingleOrDefaultAsync(row => row.Id == command.ExchangeAttemptId
                && row.PortfolioId == command.PortfolioId, ct);
        if (exchange is null) return Result(AdmitPlaidTokenExchangeOutcome.NotFound, command);
        if (exchange.CompletedAtUtc is not null) return Result(AdmitPlaidTokenExchangeOutcome.Completed, command);
        if (exchange.RemoteReceiptRecordedAtUtc is not null)
            return Result(AdmitPlaidTokenExchangeOutcome.ReceiptRecorded, command);
        if (exchange.RemoteAdmittedAtUtc is not null)
            return Result(AdmitPlaidTokenExchangeOutcome.AlreadyAdmitted, command);
        exchange.RemoteAdmittedAtUtc = command.AdmittedAtUtc;
        exchange.Status = "RemoteAdmitted";
        return Result(AdmitPlaidTokenExchangeOutcome.Admitted, command);
    }

    private static AdmitPlaidTokenExchangeResult Result(
        AdmitPlaidTokenExchangeOutcome outcome,
        AdmitPlaidTokenExchangeCommand command) => new(outcome, command.ExchangeAttemptId);
}

public sealed class RecordPlaidTokenExchangeReceiptHandler
    : IAtomicCommandHandler<RecordPlaidTokenExchangeReceiptCommand, RecordPlaidTokenExchangeReceiptResult>
{
    public async Task<RecordPlaidTokenExchangeReceiptResult> HandleAsync(
        RecordPlaidTokenExchangeReceiptCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankConnection, command.ExchangeAttemptId, ct);
        var exchange = await attempt.Persistence.Query<PlaidTokenExchangeAttempt>()
            .SingleOrDefaultAsync(row => row.Id == command.ExchangeAttemptId
                && row.PortfolioId == command.PortfolioId, ct);
        if (exchange is null) return Result(RecordPlaidTokenExchangeReceiptOutcome.NotFound, command);
        if (exchange.RemoteAdmittedAtUtc is null)
            return Result(RecordPlaidTokenExchangeReceiptOutcome.NotAdmitted, command);
        if (exchange.RemoteReceiptRecordedAtUtc is not null)
        {
            if (exchange.ProviderRequestIdentity != command.ProviderRequestIdentity
                || exchange.ExternalItemIdHash != command.ExternalItemIdHash)
            {
                throw new AtomicReceiptInvariantException(
                    $"Plaid exchange attempt {exchange.Id} is already bound to a different provider receipt.");
            }
            return Result(RecordPlaidTokenExchangeReceiptOutcome.AlreadyRecorded, command);
        }
        exchange.ProviderRequestIdentity = command.ProviderRequestIdentity;
        exchange.ExternalItemIdCipherText = command.ExternalItemIdCipherText;
        exchange.ExternalItemIdHash = command.ExternalItemIdHash;
        exchange.ExternalAccessTokenCipherText = command.ExternalAccessTokenCipherText;
        exchange.RemoteReceiptRecordedAtUtc = command.RecordedAtUtc;
        exchange.Status = "RemoteReceiptRecorded";
        return Result(RecordPlaidTokenExchangeReceiptOutcome.Recorded, command);
    }

    private static RecordPlaidTokenExchangeReceiptResult Result(
        RecordPlaidTokenExchangeReceiptOutcome outcome,
        RecordPlaidTokenExchangeReceiptCommand command) => new(outcome, command.ExchangeAttemptId);
}

public sealed class ApplyPlaidConnectionHandler
    : IAtomicCommandHandler<ApplyPlaidConnectionCommand, ApplyPlaidConnectionResult>
{
    public async Task<ApplyPlaidConnectionResult> HandleAsync(
        ApplyPlaidConnectionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankConnection, command.ExchangeAttemptId, ct);
        var exchange = await attempt.Persistence.Query<PlaidTokenExchangeAttempt>()
            .SingleOrDefaultAsync(row => row.Id == command.ExchangeAttemptId
                && row.PortfolioId == command.PortfolioId, ct)
            ?? throw new AtomicReceiptInvariantException(
                $"Plaid exchange attempt {command.ExchangeAttemptId} was not found.");
        if (exchange.RemoteReceiptRecordedAtUtc is null
            || exchange.ExternalItemIdCipherText is null
            || exchange.ExternalItemIdHash is null
            || exchange.ExternalAccessTokenCipherText is null
            || exchange.ProviderRequestIdentity is null)
        {
            throw new AtomicReceiptInvariantException(
                $"Plaid exchange attempt {exchange.Id} has no durable remote receipt.");
        }
        if (exchange.CompletedAtUtc is not null && exchange.BankConnectionId is int completedConnectionId)
            return new ApplyPlaidConnectionResult(completedConnectionId, false);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.BankConnection,
            StableGuid(command.PortfolioId, "Plaid", exchange.ExternalItemIdHash, exchange.ExternalAccountIdHash),
            ct);

        var connection = await attempt.Persistence.Query<BankConnection>()
            .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                && row.Provider == "Plaid"
                && row.ExternalItemIdHash == exchange.ExternalItemIdHash
                && row.ExternalAccountIdHash == exchange.ExternalAccountIdHash, ct);
        var created = connection is null;
        var before = connection is null ? null : Snapshot(connection);
        connection ??= new BankConnection
        {
            PortfolioId = command.PortfolioId,
            Provider = "Plaid",
            CreatedAt = command.AppliedAtUtc,
        };
        if (created) attempt.Persistence.Add(connection);

        connection.InstitutionName = exchange.InstitutionName;
        connection.AccountName = exchange.AccountName;
        connection.AccountMask = exchange.AccountMask;
        connection.AccountType = exchange.AccountType;
        connection.AccountSubtype = exchange.AccountSubtype;
        connection.ExternalItemIdCipherText = exchange.ExternalItemIdCipherText;
        connection.ExternalAccountIdCipherText = exchange.ExternalAccountIdCipherText;
        connection.ExternalItemIdHash = exchange.ExternalItemIdHash;
        connection.ExternalAccountIdHash = exchange.ExternalAccountIdHash;
        connection.ExternalAccessTokenCipherText = exchange.ExternalAccessTokenCipherText;
        connection.Status = "Active";
        connection.UpdatedAt = command.AppliedAtUtc;

        await attempt.FlushBusinessAsync(ct);
        exchange.BankConnectionId = connection.Id;
        exchange.CompletedAtUtc = command.AppliedAtUtc;
        exchange.Status = "Completed";
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            created ? AuditLogOperation.Created : AuditLogOperation.Updated,
            OldValues: before,
            NewValues: Snapshot(connection),
            ChangeReason: created
                ? $"Bank connection created from Plaid request {exchange.ProviderRequestIdentity}."
                : $"Bank connection relinked from Plaid request {exchange.ProviderRequestIdentity}."));
        return new ApplyPlaidConnectionResult(connection.Id, created);
    }

    internal static Guid StableGuid(params object?[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values)));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static string Snapshot(BankConnection connection) => JsonSerializer.Serialize(new
    {
        provider = connection.Provider,
        institutionName = connection.InstitutionName,
        accountName = connection.AccountName,
        accountMask = connection.AccountMask,
        accountType = connection.AccountType,
        accountSubtype = connection.AccountSubtype,
        status = connection.Status,
        lastSyncedAt = connection.LastSyncedAt,
    });
}

public sealed class ApplyPlaidSyncHandler
    : IAtomicCommandHandler<ApplyPlaidSyncCommand, ApplyPlaidSyncResult>
{
    private const int MaxBatch = 500;

    public async Task<ApplyPlaidSyncResult> HandleAsync(
        ApplyPlaidSyncCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.AddedInputCount < command.Added.Count
            || command.ModifiedInputCount < command.Modified.Count
            || command.AddedInputCount > MaxBatch
            || command.ModifiedInputCount > MaxBatch
            || command.Added.Count > MaxBatch
            || command.Modified.Count > MaxBatch
            || command.RemovedProviderTransactionIds.Count > MaxBatch)
        {
            throw new InvalidOperationException($"A Plaid sync result cannot exceed {MaxBatch} rows per change set.");
        }

        await attempt.Locking.AcquireAsync(AtomicLockResource.BankConnection, command.ConnectionId, ct);
        var connection = await attempt.Persistence.Query<BankConnection>()
            .SingleOrDefaultAsync(row => row.Id == command.ConnectionId
                && row.PortfolioId == command.PortfolioId
                && row.Provider == "Plaid", ct);
        if (connection is null)
        {
            return Empty(ApplyPlaidSyncOutcome.ConnectionNotFound, command.ConnectionId);
        }
        if (!string.Equals(connection.SyncCursorCipherText, command.ExpectedCursorCipherText, StringComparison.Ordinal))
        {
            return Empty(ApplyPlaidSyncOutcome.StaleCursor, connection.Id);
        }

        var beforeConnection = ApplyPlaidConnectionHandler.Snapshot(connection);
        var merge = await attempt.Banking.ApplyPlaidSyncAsync(
            command.PortfolioId,
            connection.Id,
            command.Added,
            command.AddedInputCount,
            command.Modified,
            command.ModifiedInputCount,
            command.RemovedProviderTransactionIds,
            command.AppliedAtUtc,
            ct);

        connection.SyncCursorCipherText = command.NextCursorCipherText;
        connection.LastSyncedAt = command.AppliedAtUtc;
        connection.UpdatedAt = command.AppliedAtUtc;
        await attempt.FlushBusinessAsync(ct);

        foreach (var mutation in merge.Mutations)
        {
            attempt.StageSemanticEvent(TransactionAudit(command.PortfolioId, mutation));
        }
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            AuditLogOperation.Updated,
            OldValues: beforeConnection,
            NewValues: ApplyPlaidConnectionHandler.Snapshot(connection),
            ChangeReason: $"Plaid sync {command.ProviderRequestIdentity} committed: {merge.ImportedCount} imported, {merge.ModifiedCount} modified, {merge.RemovedCount} removed."));
        StageNotification(attempt, command.PortfolioId, connection.Id, merge.ImportedCount, merge.ChangedEventCount, command.AppliedAtUtc);

        return new ApplyPlaidSyncResult(
            ApplyPlaidSyncOutcome.Applied,
            connection.Id,
            merge.ImportedCount,
            merge.SkippedCount,
            merge.AffectedTransactionIds);
    }

    private static ApplyPlaidSyncResult Empty(ApplyPlaidSyncOutcome outcome, int connectionId) =>
        new(outcome, connectionId, 0, 0, []);

    internal static string Snapshot(BankTransaction row) => JsonSerializer.Serialize(new
    {
        providerTransactionId = row.ProviderTransactionId,
        postedAt = row.PostedAt,
        authorizedAt = row.AuthorizedAt,
        description = row.Description,
        merchantName = row.MerchantName,
        amount = row.Amount,
        isoCurrencyCode = row.IsoCurrencyCode,
        category = row.Category,
        propertyId = row.PropertyId,
        matchedTenantAccountId = row.MatchedTenantAccountId,
        matchedTenantLedgerEntryId = row.MatchedTenantLedgerEntryId,
        matchedExpenseId = row.MatchedExpenseId,
        matchStatus = row.MatchStatus,
        matchConfidence = row.MatchConfidence,
        notes = row.Notes,
    });

    internal static AtomicSemanticAudit TransactionAudit(
        int portfolioId,
        BankTransaction row,
        AuditLogOperation operation,
        string? oldValues,
        string reason) => new(
            portfolioId,
            nameof(BankTransaction),
            row.Id,
            operation,
            OldValues: oldValues,
            NewValues: Snapshot(row),
            ChangeReason: reason);

    internal static AtomicSemanticAudit TransactionAudit(
        int portfolioId,
        AtomicBankTransactionMutation mutation) => new(
            portfolioId,
            nameof(BankTransaction),
            mutation.TransactionId,
            mutation.Operation == "Created" ? AuditLogOperation.Created : AuditLogOperation.Updated,
            OldValues: mutation.OldValues,
            NewValues: mutation.NewValues,
            ChangeReason: mutation.Reason);

    private static void StageNotification(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        int connectionId,
        int imported,
        int changed,
        DateTime now)
    {
        if (imported == 0 && changed == 0) return;
        attempt.Persistence.Add(new Notification
        {
            PortfolioId = portfolioId,
            Type = "BankSyncCompleted",
            Title = "Bank activity updated",
            Message = $"{imported} new and {changed} updated bank transactions are ready.",
            Severity = "Info",
            RelatedEntityType = nameof(BankConnection),
            RelatedEntityId = connectionId,
            CreatedAt = now,
        });
    }
}

public sealed class ImportBankTransactionsHandler
    : IAtomicCommandHandler<ImportBankTransactionsCommand, ImportBankTransactionsResult>
{
    private const int MaxBatch = 500;

    public async Task<ImportBankTransactionsResult> HandleAsync(
        ImportBankTransactionsCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.InputCount <= 0
            || command.InputCount > MaxBatch
            || command.InputCount < command.Transactions.Count
            || command.Transactions.Count > MaxBatch)
        {
            throw new InvalidOperationException($"A bank import must contain between 1 and {MaxBatch} rows.");
        }

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.BankConnection,
            ApplyPlaidConnectionHandler.StableGuid(
                command.PortfolioId, command.Provider, command.InstitutionName, command.AccountName, command.AccountMask),
            ct);
        var connection = await attempt.Persistence.Query<BankConnection>()
            .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider
                && row.InstitutionName == command.InstitutionName
                && row.AccountName == command.AccountName
                && row.AccountMask == command.AccountMask, ct);
        var createdConnection = connection is null;
        var connectionBefore = connection is null ? null : ApplyPlaidConnectionHandler.Snapshot(connection);
        connection ??= new BankConnection
        {
            PortfolioId = command.PortfolioId,
            Provider = command.Provider,
            InstitutionName = command.InstitutionName,
            AccountName = command.AccountName,
            AccountMask = command.AccountMask,
            AccountType = command.AccountType,
            AccountSubtype = command.AccountSubtype,
            Status = "Active",
            CreatedAt = command.ImportedAtUtc,
        };
        if (createdConnection) attempt.Persistence.Add(connection);

        if (createdConnection)
        {
            await attempt.FlushBusinessAsync(ct);
        }
        var merge = await attempt.Banking.ImportAsync(
            command.PortfolioId,
            connection.Id,
            command.Transactions,
            command.InputCount,
            command.ImportedAtUtc,
            ct);
        connection.LastSyncedAt = command.ImportedAtUtc;
        connection.UpdatedAt = command.ImportedAtUtc;
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            createdConnection ? AuditLogOperation.Created : AuditLogOperation.Updated,
            OldValues: connectionBefore,
            NewValues: ApplyPlaidConnectionHandler.Snapshot(connection),
            ChangeReason: $"Bank import {command.RequestIdentity} committed."));
        foreach (var mutation in merge.Mutations)
        {
            attempt.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(command.PortfolioId, mutation));
        }
        if (merge.ImportedCount > 0)
        {
            attempt.Persistence.Add(new Notification
            {
                PortfolioId = command.PortfolioId,
                Type = "BankImportCompleted",
                Title = "Bank transactions imported",
                Message = $"{merge.ImportedCount} bank transactions are ready for review.",
                Severity = "Info",
                RelatedEntityType = nameof(BankConnection),
                RelatedEntityId = connection.Id,
                CreatedAt = command.ImportedAtUtc,
            });
        }
        return new ImportBankTransactionsResult(
            connection.Id,
            merge.ImportedCount,
            merge.SkippedCount,
            merge.AffectedTransactionIds);
    }
}

public sealed class ReconcileBankTransactionHandler
    : IAtomicCommandHandler<ReconcileBankTransactionCommand, ReconcileBankTransactionResult>,
      IAtomicReplayAuthorizer<ReconcileBankTransactionCommand>
{
    public async Task<ReconcileBankTransactionResult> HandleAsync(
        ReconcileBankTransactionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankTransaction, command.TransactionId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var transaction = await attempt.Persistence.Query<BankTransaction>()
            .SingleOrDefaultAsync(row => row.Id == command.TransactionId
                && row.PortfolioId == command.PortfolioId, ct);
        if (transaction is null) return Result(ReconcileBankTransactionOutcome.TransactionNotFound, command.TransactionId);
        if (transaction.PropertyId is null)
            return Result(ReconcileBankTransactionOutcome.RouteRequired, transaction.Id);
        if (!await BankingAuthorizationSupport.HasPropertyAuthorityAsync(
                command.PortfolioId, transaction.PropertyId.Value, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                command.RequiredCapability, attempt.Persistence, now, ct))
        {
            throw new UnauthorizedAccessException();
        }
        if (transaction.UpdatedAt != command.ExpectedUpdatedAtUtc)
        {
            return Result(ReconcileBankTransactionOutcome.StaleVersion, transaction.Id);
        }

        if (!await TargetExistsAsync(command, transaction.PropertyId.Value, attempt, ct))
        {
            return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
        }
        if (IsAlreadyApplied(command, transaction))
        {
            return Result(ReconcileBankTransactionOutcome.AlreadyApplied, transaction.Id);
        }
        var before = ApplyPlaidSyncHandler.Snapshot(transaction);
        Apply(command, transaction);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
            command.PortfolioId,
            transaction,
            AuditLogOperation.Updated,
            before,
            Reason(command)));
        return Result(ReconcileBankTransactionOutcome.Applied, transaction.Id);
    }

    public async Task AuthorizeReplayAsync(
        ReconcileBankTransactionCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var propertyId = await persistence.Query<BankTransaction>()
            .Where(row => row.Id == command.TransactionId && row.PortfolioId == command.PortfolioId)
            .Select(row => row.PropertyId)
            .SingleOrDefaultAsync(ct);
        if (propertyId is null || !await BankingAuthorizationSupport.HasPropertyAuthorityAsync(
                command.PortfolioId, propertyId.Value, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                command.RequiredCapability, persistence, now, ct))
            throw new UnauthorizedAccessException();
    }

    private static async Task<bool> TargetExistsAsync(
        ReconcileBankTransactionCommand command,
        int propertyId,
        IAtomicWriteAttempt attempt,
        CancellationToken ct) => command.Action switch
        {
            BankReconciliationAction.MatchReceipt
                when command.TenantAccountId is { } accountId
                    && command.TenantLedgerEntryId is { } ledgerEntryId
                    && command.ExpenseId is null =>
                await attempt.Persistence.Query<TenantLedgerEntry>().AnyAsync(row =>
                    row.Id == ledgerEntryId
                    && row.TenantAccountId == accountId
                    && row.PortfolioId == command.PortfolioId
                    && row.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && row.Direction == TenantLedgerDirection.Credit
                    && row.TenantAccount!.LeaseManagement!.PropertyId == propertyId, ct),
            BankReconciliationAction.MatchExpense
                when command.ExpenseId is { } expenseId
                    && command.TenantAccountId is null
                    && command.TenantLedgerEntryId is null =>
                await attempt.Persistence.Query<Expense>().AnyAsync(row => row.Id == expenseId
                    && row.PortfolioId == command.PortfolioId
                    && row.DeletedAt == null
                    && (row.PropertyId == propertyId
                        || (row.PropertyId == null && row.Unit!.PropertyId == propertyId)
                        || (row.PropertyId == null && row.UnitId == null
                            && row.WorkOrder!.PropertyId == propertyId)), ct),
            BankReconciliationAction.MatchReceipt or BankReconciliationAction.MatchExpense => false,
            _ => command.TenantAccountId is null
                && command.TenantLedgerEntryId is null
                && command.ExpenseId is null,
        };

    private static void Apply(ReconcileBankTransactionCommand command, BankTransaction row)
    {
        row.MatchedTenantAccountId = command.Action == BankReconciliationAction.MatchReceipt
            ? command.TenantAccountId
            : null;
        row.MatchedTenantLedgerEntryId = command.Action == BankReconciliationAction.MatchReceipt
            ? command.TenantLedgerEntryId
            : null;
        row.MatchedExpenseId = command.Action == BankReconciliationAction.MatchExpense ? command.ExpenseId : null;
        row.MatchStatus = command.Action switch
        {
            BankReconciliationAction.MatchReceipt or BankReconciliationAction.MatchExpense => "Matched",
            BankReconciliationAction.Clear => "Unmatched",
            BankReconciliationAction.Dismiss => "Dismissed",
            BankReconciliationAction.Ignore => "Removed",
            _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
        };
        row.MatchConfidence = command.Action is BankReconciliationAction.MatchReceipt or BankReconciliationAction.MatchExpense
            ? 1m
            : null;
        if (command.Action == BankReconciliationAction.Ignore)
        {
            row.Notes = "Marked personal / ignored by the landlord.";
        }
        row.UpdatedAt = command.AppliedAtUtc;
    }

    private static bool IsAlreadyApplied(ReconcileBankTransactionCommand command, BankTransaction row) =>
        command.Action switch
        {
            BankReconciliationAction.MatchReceipt =>
                row.MatchStatus == "Matched"
                && row.MatchedTenantAccountId == command.TenantAccountId
                && row.MatchedTenantLedgerEntryId == command.TenantLedgerEntryId
                && row.MatchedExpenseId is null,
            BankReconciliationAction.MatchExpense =>
                row.MatchStatus == "Matched"
                && row.MatchedExpenseId == command.ExpenseId
                && row.MatchedTenantAccountId is null
                && row.MatchedTenantLedgerEntryId is null,
            BankReconciliationAction.Clear =>
                row.MatchStatus == "Unmatched"
                && row.MatchedTenantAccountId is null
                && row.MatchedTenantLedgerEntryId is null
                && row.MatchedExpenseId is null,
            BankReconciliationAction.Dismiss =>
                row.MatchStatus == "Dismissed"
                && row.MatchedTenantAccountId is null
                && row.MatchedTenantLedgerEntryId is null
                && row.MatchedExpenseId is null,
            BankReconciliationAction.Ignore =>
                row.MatchStatus == "Removed"
                && row.MatchedTenantAccountId is null
                && row.MatchedTenantLedgerEntryId is null
                && row.MatchedExpenseId is null,
            _ => false,
        };

    private static string Reason(ReconcileBankTransactionCommand command) => command.Action switch
    {
        BankReconciliationAction.MatchReceipt =>
            $"Bank transaction matched to tenant account #{command.TenantAccountId} receipt #{command.TenantLedgerEntryId}.",
        BankReconciliationAction.MatchExpense => $"Bank transaction matched to Expense #{command.ExpenseId}.",
        BankReconciliationAction.Clear => "Bank transaction match cleared.",
        BankReconciliationAction.Dismiss => "Bank suggested match dismissed.",
        BankReconciliationAction.Ignore => "Bank transaction ignored as personal / not business.",
        _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
    };

    private static ReconcileBankTransactionResult Result(ReconcileBankTransactionOutcome outcome, int id) =>
        new(outcome, id);
}

public sealed class RouteBankTransactionHandler
    : IAtomicCommandHandler<RouteBankTransactionCommand, RouteBankTransactionResult>,
      IAtomicReplayAuthorizer<RouteBankTransactionCommand>
{
    public async Task<RouteBankTransactionResult> HandleAsync(
        RouteBankTransactionCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankTransaction, command.TransactionId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);

        var transaction = await attempt.Persistence.Query<BankTransaction>()
            .SingleOrDefaultAsync(row => row.Id == command.TransactionId
                && row.PortfolioId == command.PortfolioId, ct);
        if (transaction is null) return Result(RouteBankTransactionOutcome.TransactionNotFound, command.TransactionId);
        await AuthorizeAsync(command, transaction.PropertyId, attempt.Persistence, now, ct);
        if (transaction.UpdatedAt != command.ExpectedUpdatedAtUtc)
            return Result(RouteBankTransactionOutcome.StaleVersion, transaction.Id);
        if (transaction.PropertyId == command.PropertyId)
            return Result(RouteBankTransactionOutcome.AlreadyApplied, transaction.Id);

        var before = ApplyPlaidSyncHandler.Snapshot(transaction);
        transaction.PropertyId = command.PropertyId;
        transaction.UpdatedAt = command.AppliedAtUtc;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
            command.PortfolioId, transaction, AuditLogOperation.Updated, before,
            command.PropertyId is null
                ? "Bank transaction operational route removed."
                : $"Bank transaction routed to Property #{command.PropertyId}."));
        return Result(RouteBankTransactionOutcome.Applied, transaction.Id);
    }

    public async Task AuthorizeReplayAsync(
        RouteBankTransactionCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var current = await persistence.Query<BankTransaction>()
            .Where(row => row.Id == command.TransactionId && row.PortfolioId == command.PortfolioId)
            .Select(row => new { row.PropertyId })
            .SingleOrDefaultAsync(ct);
        if (current is null) throw new UnauthorizedAccessException();
        await AuthorizeAsync(command, current.PropertyId, persistence, now, ct);
    }

    private static async Task AuthorizeAsync(
        RouteBankTransactionCommand command,
        int? currentPropertyId,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        if (command.PropertyId is not { } targetPropertyId)
        {
            var canRemoveRoute = await BankingAuthorizationSupport.HasWorkspaceAuthorityAsync(
                command.PortfolioId, command.ActorUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision,
                CapabilityKeys.MoneyReconciliationDestructive, persistence, now, ct);
            if (!canRemoveRoute) throw new UnauthorizedAccessException();
            return;
        }

        var requiredPropertyIds = currentPropertyId is { } sourcePropertyId && sourcePropertyId != targetPropertyId
            ? new[] { sourcePropertyId, targetPropertyId }
            : new[] { targetPropertyId };
        var canRoute = await BankingAuthorizationSupport.HasPropertyAuthoritiesAsync(
            command.PortfolioId, requiredPropertyIds, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision,
            CapabilityKeys.MoneyReconciliationOperate, persistence, now, ct);
        if (!canRoute) throw new UnauthorizedAccessException();
    }

    private static RouteBankTransactionResult Result(RouteBankTransactionOutcome outcome, int id) => new(outcome, id);
}

internal static class BankingAuthorizationSupport
{
    internal static void Validate(ReconcileBankTransactionCommand command)
    {
        Validate(command.PortfolioId, command.TransactionId, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, command.RequiredCapability,
            command.OperationKey);
    }

    internal static void Validate(RouteBankTransactionCommand command)
    {
        Validate(command.PortfolioId, command.TransactionId, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision,
            command.PropertyId is null
                ? CapabilityKeys.MoneyReconciliationDestructive
                : CapabilityKeys.MoneyReconciliationOperate,
            command.OperationKey);
    }

    private static void Validate(int portfolioId, int transactionId, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision, string capability, string operationKey)
    {
        if (portfolioId <= 0 || transactionId <= 0 || actorUserId <= 0 || authSessionId == Guid.Empty
            || accessContextId <= 0 || expectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(capability)
            || string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 128)
            throw new ArgumentException("Portfolio, transaction, actor, access revision, and capability are required.");
    }

    internal static Task<bool> HasPropertyAuthorityAsync(
        int portfolioId, int propertyId, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision, string capability, IAtomicPersistenceSession persistence,
        DateTime now, CancellationToken ct) => HasPropertyAuthoritiesAsync(
            portfolioId, [propertyId], actorUserId, authSessionId, accessContextId,
            expectedAccessRevision, capability, persistence, now, ct);

    internal static async Task<bool> HasPropertyAuthoritiesAsync(
        int portfolioId, IReadOnlyCollection<int> propertyIds, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision, string capability,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct)
    {
        var requiredPropertyIds = propertyIds.Distinct().Order().ToArray();
        if (requiredPropertyIds.Length == 0) return false;

        var liveAssignments = LiveAssignments(
            portfolioId, actorUserId, authSessionId, accessContextId,
            expectedAccessRevision, capability, persistence, now);
        var authorizedPropertyCount = await persistence.Query<Property>()
            .Where(property =>
                property.PortfolioId == portfolioId &&
                requiredPropertyIds.Contains(property.Id) &&
                liveAssignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                     assignment.SelectedProperties.Any(scope =>
                         scope.PortfolioId == portfolioId && scope.PropertyId == property.Id))))
            .CountAsync(ct);
        return authorizedPropertyCount == requiredPropertyIds.Length;
    }

    internal static Task<bool> HasWorkspaceAuthorityAsync(
        int portfolioId, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision, string capability, IAtomicPersistenceSession persistence,
        DateTime now, CancellationToken ct) =>
        LiveAssignments(portfolioId, actorUserId, authSessionId, accessContextId,
                expectedAccessRevision, capability, persistence, now)
            .AnyAsync(assignment => assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition!.Key == capability
                && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Workspace), ct);

    private static IQueryable<MembershipRoleAssignment> LiveAssignments(
        int portfolioId, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision, string capability, IAtomicPersistenceSession persistence, DateTime now) =>
        persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == accessContextId
            && assignment.WorkspaceMembership.PortfolioId == portfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && assignment.WorkspaceMembership.AccessContext!.UserId == actorUserId
            && assignment.WorkspaceMembership.AccessContext.PortfolioId == portfolioId
            && assignment.WorkspaceMembership.AccessContext.AccessRevision == expectedAccessRevision
            && assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null
            && persistence.Query<AuthSession>().Any(session => session.Id == authSessionId
                && session.UserId == actorUserId && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition!.Key == capability));
}
