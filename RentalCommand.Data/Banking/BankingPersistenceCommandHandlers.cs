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
    private readonly RentalCommandDbContext _db;

    public PreparePlaidTokenExchangeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<PreparePlaidTokenExchangeResult> HandleAsync(
        PreparePlaidTokenExchangeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync(
            "BankConnection",
            ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "plaid-exchange", command.ClientOperationId),
            ct);
        var authorizationNow = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await BankingAuthorizationSupport.HasWorkspaceAuthorityAsync(
                command.PortfolioId, command.ActorUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision,
                command.RequiredCapability, _db, authorizationNow, ct))
            throw new UnauthorizedAccessException();
        var existing = await _db.Set<PlaidTokenExchangeAttempt>()
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
        _db.Add(exchange);
        return new PreparePlaidTokenExchangeResult(PreparePlaidTokenExchangeOutcome.Prepared, exchange.Id);
    }

    public async Task AuthorizeReplayAsync(
        PreparePlaidTokenExchangeCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await BankingAuthorizationSupport.HasWorkspaceAuthorityAsync(
                command.PortfolioId, command.ActorUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision,
                command.RequiredCapability, _db, now, ct))
            throw new UnauthorizedAccessException();
    }
}

public sealed class AdmitPlaidTokenExchangeHandler
    : IAtomicCommandHandler<AdmitPlaidTokenExchangeCommand, AdmitPlaidTokenExchangeResult>
{
    private readonly RentalCommandDbContext _db;

    public AdmitPlaidTokenExchangeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AdmitPlaidTokenExchangeResult> HandleAsync(
        AdmitPlaidTokenExchangeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("BankConnection", command.ExchangeAttemptId, ct);
        var exchange = await _db.Set<PlaidTokenExchangeAttempt>()
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

    public async Task AuthorizeReplayAsync(
        AdmitPlaidTokenExchangeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateExchange(command.PortfolioId, command.ExchangeAttemptId);

        var exchangeExists = await _db.Set<PlaidTokenExchangeAttempt>()
            .AsNoTracking()
            .AnyAsync(exchange =>
                exchange.Id == command.ExchangeAttemptId &&
                exchange.PortfolioId == command.PortfolioId,
                ct);
        if (!exchangeExists)
        {
            throw new UnauthorizedAccessException("The Plaid token exchange attempt is unavailable.");
        }
    }

    private static AdmitPlaidTokenExchangeResult Result(
        AdmitPlaidTokenExchangeOutcome outcome,
        AdmitPlaidTokenExchangeCommand command) => new(outcome, command.ExchangeAttemptId);

    internal static void ValidateExchange(int portfolioId, Guid exchangeAttemptId)
    {
        if (portfolioId <= 0 || exchangeAttemptId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(exchangeAttemptId));
        }
    }
}

public sealed class RecordPlaidTokenExchangeReceiptHandler
    : IAtomicCommandHandler<RecordPlaidTokenExchangeReceiptCommand, RecordPlaidTokenExchangeReceiptResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordPlaidTokenExchangeReceiptHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordPlaidTokenExchangeReceiptResult> HandleAsync(
        RecordPlaidTokenExchangeReceiptCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("BankConnection", command.ExchangeAttemptId, ct);
        var exchange = await _db.Set<PlaidTokenExchangeAttempt>()
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
                    $"Plaid exchange context {exchange.Id} is already bound to a different provider receipt.");
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

    public async Task AuthorizeReplayAsync(
        RecordPlaidTokenExchangeReceiptCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AdmitPlaidTokenExchangeHandler.ValidateExchange(command.PortfolioId, command.ExchangeAttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderRequestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExternalItemIdHash);

        var exchangeExists = await _db.Set<PlaidTokenExchangeAttempt>()
            .AsNoTracking()
            .AnyAsync(exchange =>
                exchange.Id == command.ExchangeAttemptId &&
                exchange.PortfolioId == command.PortfolioId &&
                (exchange.RemoteReceiptRecordedAtUtc != null ||
                 exchange.RemoteAdmittedAtUtc != null),
                ct);
        if (!exchangeExists)
        {
            throw new UnauthorizedAccessException("The Plaid receipt exchange attempt is unavailable.");
        }
    }

    private static RecordPlaidTokenExchangeReceiptResult Result(
        RecordPlaidTokenExchangeReceiptOutcome outcome,
        RecordPlaidTokenExchangeReceiptCommand command) => new(outcome, command.ExchangeAttemptId);
}

public sealed class ApplyPlaidConnectionHandler
    : IAtomicCommandHandler<ApplyPlaidConnectionCommand, ApplyPlaidConnectionResult>
{
    private readonly RentalCommandDbContext _db;

    public ApplyPlaidConnectionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ApplyPlaidConnectionResult> HandleAsync(
        ApplyPlaidConnectionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("BankConnection", command.ExchangeAttemptId, ct);
        var exchange = await _db.Set<PlaidTokenExchangeAttempt>()
            .SingleOrDefaultAsync(row => row.Id == command.ExchangeAttemptId
                && row.PortfolioId == command.PortfolioId, ct)
            ?? throw new AtomicReceiptInvariantException(
                $"Plaid exchange context {command.ExchangeAttemptId} was not found.");
        if (exchange.RemoteReceiptRecordedAtUtc is null
            || exchange.ExternalItemIdCipherText is null
            || exchange.ExternalItemIdHash is null
            || exchange.ExternalAccessTokenCipherText is null
            || exchange.ProviderRequestIdentity is null)
        {
            throw new AtomicReceiptInvariantException(
                $"Plaid exchange context {exchange.Id} has no durable remote receipt.");
        }
        if (exchange.CompletedAtUtc is not null && exchange.BankConnectionId is int completedConnectionId)
            return new ApplyPlaidConnectionResult(completedConnectionId, false);
        await context.AcquireLockAsync(
            "BankConnection",
            StableGuid(command.PortfolioId, "Plaid", exchange.ExternalItemIdHash, exchange.ExternalAccountIdHash),
            ct);

        var connection = await _db.Set<BankConnection>()
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
        if (created) _db.Add(connection);

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

        await context.FlushBusinessAsync(ct);
        exchange.BankConnectionId = connection.Id;
        exchange.CompletedAtUtc = command.AppliedAtUtc;
        exchange.Status = "Completed";
        context.StageSemanticEvent(new AtomicSemanticAudit(
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

    public async Task AuthorizeReplayAsync(
        ApplyPlaidConnectionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AdmitPlaidTokenExchangeHandler.ValidateExchange(command.PortfolioId, command.ExchangeAttemptId);

        var completedExchangeExists = await _db.Set<PlaidTokenExchangeAttempt>()
            .AsNoTracking()
            .AnyAsync(exchange =>
                exchange.Id == command.ExchangeAttemptId &&
                exchange.PortfolioId == command.PortfolioId &&
                exchange.CompletedAtUtc != null &&
                exchange.BankConnectionId != null,
                ct);
        if (!completedExchangeExists)
        {
            throw new UnauthorizedAccessException("The applied Plaid connection exchange is unavailable.");
        }
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
    private readonly RentalCommandDbContext _db;

    public ApplyPlaidSyncHandler(RentalCommandDbContext db) => _db = db;

    private const int MaxBatch = 500;

    public async Task<ApplyPlaidSyncResult> HandleAsync(
        ApplyPlaidSyncCommand command,
        IAtomicCommandContext context,
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

        await context.AcquireLockAsync("BankConnection", command.ConnectionId, ct);
        var connection = await _db.Set<BankConnection>()
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
        var merge = await AtomicBankingPersistence.ApplyPlaidSyncAsync(_db,
            context, command.PortfolioId,
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
        await context.FlushBusinessAsync(ct);

        foreach (var mutation in merge.Mutations)
        {
            context.StageSemanticEvent(TransactionAudit(command.PortfolioId, mutation));
        }
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            AuditLogOperation.Updated,
            OldValues: beforeConnection,
            NewValues: ApplyPlaidConnectionHandler.Snapshot(connection),
            ChangeReason: $"Plaid sync {command.ProviderRequestIdentity} committed: {merge.ImportedCount} imported, {merge.ModifiedCount} modified, {merge.RemovedCount} removed."));
        StageNotification(_db, context, command.PortfolioId, connection.Id, merge.ImportedCount, merge.ChangedEventCount, command.AppliedAtUtc);

        return new ApplyPlaidSyncResult(
            ApplyPlaidSyncOutcome.Applied,
            connection.Id,
            merge.ImportedCount,
            merge.SkippedCount,
            merge.AffectedTransactionIds);
    }

    public async Task AuthorizeReplayAsync(
        ApplyPlaidSyncCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);

        var connectionExists = await _db.Set<BankConnection>()
            .AsNoTracking()
            .AnyAsync(connection =>
                connection.Id == command.ConnectionId &&
                connection.PortfolioId == command.PortfolioId &&
                connection.Provider == "Plaid",
                ct);
        if (!connectionExists)
        {
            throw new UnauthorizedAccessException("The Plaid sync connection is unavailable.");
        }
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
        matchedLoanPaymentId = row.MatchedLoanPaymentId,
        matchedOwnerDistributionId = row.MatchedOwnerDistributionId,
        matchedBankTransactionId = row.MatchedBankTransactionId,
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
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int connectionId,
        int imported,
        int changed,
        DateTime now)
    {
        if (imported == 0 && changed == 0) return;
        db.Add(new Notification
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

    private static void Validate(ApplyPlaidSyncCommand command)
    {
        if (command.PortfolioId <= 0 ||
            command.ConnectionId <= 0 ||
            string.IsNullOrWhiteSpace(command.ProviderRequestIdentity) ||
            command.AddedInputCount < command.Added.Count ||
            command.ModifiedInputCount < command.Modified.Count ||
            command.AddedInputCount > MaxBatch ||
            command.ModifiedInputCount > MaxBatch ||
            command.Added.Count > MaxBatch ||
            command.Modified.Count > MaxBatch ||
            command.RemovedProviderTransactionIds.Count > MaxBatch)
        {
            throw new InvalidOperationException($"A Plaid sync result cannot exceed {MaxBatch} rows per change set.");
        }
    }
}

public sealed class ImportBankTransactionsHandler
    : IAtomicCommandHandler<ImportBankTransactionsCommand, ImportBankTransactionsResult>
{
    private readonly RentalCommandDbContext _db;

    public ImportBankTransactionsHandler(RentalCommandDbContext db) => _db = db;

    private const int MaxBatch = 500;

    public async Task<ImportBankTransactionsResult> HandleAsync(
        ImportBankTransactionsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.InputCount < 0
            || (command.InputCount == 0 && command.Statement is null)
            || command.InputCount > MaxBatch
            || command.InputCount < command.Transactions.Count
            || command.Transactions.Count > MaxBatch)
        {
            throw new InvalidOperationException(
                $"A bank import must contain a statement or between 1 and {MaxBatch} rows.");
        }
        ValidateStatement(command.Statement);

        await context.AcquireLockAsync(
            "BankConnection",
            ApplyPlaidConnectionHandler.StableGuid(
                command.PortfolioId, command.Provider, command.InstitutionName, command.AccountName, command.AccountMask),
            ct);
        var existingStatement = command.Statement is null
            ? null
            : await _db.Set<BankStatement>()
                .SingleOrDefaultAsync(row =>
                    row.PortfolioId == command.PortfolioId
                    && row.BankConnection!.Provider == command.Provider
                    && row.BankConnection.InstitutionName == command.InstitutionName
                    && row.BankConnection.AccountName == command.AccountName
                    && row.BankConnection.AccountMask == command.AccountMask
                    && row.PeriodStart == command.Statement.PeriodStart
                    && row.PeriodEnd == command.Statement.PeriodEnd, ct);
        if (existingStatement is not null && !StatementMatches(existingStatement, command.Statement!))
        {
            throw new InvalidOperationException(
                "A different bank statement already controls this account and period.");
        }
        var connection = await _db.Set<BankConnection>()
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
        if (createdConnection) _db.Add(connection);

        if (createdConnection)
        {
            await context.FlushBusinessAsync(ct);
        }
        var createdStatement = false;
        if (command.Statement is not null && existingStatement is null)
        {
            existingStatement = new BankStatement
            {
                PortfolioId = command.PortfolioId,
                BankConnectionId = connection.Id,
                PeriodStart = command.Statement.PeriodStart,
                PeriodEnd = command.Statement.PeriodEnd,
                OpeningBalance = command.Statement.OpeningBalance,
                ClosingBalance = command.Statement.ClosingBalance,
                StatementMovement = command.Statement.StatementMovement,
                IsoCurrencyCode = command.Statement.IsoCurrencyCode,
                ImportedAtUtc = command.ImportedAtUtc,
                CreatedAt = command.ImportedAtUtc,
                UpdatedAt = command.ImportedAtUtc,
            };
            _db.Add(existingStatement);
            createdStatement = true;
        }
        var merge = await AtomicBankingPersistence.ImportAsync(_db,
            context, command.PortfolioId,
            connection.Id,
            command.Transactions,
            command.InputCount,
            command.ImportedAtUtc,
            ct);
        connection.LastSyncedAt = command.ImportedAtUtc;
        connection.UpdatedAt = command.ImportedAtUtc;
        await context.FlushBusinessAsync(ct);

        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(BankConnection),
            connection.Id,
            createdConnection ? AuditLogOperation.Created : AuditLogOperation.Updated,
            OldValues: connectionBefore,
            NewValues: ApplyPlaidConnectionHandler.Snapshot(connection),
            ChangeReason: $"Bank import {command.RequestIdentity} committed."));
        if (createdStatement)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(BankStatement),
                existingStatement!.Id,
                AuditLogOperation.Created,
                OldValues: null,
                NewValues: StatementSnapshot(existingStatement),
                ChangeReason: $"Bank statement import {command.RequestIdentity} committed."));
        }
        foreach (var mutation in merge.Mutations)
        {
            context.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(command.PortfolioId, mutation));
        }
        if (merge.ImportedCount > 0 || createdStatement)
        {
            _db.Add(new Notification
            {
                PortfolioId = command.PortfolioId,
                Type = "BankImportCompleted",
                Title = createdStatement ? "Bank statement imported" : "Bank transactions imported",
                Message = createdStatement
                    ? $"{command.InstitutionName} statement controls were imported with {merge.ImportedCount} new transactions."
                    : $"{merge.ImportedCount} bank transactions are ready for review.",
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
            merge.AffectedTransactionIds,
            existingStatement?.Id,
            existingStatement?.PeriodStart,
            existingStatement?.PeriodEnd,
            existingStatement?.OpeningBalance,
            existingStatement?.ClosingBalance,
            existingStatement?.StatementMovement,
            existingStatement?.IsoCurrencyCode);
    }

    public async Task AuthorizeReplayAsync(
        ImportBankTransactionsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateImport(command);
        ValidateStatement(command.Statement);

        var connectionExists = await _db.Set<BankConnection>()
            .AsNoTracking()
            .AnyAsync(connection =>
                connection.PortfolioId == command.PortfolioId &&
                connection.Provider == command.Provider &&
                connection.InstitutionName == command.InstitutionName &&
                connection.AccountName == command.AccountName &&
                connection.AccountMask == command.AccountMask,
                ct);
        if (!connectionExists)
        {
            throw new UnauthorizedAccessException("The bank import connection is unavailable.");
        }
    }

    private static void ValidateImport(ImportBankTransactionsCommand command)
    {
        if (command.PortfolioId <= 0 ||
            string.IsNullOrWhiteSpace(command.Provider) ||
            string.IsNullOrWhiteSpace(command.InstitutionName) ||
            string.IsNullOrWhiteSpace(command.AccountName) ||
            string.IsNullOrWhiteSpace(command.RequestIdentity) ||
            command.InputCount < 0 ||
            (command.InputCount == 0 && command.Statement is null) ||
            command.InputCount > MaxBatch ||
            command.InputCount < command.Transactions.Count ||
            command.Transactions.Count > MaxBatch)
        {
            throw new InvalidOperationException(
                $"A bank import must contain a statement or between 1 and {MaxBatch} rows.");
        }
    }

    private static void ValidateStatement(BankStatementInput? statement)
    {
        if (statement is null) return;
        if (statement.PeriodStart == default
            || statement.PeriodEnd == default
            || statement.PeriodStart > statement.PeriodEnd)
        {
            throw new InvalidOperationException("A bank statement requires a valid period.");
        }
        if (statement.StatementMovement != statement.ClosingBalance - statement.OpeningBalance)
            throw new InvalidOperationException("Bank statement movement must equal closing balance minus opening balance.");
        if (string.IsNullOrWhiteSpace(statement.IsoCurrencyCode) || statement.IsoCurrencyCode.Length > 8)
            throw new InvalidOperationException("A bank statement requires a valid currency code.");
    }

    private static bool StatementMatches(BankStatement row, BankStatementInput input) =>
        row.OpeningBalance == input.OpeningBalance
        && row.ClosingBalance == input.ClosingBalance
        && row.StatementMovement == input.StatementMovement
        && row.IsoCurrencyCode == input.IsoCurrencyCode;

    private static string StatementSnapshot(BankStatement row) => JsonSerializer.Serialize(new
    {
        row.BankConnectionId,
        row.PeriodStart,
        row.PeriodEnd,
        row.OpeningBalance,
        row.ClosingBalance,
        row.StatementMovement,
        row.IsoCurrencyCode,
        row.ImportedAtUtc,
    });
}

public sealed class ReconcileBankTransactionHandler
    : IAtomicCommandHandler<ReconcileBankTransactionCommand, ReconcileBankTransactionResult>
{
    private readonly RentalCommandDbContext _db;

    public ReconcileBankTransactionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ReconcileBankTransactionResult> HandleAsync(
        ReconcileBankTransactionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        var discoveredClearTargets = command.Action == BankReconciliationAction.Clear
            ? await _db.Set<BankTransaction>()
                .Where(row => row.Id == command.TransactionId && row.PortfolioId == command.PortfolioId)
                .Select(row => new
                {
                    row.MatchedBankTransactionId,
                    row.MatchedExpenseId,
                })
                .SingleOrDefaultAsync(ct)
            : null;
        await AcquireTargetClaimLockAsync(command, discoveredClearTargets?.MatchedExpenseId, context, ct);
        var discoveredClearTransferId = discoveredClearTargets?.MatchedBankTransactionId;
        var effectiveTransferId = command.TransferBankTransactionId ?? discoveredClearTransferId;
        var transactionLockIds = effectiveTransferId is { } transferId
            ? new[] { command.TransactionId, transferId }.Distinct().Order().ToArray()
            : [command.TransactionId];
        foreach (var transactionLockId in transactionLockIds)
            await context.AcquireLockAsync("BankTransaction", transactionLockId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var transaction = await _db.Set<BankTransaction>()
            .SingleOrDefaultAsync(row => row.Id == command.TransactionId
                && row.PortfolioId == command.PortfolioId, ct);
        if (transaction is null) return Result(ReconcileBankTransactionOutcome.TransactionNotFound, command.TransactionId);
        if (!await AuthorizeAsync(command, transaction, _db, now, ct))
        {
            throw new UnauthorizedAccessException();
        }
        if (transaction.UpdatedAt != command.ExpectedUpdatedAtUtc)
        {
            return Result(ReconcileBankTransactionOutcome.StaleVersion, transaction.Id);
        }
        if (command.Action == BankReconciliationAction.Clear
            && (transaction.MatchedBankTransactionId != discoveredClearTransferId
                || transaction.MatchedExpenseId != discoveredClearTargets?.MatchedExpenseId))
        {
            return Result(ReconcileBankTransactionOutcome.StaleVersion, transaction.Id);
        }

        var expenseTarget = command.Action switch
        {
            BankReconciliationAction.MatchExpense =>
                await LoadEligibleExpenseTargetAsync(command, transaction, _db, ct),
            BankReconciliationAction.Clear when transaction.MatchedExpenseId is not null =>
                await LoadRestorableExpenseTargetAsync(transaction, _db, ct),
            _ => null,
        };
        if (!await TargetExistsAsync(command, transaction, expenseTarget, context, ct))
        {
            return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
        }
        if (IsAlreadyApplied(command, transaction, expenseTarget))
        {
            return Result(
                ReconcileBankTransactionOutcome.AlreadyApplied,
                transaction.Id,
                await SnapshotAsync(command.PortfolioId, transaction.Id, _db, ct));
        }
        if (command.Action == BankReconciliationAction.MatchExpense
            && transaction.MatchedExpenseId == command.ExpenseId)
        {
            return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
        }
        if (await TargetIsAlreadyMatchedAsync(command, transaction, _db, ct))
        {
            return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
        }
        var before = ApplyPlaidSyncHandler.Snapshot(transaction);
        BankTransaction? transferTarget = null;
        string? transferBefore = null;
        if (effectiveTransferId is { } matchedBankTransactionId)
        {
            transferTarget = await _db.Set<BankTransaction>()
                .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                    && row.Id == matchedBankTransactionId, ct);
            if (transferTarget is null)
                return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
            if (command.Action == BankReconciliationAction.Clear
                && (transferTarget.MatchStatus != "Matched"
                    || transferTarget.MatchedBankTransactionId != transaction.Id))
            {
                return Result(ReconcileBankTransactionOutcome.TargetNotFound, transaction.Id);
            }
            transferBefore = ApplyPlaidSyncHandler.Snapshot(transferTarget);
        }
        Apply(command, transaction, transferTarget, expenseTarget);
        if (expenseTarget is not null)
        {
            context.BindSemanticAudit(expenseTarget, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Expense),
                expenseTarget.Id,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                ChangeReason: command.Action == BankReconciliationAction.Clear
                    ? $"Expense {expenseTarget.Id} lifecycle restored after clearing bank transaction {transaction.Id}."
                    : $"Expense {expenseTarget.Id} marked paid from bank transaction {transaction.Id}."));
        }
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
            command.PortfolioId,
            transaction,
            AuditLogOperation.Updated,
            before,
            Reason(command)));
        if (transferTarget is not null)
        {
            context.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
                command.PortfolioId,
                transferTarget,
                AuditLogOperation.Updated,
                transferBefore,
                Reason(command)));
        }
        return Result(
            ReconcileBankTransactionOutcome.Applied,
            transaction.Id,
            await SnapshotAsync(command.PortfolioId, transaction.Id, _db, ct));
    }

    public async Task AuthorizeReplayAsync(
        ReconcileBankTransactionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var transaction = await _db.Set<BankTransaction>()
            .Where(row => row.Id == command.TransactionId && row.PortfolioId == command.PortfolioId)
            .Select(row => new BankReplayAuthorizationRow
            {
                Id = row.Id,
                PropertyId = row.PropertyId,
                MatchedBankTransactionId = row.MatchedBankTransactionId,
            })
            .SingleOrDefaultAsync(ct);
        if (transaction is null || !await AuthorizeReplayAsync(command, transaction, _db, now, ct))
            throw new UnauthorizedAccessException();
    }

    private async Task<bool> TargetExistsAsync(
        ReconcileBankTransactionCommand command,
        BankTransaction transaction,
        Expense? expenseTarget,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var expectedTransferUpdatedAtUtc =
            command.ExpectedTransferUpdatedAtUtc ?? command.ResolvedSuggestionTransferUpdatedAtUtc;
        return command.Action switch
        {
            BankReconciliationAction.MatchReceipt
                when command.TenantAccountId is { } accountId
                    && command.TenantLedgerEntryId is { } ledgerEntryId
                    && command.ExpenseId is null =>
                await _db.Set<TenantLedgerEntry>().AnyAsync(row =>
                    row.Id == ledgerEntryId
                    && row.TenantAccountId == accountId
                    && row.PortfolioId == command.PortfolioId
                    && ((row.EntryType == TenantLedgerEntryType.PaymentReceipt
                            && row.Direction == TenantLedgerDirection.Credit
                            && transaction.Amount > 0m)
                        || (row.EntryType == TenantLedgerEntryType.TransferIn
                            || row.EntryType == TenantLedgerEntryType.TransferOut)
                           && ((row.Direction == TenantLedgerDirection.Credit && transaction.Amount > 0m)
                               || (row.Direction == TenantLedgerDirection.Debit && transaction.Amount < 0m)))
                    && row.Amount >= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) - 0.01m
                    && row.Amount <= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) + 0.01m
                    && row.EffectiveOn >= DateOnly.FromDateTime(transaction.PostedAt.AddDays(-14))
                    && row.EffectiveOn <= DateOnly.FromDateTime(transaction.PostedAt.AddDays(14))
                    && transaction.PropertyId != null
                    && row.TenantAccount!.LeaseManagement!.PropertyId == transaction.PropertyId, ct),
            BankReconciliationAction.MatchExpense
                when command.ExpenseId is { } expenseId
                    && command.TenantAccountId is null
                    && command.TenantLedgerEntryId is null =>
                expenseTarget is not null && expenseTarget.Id == expenseId,
            BankReconciliationAction.MatchLoanPayment
                when command.LoanPaymentId is { } loanPaymentId =>
                await _db.Set<LoanPayment>().AnyAsync(payment =>
                    payment.Id == loanPaymentId
                    && payment.PortfolioId == command.PortfolioId
                    && payment.Status == LoanPaymentStatus.Paid
                    && transaction.Amount < 0m
                    && payment.TotalAmount >= -transaction.Amount - 0.01m
                    && payment.TotalAmount <= -transaction.Amount + 0.01m
                    && (payment.PaidDate ?? payment.DueDate) >= transaction.PostedAt.AddDays(-14)
                    && (payment.PaidDate ?? payment.DueDate) <= transaction.PostedAt.AddDays(14)
                    && transaction.PropertyId != null
                    && payment.Loan!.PropertyId == transaction.PropertyId, ct),
            BankReconciliationAction.MatchOwnerDistribution
                when command.OwnerDistributionId is { } ownerDistributionId =>
                await _db.Set<OwnerDistribution>().AnyAsync(distribution =>
                    distribution.Id == ownerDistributionId
                    && distribution.PortfolioId == command.PortfolioId
                    && distribution.Status == OwnerDistributionStatus.Approved
                    && transaction.Amount < 0m
                    && distribution.Amount >= -transaction.Amount - 0.01m
                    && distribution.Amount <= -transaction.Amount + 0.01m
                    && distribution.Date >= transaction.PostedAt.AddDays(-14)
                    && distribution.Date <= transaction.PostedAt.AddDays(14)
                    && (distribution.PropertyId == transaction.PropertyId
                        || (distribution.PropertyId == null && transaction.PropertyId == null)), ct),
            BankReconciliationAction.MatchTransfer
                when command.TransferBankTransactionId is { } transferId
                    && expectedTransferUpdatedAtUtc is { } =>
                await _db.Set<BankTransaction>().AnyAsync(other =>
                    other.Id == transferId
                    && other.Id != transaction.Id
                    && other.PortfolioId == command.PortfolioId
                    && other.BankConnectionId != transaction.BankConnectionId
                    && other.MatchStatus == "Unmatched"
                    && other.UpdatedAt == expectedTransferUpdatedAtUtc
                    && other.Amount >= -transaction.Amount - 0.01m
                    && other.Amount <= -transaction.Amount + 0.01m
                    && other.PostedAt >= transaction.PostedAt.AddDays(-3)
                    && other.PostedAt <= transaction.PostedAt.AddDays(3), ct),
            BankReconciliationAction.Clear when transaction.MatchedExpenseId is not null =>
                expenseTarget is not null,
            BankReconciliationAction.MatchReceipt
                or BankReconciliationAction.MatchExpense
                or BankReconciliationAction.MatchLoanPayment
                or BankReconciliationAction.MatchOwnerDistribution
                or BankReconciliationAction.MatchTransfer => false,
            _ => command.TenantAccountId is null
                && command.TenantLedgerEntryId is null
                && command.ExpenseId is null
                && command.LoanPaymentId is null
                && command.OwnerDistributionId is null,
        };
    }

    private static void Apply(
        ReconcileBankTransactionCommand command,
        BankTransaction row,
        BankTransaction? transferTarget,
        Expense? expenseTarget)
    {
        if (command.Action == BankReconciliationAction.MatchExpense && expenseTarget is not null)
        {
            row.ExpenseMatchAppliedAt = command.AppliedAtUtc;
            row.ExpenseMatchPreviousStatus = expenseTarget.Status;
            row.ExpenseMatchPreviousPaidAt = expenseTarget.PaidAt;
            row.ExpenseMatchPreviousUpdatedAt = expenseTarget.UpdatedAt;
        }
        else if (command.Action == BankReconciliationAction.Clear
            && row.MatchedExpenseId is not null
            && expenseTarget is not null)
        {
            expenseTarget.Status = row.ExpenseMatchPreviousStatus!.Value;
            expenseTarget.PaidAt = row.ExpenseMatchPreviousPaidAt;
            expenseTarget.UpdatedAt = row.ExpenseMatchPreviousUpdatedAt!.Value;
            row.ExpenseMatchAppliedAt = null;
            row.ExpenseMatchPreviousStatus = null;
            row.ExpenseMatchPreviousPaidAt = null;
            row.ExpenseMatchPreviousUpdatedAt = null;
        }
        row.MatchedTenantAccountId = command.Action == BankReconciliationAction.MatchReceipt
            ? command.TenantAccountId
            : null;
        row.MatchedTenantLedgerEntryId = command.Action == BankReconciliationAction.MatchReceipt
            ? command.TenantLedgerEntryId
            : null;
        row.MatchedExpenseId = command.Action == BankReconciliationAction.MatchExpense ? command.ExpenseId : null;
        row.MatchedLoanPaymentId = command.Action == BankReconciliationAction.MatchLoanPayment
            ? command.LoanPaymentId
            : null;
        row.MatchedOwnerDistributionId = command.Action == BankReconciliationAction.MatchOwnerDistribution
            ? command.OwnerDistributionId
            : null;
        row.MatchedBankTransactionId = command.Action == BankReconciliationAction.MatchTransfer
            ? command.TransferBankTransactionId
            : null;
        row.MatchStatus = command.Action switch
        {
            BankReconciliationAction.MatchReceipt
                or BankReconciliationAction.MatchExpense
                or BankReconciliationAction.MatchLoanPayment
                or BankReconciliationAction.MatchOwnerDistribution
                or BankReconciliationAction.MatchTransfer => "Matched",
            BankReconciliationAction.Clear => "Unmatched",
            BankReconciliationAction.Dismiss => "Dismissed",
            BankReconciliationAction.Ignore => "Removed",
            _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
        };
        row.MatchConfidence = command.Action is BankReconciliationAction.MatchReceipt
            or BankReconciliationAction.MatchExpense
            or BankReconciliationAction.MatchLoanPayment
            or BankReconciliationAction.MatchOwnerDistribution
            or BankReconciliationAction.MatchTransfer
            ? 1m
            : null;
        if (command.Action == BankReconciliationAction.Ignore)
        {
            row.Notes = "Marked personal / ignored by the landlord.";
        }
        row.UpdatedAt = command.AppliedAtUtc;
        if (command.Action == BankReconciliationAction.MatchExpense && expenseTarget is not null)
        {
            expenseTarget.Status = ExpenseStatus.Paid;
            expenseTarget.PaidAt = row.PostedAt;
            expenseTarget.UpdatedAt = command.AppliedAtUtc;
        }
        if (command.Action == BankReconciliationAction.MatchTransfer && transferTarget is not null)
        {
            transferTarget.MatchedTenantAccountId = null;
            transferTarget.MatchedTenantLedgerEntryId = null;
            transferTarget.MatchedExpenseId = null;
            transferTarget.MatchedLoanPaymentId = null;
            transferTarget.MatchedOwnerDistributionId = null;
            transferTarget.MatchedBankTransactionId = row.Id;
            transferTarget.MatchStatus = "Matched";
            transferTarget.MatchConfidence = 1m;
            transferTarget.UpdatedAt = command.AppliedAtUtc;
        }
        else if (command.Action == BankReconciliationAction.Clear && transferTarget is not null)
        {
            transferTarget.MatchedTenantAccountId = null;
            transferTarget.MatchedTenantLedgerEntryId = null;
            transferTarget.MatchedExpenseId = null;
            transferTarget.MatchedLoanPaymentId = null;
            transferTarget.MatchedOwnerDistributionId = null;
            transferTarget.MatchedBankTransactionId = null;
            transferTarget.MatchStatus = "Unmatched";
            transferTarget.MatchConfidence = null;
            transferTarget.UpdatedAt = command.AppliedAtUtc;
        }
    }

    private static bool IsAlreadyApplied(
        ReconcileBankTransactionCommand command,
        BankTransaction row,
        Expense? expenseTarget) =>
        command.Action switch
        {
            BankReconciliationAction.MatchReceipt =>
                row.MatchStatus == "Matched"
                && row.MatchedTenantAccountId == command.TenantAccountId
                && row.MatchedTenantLedgerEntryId == command.TenantLedgerEntryId
                && OtherTargetsAreNull(row, exceptTenantLedger: true),
            BankReconciliationAction.MatchExpense =>
                row.MatchStatus == "Matched"
                && row.MatchedExpenseId == command.ExpenseId
                && row.ExpenseMatchAppliedAt is not null
                && row.ExpenseMatchPreviousStatus is not null
                && row.ExpenseMatchPreviousUpdatedAt is not null
                && expenseTarget is not null
                && expenseTarget.Status == ExpenseStatus.Paid
                && expenseTarget.PaidAt == row.PostedAt
                && expenseTarget.UpdatedAt == row.ExpenseMatchAppliedAt
                && OtherTargetsAreNull(row, exceptExpense: true),
            BankReconciliationAction.MatchLoanPayment =>
                row.MatchStatus == "Matched"
                && row.MatchedLoanPaymentId == command.LoanPaymentId
                && OtherTargetsAreNull(row, exceptLoanPayment: true),
            BankReconciliationAction.MatchOwnerDistribution =>
                row.MatchStatus == "Matched"
                && row.MatchedOwnerDistributionId == command.OwnerDistributionId
                && OtherTargetsAreNull(row, exceptOwnerDistribution: true),
            BankReconciliationAction.MatchTransfer =>
                row.MatchStatus == "Matched"
                && row.MatchedBankTransactionId == command.TransferBankTransactionId
                && OtherTargetsAreNull(row, exceptBankTransaction: true),
            BankReconciliationAction.Clear =>
                row.MatchStatus == "Unmatched"
                && OtherTargetsAreNull(row)
                && ExpenseMatchProvenanceIsNull(row),
            BankReconciliationAction.Dismiss =>
                row.MatchStatus == "Dismissed"
                && OtherTargetsAreNull(row),
            BankReconciliationAction.Ignore =>
                row.MatchStatus == "Removed"
                && OtherTargetsAreNull(row),
            _ => false,
        };

    private static Task<Expense?> LoadEligibleExpenseTargetAsync(
        ReconcileBankTransactionCommand command,
        BankTransaction transaction,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        command.ExpenseId is not { } expenseId
            ? Task.FromResult<Expense?>(null)
            : db.Set<Expense>().SingleOrDefaultAsync(row =>
                row.Id == expenseId
                && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null
                && (row.Status == ExpenseStatus.Pending
                    || row.Status == ExpenseStatus.Approved
                    || row.Status == ExpenseStatus.Paid)
                && transaction.Amount < 0m
                && row.Amount >= -transaction.Amount - 0.01m
                && row.Amount <= -transaction.Amount + 0.01m
                && (row.PaidAt ?? row.IncurredAt) >= transaction.PostedAt.AddDays(-14)
                && (row.PaidAt ?? row.IncurredAt) <= transaction.PostedAt.AddDays(14)
                && (row.PropertyId == transaction.PropertyId
                    || (row.PropertyId == null && row.Unit!.PropertyId == transaction.PropertyId)
                    || (row.PropertyId == null && row.UnitId == null
                        && row.WorkOrder!.PropertyId == transaction.PropertyId)
                    || (transaction.PropertyId == null && row.PropertyId == null
                        && row.UnitId == null && row.WorkOrderId == null)), ct);

    private static Task<Expense?> LoadRestorableExpenseTargetAsync(
        BankTransaction transaction,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        transaction.MatchedExpenseId is not { } expenseId
            || transaction.ExpenseMatchAppliedAt is null
            || transaction.ExpenseMatchPreviousStatus is null
            || transaction.ExpenseMatchPreviousUpdatedAt is null
            ? Task.FromResult<Expense?>(null)
            : db.Set<Expense>().SingleOrDefaultAsync(row =>
                row.Id == expenseId
                && row.PortfolioId == transaction.PortfolioId
                && row.DeletedAt == null
                && row.Status == ExpenseStatus.Paid
                && row.PaidAt == transaction.PostedAt
                && row.UpdatedAt == transaction.ExpenseMatchAppliedAt, ct);

    private static string Reason(ReconcileBankTransactionCommand command) => command.Action switch
    {
        BankReconciliationAction.MatchReceipt =>
            $"Bank transaction matched to tenant receipt #{command.TenantLedgerEntryId} on account #{command.TenantAccountId}.",
        BankReconciliationAction.MatchExpense => $"Bank transaction matched to Expense #{command.ExpenseId}.",
        BankReconciliationAction.MatchLoanPayment =>
            $"Bank transaction matched to LoanPayment #{command.LoanPaymentId}.",
        BankReconciliationAction.MatchOwnerDistribution =>
            $"Bank transaction matched to OwnerDistribution #{command.OwnerDistributionId}.",
        BankReconciliationAction.MatchTransfer =>
            $"Bank transaction matched to opposite BankTransaction #{command.TransferBankTransactionId}.",
        BankReconciliationAction.Clear => "Bank transaction match cleared.",
        BankReconciliationAction.Dismiss => "Bank suggested match dismissed.",
        BankReconciliationAction.Ignore => "Bank transaction ignored as personal / not business.",
        _ => throw new ArgumentOutOfRangeException(nameof(command.Action)),
    };

    private static async Task<bool> TargetIsAlreadyMatchedAsync(
        ReconcileBankTransactionCommand command,
        BankTransaction transaction,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        command.Action switch
        {
            BankReconciliationAction.MatchReceipt
                when command.TenantLedgerEntryId is { } ledgerEntryId =>
                await db.Set<BankTransaction>().AnyAsync(row =>
                    row.Id != transaction.Id
                    && row.PortfolioId == command.PortfolioId
                    && row.MatchedTenantLedgerEntryId == ledgerEntryId, ct),
            BankReconciliationAction.MatchExpense
                when command.ExpenseId is { } expenseId =>
                await db.Set<BankTransaction>().AnyAsync(row =>
                    row.Id != transaction.Id
                    && row.PortfolioId == command.PortfolioId
                    && row.MatchedExpenseId == expenseId, ct),
            BankReconciliationAction.MatchLoanPayment
                when command.LoanPaymentId is { } loanPaymentId =>
                await db.Set<BankTransaction>().AnyAsync(row =>
                    row.Id != transaction.Id
                    && row.PortfolioId == command.PortfolioId
                    && row.MatchedLoanPaymentId == loanPaymentId, ct),
            BankReconciliationAction.MatchOwnerDistribution
                when command.OwnerDistributionId is { } ownerDistributionId =>
                await db.Set<BankTransaction>().AnyAsync(row =>
                    row.Id != transaction.Id
                    && row.PortfolioId == command.PortfolioId
                    && row.MatchedOwnerDistributionId == ownerDistributionId, ct),
            _ => false,
        };

    private static async Task AcquireTargetClaimLockAsync(
        ReconcileBankTransactionCommand command,
        int? discoveredClearExpenseId,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var lockId = command.Action switch
        {
            BankReconciliationAction.MatchReceipt
                when command.TenantLedgerEntryId is { } ledgerEntryId =>
                ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "bank-reconcile-receipt", ledgerEntryId),
            BankReconciliationAction.MatchExpense
                when command.ExpenseId is { } expenseId =>
                ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "bank-reconcile-expense", expenseId),
            BankReconciliationAction.Clear
                when discoveredClearExpenseId is { } expenseId =>
                ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "bank-reconcile-expense", expenseId),
            BankReconciliationAction.MatchLoanPayment
                when command.LoanPaymentId is { } loanPaymentId =>
                ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "bank-reconcile-loan-payment", loanPaymentId),
            BankReconciliationAction.MatchOwnerDistribution
                when command.OwnerDistributionId is { } ownerDistributionId =>
                ApplyPlaidConnectionHandler.StableGuid(command.PortfolioId, "bank-reconcile-owner-distribution", ownerDistributionId),
            _ => (Guid?)null,
        };
        if (lockId is { } id)
            await context.AcquireLockAsync("BankTransaction", id, ct);
    }

    private static async Task<ReconciledBankTransactionSnapshot?> SnapshotAsync(
        int portfolioId,
        int transactionId,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        await db.Set<BankTransaction>()
            .Where(row => row.Id == transactionId && row.PortfolioId == portfolioId)
            .Select(row => new ReconciledBankTransactionSnapshot(
                row.Id,
                row.PropertyId,
                row.Property == null ? null : row.Property.Name,
                row.BankConnectionId,
                row.BankConnection == null ? string.Empty : row.BankConnection.InstitutionName,
                row.BankConnection == null ? string.Empty : row.BankConnection.AccountName,
                row.ProviderTransactionId,
                row.PostedAt,
                row.AuthorizedAt,
                row.Description,
                row.MerchantName,
                row.Amount,
                row.IsoCurrencyCode,
                row.Category,
                row.MatchedTenantAccountId,
                row.MatchedTenantLedgerEntryId,
                row.MatchedExpenseId,
                row.MatchedLoanPaymentId,
                row.MatchedOwnerDistributionId,
                row.MatchedBankTransactionId,
                row.MatchStatus,
                row.MatchConfidence,
                row.Notes,
                row.UpdatedAt))
            .SingleOrDefaultAsync(ct);

    private static ReconcileBankTransactionResult Result(
        ReconcileBankTransactionOutcome outcome,
        int id,
        ReconciledBankTransactionSnapshot? transaction = null) =>
        new(outcome, id, transaction);

    private static bool OtherTargetsAreNull(
        BankTransaction row,
        bool exceptTenantLedger = false,
        bool exceptExpense = false,
        bool exceptLoanPayment = false,
        bool exceptOwnerDistribution = false,
        bool exceptBankTransaction = false) =>
        (exceptTenantLedger || (row.MatchedTenantAccountId is null && row.MatchedTenantLedgerEntryId is null))
        && (exceptExpense || row.MatchedExpenseId is null)
        && (exceptLoanPayment || row.MatchedLoanPaymentId is null)
        && (exceptOwnerDistribution || row.MatchedOwnerDistributionId is null)
        && (exceptBankTransaction || row.MatchedBankTransactionId is null);

    private static bool ExpenseMatchProvenanceIsNull(BankTransaction row) =>
        row.ExpenseMatchAppliedAt is null
        && row.ExpenseMatchPreviousStatus is null
        && row.ExpenseMatchPreviousPaidAt is null
        && row.ExpenseMatchPreviousUpdatedAt is null;

    private static async Task<bool> AuthorizeAsync(
        ReconcileBankTransactionCommand command,
        BankTransaction transaction,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        if (command.Action == BankReconciliationAction.MatchTransfer
            || transaction.PropertyId is null)
        {
            return await BankingAuthorizationSupport.HasAllPropertiesAuthorityAsync(
                command.PortfolioId,
                command.ActorUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision,
                command.RequiredCapability,
                db,
                now,
                ct);
        }
        return await BankingAuthorizationSupport.HasPropertyAuthorityAsync(
            command.PortfolioId,
            transaction.PropertyId.Value,
            command.ActorUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            command.RequiredCapability,
            db,
            now,
            ct);
    }

    private static Task<bool> AuthorizeReplayAsync(
        ReconcileBankTransactionCommand command,
        BankReplayAuthorizationRow transaction,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        command.Action == BankReconciliationAction.MatchTransfer || transaction.PropertyId is null
            ? BankingAuthorizationSupport.HasAllPropertiesAuthorityAsync(
                command.PortfolioId, command.ActorUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision, command.RequiredCapability,
                db, now, ct)
            : BankingAuthorizationSupport.HasPropertyAuthorityAsync(
                command.PortfolioId, transaction.PropertyId.Value, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                command.RequiredCapability, db, now, ct);

    private sealed class BankReplayAuthorizationRow
    {
        public int Id { get; set; }
        public int? PropertyId { get; set; }
        public int? MatchedBankTransactionId { get; set; }
    }
}

public sealed class RouteBankTransactionHandler
    : IAtomicCommandHandler<RouteBankTransactionCommand, RouteBankTransactionResult>
{
    private readonly RentalCommandDbContext _db;

    public RouteBankTransactionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RouteBankTransactionResult> HandleAsync(
        RouteBankTransactionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync("BankTransaction", command.TransactionId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);

        var transaction = await _db.Set<BankTransaction>()
            .SingleOrDefaultAsync(row => row.Id == command.TransactionId
                && row.PortfolioId == command.PortfolioId, ct);
        if (transaction is null) return Result(RouteBankTransactionOutcome.TransactionNotFound, command.TransactionId);
        await AuthorizeAsync(command, transaction.PropertyId, _db, now, ct);
        if (transaction.UpdatedAt != command.ExpectedUpdatedAtUtc)
            return Result(RouteBankTransactionOutcome.StaleVersion, transaction.Id);
        if (transaction.PropertyId == command.PropertyId)
            return Result(RouteBankTransactionOutcome.AlreadyApplied, transaction.Id);

        var before = ApplyPlaidSyncHandler.Snapshot(transaction);
        transaction.PropertyId = command.PropertyId;
        transaction.UpdatedAt = command.AppliedAtUtc;
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(ApplyPlaidSyncHandler.TransactionAudit(
            command.PortfolioId, transaction, AuditLogOperation.Updated, before,
            command.PropertyId is null
                ? "Bank transaction operational route removed."
                : $"Bank transaction routed to Property #{command.PropertyId}."));
        return Result(RouteBankTransactionOutcome.Applied, transaction.Id);
    }

    public async Task AuthorizeReplayAsync(
        RouteBankTransactionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        BankingAuthorizationSupport.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var current = await _db.Set<BankTransaction>()
            .Where(row => row.Id == command.TransactionId && row.PortfolioId == command.PortfolioId)
            .Select(row => new { row.PropertyId })
            .SingleOrDefaultAsync(ct);
        if (current is null) throw new UnauthorizedAccessException();
        await AuthorizeAsync(command, current.PropertyId, _db, now, ct);
    }

    private static async Task AuthorizeAsync(
        RouteBankTransactionCommand command,
        int? currentPropertyId,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        if (command.PropertyId is not { } targetPropertyId)
        {
            var canRemoveRoute = await BankingAuthorizationSupport.HasWorkspaceAuthorityAsync(
                command.PortfolioId, command.ActorUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision,
                CapabilityKeys.MoneyReconciliationDestructive, db, now, ct);
            if (!canRemoveRoute) throw new UnauthorizedAccessException();
            return;
        }

        var requiredPropertyIds = currentPropertyId is { } sourcePropertyId && sourcePropertyId != targetPropertyId
            ? new[] { sourcePropertyId, targetPropertyId }
            : new[] { targetPropertyId };
        var canRoute = await BankingAuthorizationSupport.HasPropertyAuthoritiesAsync(
            command.PortfolioId, requiredPropertyIds, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision,
            CapabilityKeys.MoneyReconciliationOperate, db, now, ct);
        if (!canRoute) throw new UnauthorizedAccessException();
    }

    private static RouteBankTransactionResult Result(RouteBankTransactionOutcome outcome, int id) => new(outcome, id);
}

internal static class BankingAuthorizationSupport
{
    internal static void Validate(PreparePlaidTokenExchangeCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || command.RequiredCapability != CapabilityKeys.BankConnectionsManage)
            throw new UnauthorizedAccessException("An active bank-connections workspace context is required.");
    }

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
        long expectedAccessRevision, string capability, RentalCommandDbContext db,
        DateTime now, CancellationToken ct) => HasPropertyAuthoritiesAsync(
            portfolioId, [propertyId], actorUserId, authSessionId, accessContextId,
            expectedAccessRevision, capability, db, now, ct);

    internal static async Task<bool> HasPropertyAuthoritiesAsync(
        int portfolioId, IReadOnlyCollection<int> propertyIds, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision, string capability,
        RentalCommandDbContext db, DateTime now, CancellationToken ct)
    {
        var requiredPropertyIds = propertyIds.Distinct().Order().ToArray();
        if (requiredPropertyIds.Length == 0) return false;

        var liveAssignments = LiveAssignments(
            portfolioId, actorUserId, authSessionId, accessContextId,
            expectedAccessRevision, capability, db, now);
        var authorizedPropertyCount = await db.Set<Property>()
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
        long expectedAccessRevision, string capability, RentalCommandDbContext db,
        DateTime now, CancellationToken ct) =>
        LiveAssignments(portfolioId, actorUserId, authSessionId, accessContextId,
                expectedAccessRevision, capability, db, now)
            .AnyAsync(assignment => assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition!.Key == capability
                && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Workspace), ct);

    internal static Task<bool> HasAllPropertiesAuthorityAsync(
        int portfolioId, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision, string capability, RentalCommandDbContext db,
        DateTime now, CancellationToken ct) =>
        LiveAssignments(portfolioId, actorUserId, authSessionId, accessContextId,
                expectedAccessRevision, capability, db, now)
            .AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);

    private static IQueryable<MembershipRoleAssignment> LiveAssignments(
        int portfolioId, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision, string capability, RentalCommandDbContext db, DateTime now) =>
        db.Set<MembershipRoleAssignment>().Where(assignment =>
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
            && db.Set<AuthSession>().Any(session => session.Id == authSessionId
                && session.UserId == actorUserId && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition!.Key == capability));
}
