using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public interface IAccountingLifecycleWorkspaceCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int ActorUserId { get; }
    [AtomicFingerprintIgnore]
    Guid AuthSessionId { get; }
    [AtomicFingerprintIgnore]
    int AccessContextId { get; }
    [AtomicFingerprintIgnore]
    long ExpectedAccessRevision { get; }
    AccountingProvider Provider { get; }
    [AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey { get; }
}

public sealed record PrepareAccountingDisconnectCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AccountingProvider Provider,
    string DeliveryIdempotencyKey) : IAccountingLifecycleWorkspaceCommand;

public enum PrepareAccountingDisconnectOutcome
{
    NotFound,
    Prepared,
    AlreadyDisconnected,
}

public sealed record PrepareAccountingDisconnectResult(
    PrepareAccountingDisconnectOutcome Outcome,
    int ConnectionId,
    long TokenGeneration,
    DateTime? PreparedAtUtc,
    string? RefreshTokenCipherText);

public sealed record FinalizeAccountingDisconnectCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AccountingProvider Provider,
    int ConnectionId,
    long PreparedTokenGeneration,
    [property: AtomicFingerprintIgnore]
    DateTime PreparedAtUtc,
    string DeliveryIdempotencyKey) : IAccountingLifecycleWorkspaceCommand;

public enum FinalizeAccountingDisconnectOutcome
{
    NotFound,
    Applied,
    AlreadyFinalized,
    Superseded,
}

public sealed record FinalizeAccountingDisconnectResult(
    FinalizeAccountingDisconnectOutcome Outcome,
    int ConnectionId,
    long TokenGeneration,
    DateTime? DisconnectedAtUtc);

public sealed record SetAccountingDirectionCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AccountingProvider Provider,
    bool? PullEnabled,
    bool? PushEnabled,
    string DeliveryIdempotencyKey) : IAccountingLifecycleWorkspaceCommand;

public sealed record SetAccountingDirectionResult(
    bool Found,
    int ConnectionId,
    bool PullEnabled,
    bool PushEnabled,
    DateTime? UpdatedAtUtc);

public sealed class PrepareAccountingDisconnectHandler
{
    private readonly RentalCommandDbContext _db;

    public PrepareAccountingDisconnectHandler(RentalCommandDbContext db) => _db = db;

    public async Task<PrepareAccountingDisconnectResult> ExecuteAsync(
        PrepareAccountingDisconnectCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        AccountingLifecycleCommandSupport.Validate(command);
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        await AccountingLifecycleCommandSupport.RequireAuthorizationAsync(command, _db, now, ct);

        var connectionId = await AccountingLifecycleCommandSupport.ResolveAndLockConnectionIdAsync(
            _db, command, attempt, ct);
        if (connectionId is null)
        {
            return new PrepareAccountingDisconnectResult(
                PrepareAccountingDisconnectOutcome.NotFound, 0, 0, null, null);
        }

        var connection = await _db.Set<AccountingConnection>()
            .SingleAsync(row => row.Id == connectionId.Value
                && row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider, ct);
        if (AccountingLifecycleCommandSupport.IsFullyDisconnected(connection))
        {
            return new PrepareAccountingDisconnectResult(
                PrepareAccountingDisconnectOutcome.AlreadyDisconnected,
                connection.Id,
                connection.TokenGeneration,
                connection.DisconnectedAt,
                null);
        }

        var refreshTokenCipherText = connection.RefreshTokenCipherText;
        var oldValues = JsonSerializer.Serialize(new
        {
            connection.Status,
            HadAccessToken = connection.AccessTokenCipherText != null,
            HadRefreshToken = refreshTokenCipherText != null,
            connection.TokenGeneration,
        });

        connection.Status = AccountingConnectionStatus.Disconnected;
        connection.AccessTokenCipherText = null;
        connection.TokenExpiresAt = null;
        connection.PullClaimOwner = null;
        connection.PullClaimToken = null;
        connection.PullClaimExpiresAtUtc = null;
        connection.TokenRotationState = AccountingTokenRotationState.Idle;
        connection.TokenRotationClaimOwner = null;
        connection.TokenRotationClaimToken = null;
        connection.TokenRotationClaimExpiresAtUtc = null;
        connection.TokenGeneration++;
        connection.DisconnectedAt = now;
        connection.UpdatedAt = now;

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(connection, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AccountingConnection),
            connection.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            OldValues: oldValues,
            NewValues: JsonSerializer.Serialize(new
            {
                connection.Status,
                HadAccessToken = false,
                ProviderRevokePending = refreshTokenCipherText != null,
                connection.TokenGeneration,
                connection.DisconnectedAt,
            }),
            ChangeReason: "Accounting provider disconnect prepared; local access and worker claims disabled."));
        await attempt.FlushBusinessAsync(ct);

        AccountingLifecycleCommandSupport.StageDataUpdate(
            attempt, command, connection, now, "disconnect-prepare");
        return new PrepareAccountingDisconnectResult(
            PrepareAccountingDisconnectOutcome.Prepared,
            connection.Id,
            connection.TokenGeneration,
            now,
            refreshTokenCipherText);
    }

    public Task AuthorizeReplayAsync(
        PrepareAccountingDisconnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw AccountingWriteSupport.RetiredPath();

    public Task AuthorizeAsync(
        PrepareAccountingDisconnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        AccountingLifecycleCommandSupport.AuthorizeReplayAsync(command, _db, context, ct);
}

public sealed class FinalizeAccountingDisconnectHandler
{
    private readonly RentalCommandDbContext _db;

    public FinalizeAccountingDisconnectHandler(RentalCommandDbContext db) => _db = db;

    public async Task<FinalizeAccountingDisconnectResult> ExecuteAsync(
        FinalizeAccountingDisconnectCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        AccountingLifecycleCommandSupport.Validate(command);
        if (command.ConnectionId <= 0 || command.PreparedTokenGeneration <= 0)
        {
            throw new ArgumentException("A prepared accounting connection and token generation are required.");
        }

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        await AccountingLifecycleCommandSupport.RequireAuthorizationAsync(command, _db, now, ct);

        var connection = await _db.Set<AccountingConnection>()
            .SingleOrDefaultAsync(row => row.Id == command.ConnectionId
                && row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider, ct);
        if (connection is null)
        {
            return new FinalizeAccountingDisconnectResult(
                FinalizeAccountingDisconnectOutcome.NotFound,
                command.ConnectionId,
                command.PreparedTokenGeneration,
                null);
        }

        if (connection.Status != AccountingConnectionStatus.Disconnected
            || connection.TokenGeneration != command.PreparedTokenGeneration
            || connection.DisconnectedAt != command.PreparedAtUtc)
        {
            return new FinalizeAccountingDisconnectResult(
                FinalizeAccountingDisconnectOutcome.Superseded,
                connection.Id,
                connection.TokenGeneration,
                connection.DisconnectedAt);
        }

        if (connection.RefreshTokenCipherText is null)
        {
            return new FinalizeAccountingDisconnectResult(
                FinalizeAccountingDisconnectOutcome.AlreadyFinalized,
                connection.Id,
                connection.TokenGeneration,
                connection.DisconnectedAt);
        }

        connection.RefreshTokenCipherText = null;
        connection.UpdatedAt = now;
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(connection, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AccountingConnection),
            connection.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                connection.Status,
                RefreshCredentialRetained = false,
                connection.TokenGeneration,
                connection.DisconnectedAt,
            }),
            ChangeReason: "Accounting provider disconnect finalized; retained revoke credential cleared."));
        await attempt.FlushBusinessAsync(ct);

        return new FinalizeAccountingDisconnectResult(
            FinalizeAccountingDisconnectOutcome.Applied,
            connection.Id,
            connection.TokenGeneration,
            connection.DisconnectedAt);
    }

    public Task AuthorizeReplayAsync(
        FinalizeAccountingDisconnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw AccountingWriteSupport.RetiredPath();

    public Task AuthorizeAsync(
        FinalizeAccountingDisconnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        AccountingLifecycleCommandSupport.AuthorizeReplayAsync(command, _db, context, ct);
}

public sealed class SetAccountingDirectionHandler
{
    private readonly RentalCommandDbContext _db;

    public SetAccountingDirectionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SetAccountingDirectionResult> ExecuteAsync(
        SetAccountingDirectionCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        AccountingLifecycleCommandSupport.Validate(command);
        if (command.PullEnabled is null && command.PushEnabled is null)
        {
            throw new ArgumentException("At least one accounting direction must be specified.");
        }

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        await AccountingLifecycleCommandSupport.RequireAuthorizationAsync(command, _db, now, ct);

        var connectionId = await AccountingLifecycleCommandSupport.ResolveAndLockConnectionIdAsync(
            _db, command, attempt, ct);
        if (connectionId is null)
        {
            return new SetAccountingDirectionResult(false, 0, false, false, null);
        }

        var connection = await _db.Set<AccountingConnection>()
            .SingleAsync(row => row.Id == connectionId.Value
                && row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider, ct);
        var oldValues = JsonSerializer.Serialize(new
        {
            connection.PullEnabled,
            connection.PushEnabled,
        });
        if (command.PullEnabled.HasValue)
        {
            connection.PullEnabled = command.PullEnabled.Value;
        }
        if (command.PushEnabled.HasValue)
        {
            connection.PushEnabled = command.PushEnabled.Value;
        }
        if (command.PullEnabled == false)
        {
            connection.PullClaimOwner = null;
            connection.PullClaimToken = null;
            connection.PullClaimExpiresAtUtc = null;
        }
        connection.UpdatedAt = now;

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(connection, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AccountingConnection),
            connection.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            OldValues: oldValues,
            NewValues: JsonSerializer.Serialize(new
            {
                connection.PullEnabled,
                connection.PushEnabled,
            }),
            ChangeReason: "Accounting synchronization direction updated."));
        await attempt.FlushBusinessAsync(ct);

        AccountingLifecycleCommandSupport.StageDataUpdate(
            attempt, command, connection, now, "direction");
        return new SetAccountingDirectionResult(
            true,
            connection.Id,
            connection.PullEnabled,
            connection.PushEnabled,
            now);
    }

    public Task AuthorizeReplayAsync(
        SetAccountingDirectionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw AccountingWriteSupport.RetiredPath();

    public Task AuthorizeAsync(
        SetAccountingDirectionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        AccountingLifecycleCommandSupport.AuthorizeReplayAsync(command, _db, context, ct);
}

internal static class AccountingLifecycleCommandSupport
{
    public static async Task<int?> ResolveAndLockConnectionIdAsync(
        RentalCommandDbContext db,
        IAccountingLifecycleWorkspaceCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        var connectionId = await db.Set<AccountingConnection>().AsNoTracking()
            .Where(row => row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider)
            .Select(row => (int?)row.Id)
            .SingleOrDefaultAsync(ct);
        if (connectionId.HasValue)
        {
            await attempt.AcquireLockAsync(
                "AccountingConnection", connectionId.Value, ct);
        }
        return connectionId;
    }

    public static async Task RequireAuthorizationAsync(
        IAccountingLifecycleWorkspaceCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(command, db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or no longer permits accounting integrations.");
        }
    }

    public static async Task AuthorizeReplayAsync(
        IAccountingLifecycleWorkspaceCommand command,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await RequireAuthorizationAsync(command, db, now, ct);
    }

    public static void Validate(IAccountingLifecycleWorkspaceCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || !Enum.IsDefined(command.Provider)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Portfolio, actor, provider, access revision, and delivery identifier are required.");
        }
    }

    public static bool IsFullyDisconnected(AccountingConnection connection) =>
        connection.Status == AccountingConnectionStatus.Disconnected
        && connection.AccessTokenCipherText is null
        && connection.RefreshTokenCipherText is null
        && connection.TokenExpiresAt is null
        && connection.PullClaimOwner is null
        && connection.PullClaimToken is null
        && connection.PullClaimExpiresAtUtc is null
        && connection.TokenRotationState == AccountingTokenRotationState.Idle
        && connection.TokenRotationClaimOwner is null
        && connection.TokenRotationClaimToken is null
        && connection.TokenRotationClaimExpiresAtUtc is null;

    public static void StageDataUpdate(
        IAtomicCommandContext attempt,
        IAccountingLifecycleWorkspaceCommand command,
        AccountingConnection connection,
        DateTime now,
        string suffix) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(AccountingConnection),
                entityId = connection.Id,
                operation = "update",
                data = new
                {
                    provider = connection.Provider,
                    status = connection.Status,
                    pullEnabled = connection.PullEnabled,
                    pushEnabled = connection.PushEnabled,
                },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private static Task<bool> IsAuthorizedAsync(
        IAccountingLifecycleWorkspaceCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.IntegrationsManage
                && grant.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Workspace), ct);
}

public static class AtomicAccountingLifecycle
{
    public static readonly AtomicJsonResultCodec<PrepareAccountingDisconnectResult> DisconnectPrepareCodec =
        new("rental.accounting-disconnect.prepare.v1");
    public static readonly AtomicJsonResultCodec<FinalizeAccountingDisconnectResult> DisconnectFinalizeCodec =
        new("rental.accounting-disconnect.finalize.v1");
    public static readonly AtomicJsonResultCodec<SetAccountingDirectionResult> DirectionCodec =
        new("rental.accounting-direction.set.v1");

    public static PrepareAccountingDisconnectCommand PrepareDisconnectCommand(
        WorkspaceReadScope scope,
        AccountingProvider provider,
        string operationKey) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            provider,
            operationKey.Trim());

    public static AtomicCommandIdentity PrepareDisconnectIdentity(
        PrepareAccountingDisconnectCommand command) => new(
            "accounting.connection.disconnect.prepare",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.Provider}:" +
            command.DeliveryIdempotencyKey);

    public static FinalizeAccountingDisconnectCommand FinalizeDisconnectCommand(
        PrepareAccountingDisconnectCommand prepare,
        PrepareAccountingDisconnectResult prepared) => new(
            prepare.PortfolioId,
            prepare.ActorUserId,
            prepare.AuthSessionId,
            prepare.AccessContextId,
            prepare.ExpectedAccessRevision,
            prepare.Provider,
            prepared.ConnectionId,
            prepared.TokenGeneration,
            prepared.PreparedAtUtc
                ?? throw new AtomicReceiptInvariantException(
                    "A prepared accounting disconnect must include its database timestamp."),
            prepare.DeliveryIdempotencyKey);

    public static AtomicCommandIdentity FinalizeDisconnectIdentity(
        FinalizeAccountingDisconnectCommand command) => new(
            "accounting.connection.disconnect.finalize",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.Provider}:" +
            command.DeliveryIdempotencyKey);

    public static SetAccountingDirectionCommand DirectionCommand(
        WorkspaceReadScope scope,
        AccountingProvider provider,
        bool? pull,
        bool? push,
        string operationKey) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            provider,
            pull,
            push,
            operationKey.Trim());

    public static AtomicCommandIdentity DirectionIdentity(SetAccountingDirectionCommand command) => new(
        "accounting.connection.direction.set",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Provider}:" +
        command.DeliveryIdempotencyKey);
}

public static class AccountingWriteSupport
{
    public static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var (operationName, resultContract) = command switch
        {
            CreateLedgerAccountCommand =>
                ("accounting.ledger-account.create", "accounting.ledger-account.mutation.v1"),
            UpdateLedgerAccountCommand =>
                ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1"),
            PrepareAccountingConnectCommand =>
                ("accounting.oauth-state.prepare", "rental.accounting-connect.prepare.v1"),
            CancelTenantAutopayCommand =>
                ("tenant-autopay.cancel", "rental.tenant-autopay.cancel.v1"),
            PrepareAccountingDisconnectCommand =>
                ("accounting.connection.disconnect.prepare", "rental.accounting-disconnect.prepare.v1"),
            FinalizeAccountingDisconnectCommand =>
                ("accounting.connection.disconnect.finalize", "rental.accounting-disconnect.finalize.v1"),
            SetAccountingDirectionCommand =>
                ("accounting.connection.direction.set", "rental.accounting-direction.set.v1"),
            ApplyAccountingPullResultCommand =>
                ("accounting.pull.apply", "accounting.pull.apply.v1"),
            ConfirmAccountingMappingCommand =>
                ("accounting.mapping.confirm", "accounting.mapping.confirm.result.v2"),
            ContinueAccountingMappingPromotionCommand =>
                ("accounting.mapping.promote.continue", "accounting.mapping.promote.continue.result.v1"),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        var lockPlan = command switch
        {
            CreateLedgerAccountCommand create => AuthorizationScope(
                create.ActorAuthSessionId, create.ActorAccessContextId, create.PortfolioId),
            UpdateLedgerAccountCommand update => new WriteLockPlan(
                WriteLockProtocol.AuthorizationScopeLedgerAccount,
                update.ActorAuthSessionId,
                update.ActorAccessContextId,
                update.PortfolioId,
                update.AccountId),
            PrepareAccountingConnectCommand connect => AuthorizationScope(
                connect.AuthSessionId, connect.AccessContextId, connect.PortfolioId),
            CancelTenantAutopayCommand autopay => new WriteLockPlan(
                WriteLockProtocol.AuthorizationScopeTenantAccount,
                autopay.TenantAuthSessionId,
                autopay.TenantAccessContextId,
                autopay.TenantAccountId),
            PrepareAccountingDisconnectCommand prepare => AuthorizationScope(
                prepare.AuthSessionId, prepare.AccessContextId, prepare.PortfolioId),
            FinalizeAccountingDisconnectCommand finalize => AuthorizationAccountingConnection(
                finalize.AuthSessionId, finalize.AccessContextId, finalize.ConnectionId),
            SetAccountingDirectionCommand direction => AuthorizationScope(
                direction.AuthSessionId, direction.AccessContextId, direction.PortfolioId),
            ApplyAccountingPullResultCommand pull => new WriteLockPlan(
                WriteLockProtocol.AccountingConnection,
                pull.AccountingConnectionId),
            ConfirmAccountingMappingCommand mapping => AuthorizationAccountingConnection(
                mapping.AuthSessionId, mapping.AccessContextId, mapping.AccountingConnectionId),
            ContinueAccountingMappingPromotionCommand continuation => AuthorizationAccountingConnection(
                continuation.AuthSessionId, continuation.AccessContextId,
                continuation.AccountingConnectionId),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        return new TransactionalWrite<TCommand, TResult>(
            operationName,
            WriteIdempotencyPolicy.Required,
            command,
            resultContract,
            lockPlan,
            executeAsync,
            authorizeReplayAsync);
    }

    internal static InvalidOperationException RetiredPath() => new(
        "Legacy atomic accounting writes are retired; use the shared write executor.");

    private static WriteLockPlan AuthorizationScope(Guid sessionId, int accessContextId, int portfolioId) =>
        new(
            WriteLockProtocol.AuthorizationScope,
            sessionId,
            accessContextId,
            portfolioId);

    private static WriteLockPlan AuthorizationAccountingConnection(
        Guid sessionId, int accessContextId, int connectionId) =>
        new(
            WriteLockProtocol.AuthorizationScopeAccountingConnection,
            sessionId,
            accessContextId,
            connectionId);
}
