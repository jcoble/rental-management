using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
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
        var incomingIds = command.Added.Select(row => row.ProviderTransactionId)
            .Concat(command.Modified.Select(row => row.ProviderTransactionId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingRows = incomingIds.Length == 0
            ? new List<BankTransaction>()
            : await attempt.Persistence.Query<BankTransaction>()
                .Where(row => row.PortfolioId == command.PortfolioId
                    && row.BankConnectionId == connection.Id
                    && incomingIds.Contains(row.ProviderTransactionId))
                .ToListAsync(ct);
        var existing = existingRows.ToDictionary(row => row.ProviderTransactionId, StringComparer.Ordinal);
        var created = new List<BankTransaction>();
        var changed = new List<(BankTransaction Row, string Before, string Reason)>();
        var modifiedIds = new List<int>();
        var skipped = command.AddedInputCount - command.Added.Count
            + command.ModifiedInputCount - command.Modified.Count;

        foreach (var input in command.Added)
        {
            if (existing.ContainsKey(input.ProviderTransactionId))
            {
                skipped++;
                continue;
            }
            var row = NewTransaction(command.PortfolioId, connection.Id, input, command.AppliedAtUtc);
            attempt.Persistence.Add(row);
            existing.Add(input.ProviderTransactionId, row);
            created.Add(row);
        }
        foreach (var input in command.Modified)
        {
            if (!existing.TryGetValue(input.ProviderTransactionId, out var row))
            {
                skipped++;
                continue;
            }
            if (created.Contains(row))
            {
                Apply(row, input, command.AppliedAtUtc);
                continue;
            }
            var before = Snapshot(row);
            Apply(row, input, command.AppliedAtUtc);
            changed.Add((row, before, "Bank transaction updated from Plaid."));
            modifiedIds.Add(row.Id);
        }

        var removedIds = command.RemovedProviderTransactionIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var removed = removedIds.Length == 0
            ? new List<BankTransaction>()
            : await attempt.Persistence.Query<BankTransaction>()
                .Where(row => row.PortfolioId == command.PortfolioId
                    && row.BankConnectionId == connection.Id
                    && removedIds.Contains(row.ProviderTransactionId))
                .ToListAsync(ct);
        foreach (var row in removed)
        {
            var before = Snapshot(row);
            row.MatchedPaymentId = null;
            row.MatchedExpenseId = null;
            row.MatchStatus = "Removed";
            row.MatchConfidence = null;
            row.Notes = "Removed by Plaid sync.";
            row.UpdatedAt = command.AppliedAtUtc;
            changed.Add((row, before, "Bank transaction removed by Plaid sync."));
        }

        connection.SyncCursorCipherText = command.NextCursorCipherText;
        connection.LastSyncedAt = command.AppliedAtUtc;
        connection.UpdatedAt = command.AppliedAtUtc;
        await attempt.FlushBusinessAsync(ct);

        foreach (var row in created)
        {
            attempt.StageSemanticEvent(TransactionAudit(command.PortfolioId, row, AuditLogOperation.Created,
                null, "Bank transaction imported from Plaid."));
        }
        foreach (var mutation in changed)
        {
            attempt.StageSemanticEvent(TransactionAudit(command.PortfolioId, mutation.Row, AuditLogOperation.Updated,
                mutation.Before, mutation.Reason));
        }
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            AuditLogOperation.Updated,
            OldValues: beforeConnection,
            NewValues: ApplyPlaidConnectionHandler.Snapshot(connection),
            ChangeReason: $"Plaid sync {command.ProviderRequestIdentity} committed: {created.Count} imported, {modifiedIds.Count} modified, {removed.Count} removed."));
        StageNotification(attempt, command.PortfolioId, connection.Id, created.Count, changed.Count, command.AppliedAtUtc);

        return new ApplyPlaidSyncResult(
            ApplyPlaidSyncOutcome.Applied,
            connection.Id,
            created.Count,
            skipped,
            created.Select(row => row.Id).Concat(modifiedIds).Distinct().ToArray());
    }

    private static ApplyPlaidSyncResult Empty(ApplyPlaidSyncOutcome outcome, int connectionId) =>
        new(outcome, connectionId, 0, 0, []);

    internal static BankTransaction NewTransaction(
        int portfolioId,
        int connectionId,
        BankTransactionInput input,
        DateTime now) => new()
        {
            PortfolioId = portfolioId,
            BankConnectionId = connectionId,
            ProviderTransactionId = input.ProviderTransactionId,
            PostedAt = input.PostedAtUtc,
            AuthorizedAt = input.AuthorizedAtUtc,
            Description = input.Description,
            MerchantName = input.MerchantName,
            Amount = input.Amount,
            IsoCurrencyCode = input.IsoCurrencyCode,
            Category = input.Category,
            RawData = input.RawData,
            MatchStatus = "Unmatched",
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal static void Apply(BankTransaction row, BankTransactionInput input, DateTime now)
    {
        row.PostedAt = input.PostedAtUtc;
        row.AuthorizedAt = input.AuthorizedAtUtc;
        row.Description = input.Description;
        row.MerchantName = input.MerchantName;
        row.Amount = input.Amount;
        row.IsoCurrencyCode = input.IsoCurrencyCode;
        row.Category = input.Category;
        row.RawData = input.RawData;
        if (row.MatchStatus == "Matched")
        {
            row.MatchStatus = "Unmatched";
            row.MatchConfidence = null;
            row.MatchedPaymentId = null;
            row.MatchedExpenseId = null;
            row.Notes = "Plaid modified this transaction after it was matched; review the match again.";
        }
        row.UpdatedAt = now;
    }

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
        matchedPaymentId = row.MatchedPaymentId,
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
            ActionUrl = "/banking",
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

        var inputs = command.Transactions
            .GroupBy(row => row.ProviderTransactionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var ids = inputs.Select(row => row.ProviderTransactionId).Distinct(StringComparer.Ordinal).ToArray();
        var existing = createdConnection || ids.Length == 0
            ? new List<string>()
            : await attempt.Persistence.Query<BankTransaction>()
                .Where(row => row.PortfolioId == command.PortfolioId
                    && row.BankConnectionId == connection.Id
                    && ids.Contains(row.ProviderTransactionId))
                .Select(row => row.ProviderTransactionId)
                .ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var imported = inputs
            .Where(input => !existingSet.Contains(input.ProviderTransactionId))
            .Select(input => ApplyPlaidSyncHandler.NewTransaction(
                command.PortfolioId, connection.Id, input, command.ImportedAtUtc))
            .ToList();
        if (createdConnection)
        {
            foreach (var row in imported) row.BankConnection = connection;
        }
        attempt.Persistence.AddRange(imported);
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
        foreach (var row in imported)
        {
            attempt.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
                command.PortfolioId, row, AuditLogOperation.Created, null, "Bank transaction imported."));
        }
        if (imported.Count > 0)
        {
            attempt.Persistence.Add(new Notification
            {
                PortfolioId = command.PortfolioId,
                Type = "BankImportCompleted",
                Title = "Bank transactions imported",
                Message = $"{imported.Count} bank transactions are ready for review.",
                Severity = "Info",
                ActionUrl = "/banking",
                RelatedEntityType = nameof(BankConnection),
                RelatedEntityId = connection.Id,
                CreatedAt = command.ImportedAtUtc,
            });
        }
        return new ImportBankTransactionsResult(
            connection.Id,
            imported.Count,
            command.InputCount - imported.Count,
            imported.Select(row => row.Id).ToArray());
    }
}

public sealed class ReconcileBankTransactionHandler
    : IAtomicCommandHandler<ReconcileBankTransactionCommand, ReconcileBankTransactionResult>
{
    public async Task<ReconcileBankTransactionResult> HandleAsync(
        ReconcileBankTransactionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.BankTransaction, command.TransactionId, ct);
        var transaction = await attempt.Persistence.Query<BankTransaction>()
            .SingleOrDefaultAsync(row => row.Id == command.TransactionId
                && row.PortfolioId == command.PortfolioId, ct);
        if (transaction is null) return Result(ReconcileBankTransactionOutcome.TransactionNotFound, command.TransactionId);
        if (transaction.UpdatedAt != command.ExpectedUpdatedAtUtc)
        {
            return Result(ReconcileBankTransactionOutcome.StaleVersion, transaction.Id);
        }

        if (!await TargetExistsAsync(command, attempt, ct))
        {
            return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
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

    private static async Task<bool> TargetExistsAsync(
        ReconcileBankTransactionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct) => command.Action switch
        {
            BankReconciliationAction.MatchPayment when command.TargetEntityId is { } paymentId =>
                await attempt.Persistence.Query<Payment>().AnyAsync(row => row.Id == paymentId
                    && row.PortfolioId == command.PortfolioId, ct),
            BankReconciliationAction.MatchExpense when command.TargetEntityId is { } expenseId =>
                await attempt.Persistence.Query<Expense>().AnyAsync(row => row.Id == expenseId
                    && row.PortfolioId == command.PortfolioId, ct),
            BankReconciliationAction.MatchPayment or BankReconciliationAction.MatchExpense => false,
            _ => command.TargetEntityId is null,
        };

    private static void Apply(ReconcileBankTransactionCommand command, BankTransaction row)
    {
        row.MatchedPaymentId = command.Action == BankReconciliationAction.MatchPayment ? command.TargetEntityId : null;
        row.MatchedExpenseId = command.Action == BankReconciliationAction.MatchExpense ? command.TargetEntityId : null;
        row.MatchStatus = command.Action switch
        {
            BankReconciliationAction.MatchPayment or BankReconciliationAction.MatchExpense => "Matched",
            BankReconciliationAction.Clear => "Unmatched",
            BankReconciliationAction.Dismiss => "Dismissed",
            BankReconciliationAction.Ignore => "Removed",
            _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
        };
        row.MatchConfidence = command.Action is BankReconciliationAction.MatchPayment or BankReconciliationAction.MatchExpense
            ? 1m
            : null;
        if (command.Action == BankReconciliationAction.Ignore)
        {
            row.Notes = "Marked personal / ignored by the landlord.";
        }
        row.UpdatedAt = command.AppliedAtUtc;
    }

    private static string Reason(ReconcileBankTransactionCommand command) => command.Action switch
    {
        BankReconciliationAction.MatchPayment => $"Bank transaction matched to Payment #{command.TargetEntityId}.",
        BankReconciliationAction.MatchExpense => $"Bank transaction matched to Expense #{command.TargetEntityId}.",
        BankReconciliationAction.Clear => "Bank transaction match cleared.",
        BankReconciliationAction.Dismiss => "Bank suggested match dismissed.",
        BankReconciliationAction.Ignore => "Bank transaction ignored as personal / not business.",
        _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
    };

    private static ReconcileBankTransactionResult Result(ReconcileBankTransactionOutcome outcome, int id) =>
        new(outcome, id);
}
