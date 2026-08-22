using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Time;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed record PrepareAccountingConnectCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AccountingProvider Provider,
    string RedirectUri,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record PrepareAccountingConnectResult(
    string StateToken,
    string RedirectUri,
    DateTime ExpiresAtUtc);

public sealed class PrepareAccountingConnectHandler
{
    private readonly RentalCommandDbContext _db;

    public PrepareAccountingConnectHandler(RentalCommandDbContext db) => _db = db;

    private readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    public async Task<PrepareAccountingConnectResult> ExecuteAsync(
        PrepareAccountingConnectCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or no longer permits accounting integrations.");
        }

        // One provider row exists at most. Removing a stale Pending row and staging the new
        // single-use OAuth state happen under the receipt owner's transaction.
        var stalePending = await _db.Set<AccountingConnection>()
            .SingleOrDefaultAsync(connection => connection.PortfolioId == command.PortfolioId
                && connection.Provider == command.Provider
                && connection.Status == AccountingConnectionStatus.Pending, ct);
        if (stalePending is not null)
        {
            _db.Remove(stalePending);
            attempt.UseDatabaseWallClockForAudit(now);
            attempt.BindSemanticAudit(stalePending, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(AccountingConnection),
                stalePending.Id,
                AuditLogOperation.Deleted,
                command.ActorUserId,
                ChangeReason: "Replaced stale pending accounting connection before OAuth authorization."));
        }

        var stateToken = GenerateBase64UrlToken(32);
        var expiresAtUtc = now.Add(StateTtl);
        _db.Add(new OAuthState
        {
            PortfolioId = command.PortfolioId,
            Provider = command.Provider,
            StateToken = stateToken,
            RedirectUri = command.RedirectUri,
            CodeVerifier = null,
            CreatedAt = now,
            ExpiresAt = expiresAtUtc,
        });
        await attempt.FlushBusinessAsync(ct);

        return new PrepareAccountingConnectResult(
            stateToken, command.RedirectUri, expiresAtUtc);
    }

    public Task AuthorizeReplayAsync(
        PrepareAccountingConnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw AccountingWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        PrepareAccountingConnectCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or no longer permits accounting integrations.");
        }
    }

    private Task<bool> IsAuthorizedAsync(
        PrepareAccountingConnectCommand command,
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

    private void Validate(PrepareAccountingConnectCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || !Enum.IsDefined(command.Provider)
            || string.IsNullOrWhiteSpace(command.RedirectUri)
            || command.RedirectUri.Length > 2048
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Portfolio, actor, provider, redirect URI, access revision, and delivery identifier are required.");
        }
    }

    private string GenerateBase64UrlToken(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

public sealed record CancelTenantAutopayCommand(
    int PortfolioId,
    int TenantUserId,
    Guid TenantAuthSessionId,
    int TenantAccessContextId,
    long TenantAccessRevision,
    int TenantId,
    int TenantAccountId,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record CancelTenantAutopayResult(
    bool Found,
    bool Applied,
    int TenantAccountId);

public sealed class CancelTenantAutopayHandler
{
    private readonly RentalCommandDbContext _db;

    public CancelTenantAutopayHandler(RentalCommandDbContext db) => _db = db;

    public async Task<CancelTenantAutopayResult> ExecuteAsync(
        CancelTenantAutopayCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await AuthorizedAccount(command, _db, times)
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return new CancelTenantAutopayResult(false, false, command.TenantAccountId);
        }

        if (target.Enrollment is null)
        {
            return new CancelTenantAutopayResult(true, false, command.TenantAccountId);
        }

        target.Enrollment.CanceledAtUtc = times.WallClockUtc;
        target.Enrollment.CancelReason = "Canceled by tenant";
        attempt.UseDatabaseWallClockForAudit(times.WallClockUtc);
        attempt.BindSemanticAudit(target.Enrollment, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(TenantAutopayEnrollment),
            target.Enrollment.Id,
            AuditLogOperation.Updated,
            command.TenantUserId,
            OldValues: JsonSerializer.Serialize(new
            {
                target.Enrollment.TenantAccountId,
                Active = true,
            }),
            NewValues: JsonSerializer.Serialize(new
            {
                target.Enrollment.TenantAccountId,
                Active = false,
                target.Enrollment.CanceledAtUtc,
                target.Enrollment.CancelReason,
            }),
            ChangeReason: "Tenant canceled autopay."));
        await attempt.FlushBusinessAsync(ct);

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(TenantAutopayEnrollment),
                entityId = target.Enrollment.Id,
                operation = "update",
                data = new
                {
                    tenantAccountId = command.TenantAccountId,
                    active = false,
                },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = times.WallClockUtc,
            NextAttemptAtUtc = times.WallClockUtc,
        });

        return new CancelTenantAutopayResult(true, true, command.TenantAccountId);
    }

    public Task AuthorizeReplayAsync(
        CancelTenantAutopayCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw AccountingWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        CancelTenantAutopayCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        if (!await AuthorizedAccount(command, _db, times).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException(
                "The tenant relationship no longer authorizes this autopay account.");
        }
    }

    private IQueryable<AuthorizedAutopayAccount> AuthorizedAccount(
        CancelTenantAutopayCommand command,
        RentalCommandDbContext db,
        AtomicCommandTimes times) =>
        from account in db.Set<TenantAccount>()
        join party in db.Set<LeaseManagementParty>()
            on new { account.LeaseManagementId, account.PortfolioId }
            equals new { party.LeaseManagementId, party.PortfolioId }
        join access in db.Set<TenantUserAccess>()
            on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
            equals new { access.LeaseManagementPartyId, access.PortfolioId }
        from enrollment in db.Set<TenantAutopayEnrollment>()
            .Where(item => item.TenantAccountId == account.Id
                && item.PortfolioId == account.PortfolioId
                && item.CanceledAtUtc == null)
            .DefaultIfEmpty()
        where account.Id == command.TenantAccountId
            && account.PortfolioId == command.PortfolioId
            && account.ClosedAtUtc == null
            && party.TenantId == command.TenantId
            && party.EffectiveFrom <= times.BusinessDate
            && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
            && party.Role != LeaseManagementPartyRole.Occupant
            && access.AccessContextId == command.TenantAccessContextId
            && access.ApplicationUserId == command.TenantUserId
            && access.PortfolioId == command.PortfolioId
            && access.RevokedAtUtc == null
            && access.AccessContext != null
            && access.AccessContext.UserId == command.TenantUserId
            && access.AccessContext.PortfolioId == command.PortfolioId
            && access.AccessContext.AccessRevision == command.TenantAccessRevision
            && access.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && access.AccessContext.SuspendedAtUtc == null
            && access.AccessContext.RevokedAtUtc == null
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.TenantAuthSessionId
                && session.UserId == command.TenantUserId
                && session.ActiveAccessContextId == command.TenantAccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > times.WallClockUtc)
        orderby party.Id
        select new AuthorizedAutopayAccount(account.Id, enrollment);

    private void Validate(CancelTenantAutopayCommand command)
    {
        if (command.PortfolioId <= 0
            || command.TenantUserId <= 0
            || command.TenantAuthSessionId == Guid.Empty
            || command.TenantAccessContextId <= 0
            || command.TenantAccessRevision <= 0
            || command.TenantId <= 0
            || command.TenantAccountId <= 0
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Tenant session, relationship, account, access revision, and delivery identifier are required.");
        }
    }

    private sealed record AuthorizedAutopayAccount(
        int TenantAccountId,
        TenantAutopayEnrollment? Enrollment);
}

public static class AtomicAccountingConnect
{
    public static readonly AtomicJsonResultCodec<PrepareAccountingConnectResult> Codec =
        new("rental.accounting-connect.prepare.v1");

    public static PrepareAccountingConnectCommand Command(
        WorkspaceReadScope scope,
        AccountingProvider provider,
        string redirectUri,
        string operationKey) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            provider,
            redirectUri,
            operationKey.Trim());

    public static AtomicCommandIdentity Identity(PrepareAccountingConnectCommand command) => new(
        "accounting.oauth-state.prepare",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Provider}:" +
        command.DeliveryIdempotencyKey);
}

public static class AtomicTenantAutopayCancellation
{
    public static readonly AtomicJsonResultCodec<CancelTenantAutopayResult> Codec =
        new("rental.tenant-autopay.cancel.v1");

    public static CancelTenantAutopayCommand Command(
        ActiveAccessContext access,
        int tenantId,
        int tenantAccountId,
        string operationKey) => new(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            tenantId,
            tenantAccountId,
            operationKey.Trim());

    public static AtomicCommandIdentity Identity(CancelTenantAutopayCommand command) => new(
        "tenant-autopay.cancel",
        $"{command.PortfolioId}:{command.TenantAccessContextId}:{command.TenantAccountId}:" +
        command.DeliveryIdempotencyKey);
}
