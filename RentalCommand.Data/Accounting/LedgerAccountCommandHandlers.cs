using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

public sealed class CreateLedgerAccountHandler
    : IAtomicCommandHandler<CreateLedgerAccountCommand, LedgerAccountMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CreateLedgerAccountHandler(RentalCommandDbContext db) => _db = db;

    public Task<LedgerAccountMutationResult> HandleAsync(
        CreateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public async Task<LedgerAccountMutationResult> ExecuteAsync(
        CreateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LedgerAccountAtomicSupport.ValidateEnvelope(command);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await LedgerAccountAtomicSupport.EnsureAuthorizedAsync(command, _db, now, ct);
        LedgerAccountAtomicSupport.ValidateCreate(command);

        if (command.ParentAccountId is int parentAccountId && parentAccountId > 0)
        {
            await context.AcquireLockAsync("LedgerAccount", parentAccountId, ct);
        }

        await context.AcquireLockAsync("LedgerAccountCode", command.PortfolioId, ct);
        var code = await LedgerAccountAtomicSupport.ResolveCodeAsync(_db, command, ct);
        var parentId = await LedgerAccountAtomicSupport.ValidateParentAsync(
            _db,
            command.PortfolioId,
            command.ParentAccountId,
            command.AccountType,
            parentAccountIdToExclude: null,
            ct);

        var account = new LedgerAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            Code = code,
            Name = command.Name.Trim(),
            AccountType = command.AccountType,
            NormalBalance = LedgerAccountAtomicSupport.NormalBalanceFor(command.AccountType),
            ParentAccountId = parentId,
            // SystemKey and IsSystem are seed-service-only state.
            SystemKey = null,
            ScheduleECategory = command.ScheduleECategory,
            IsSystem = false,
            IsActive = command.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        _db.LedgerAccounts.Add(account);
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(
            account,
            new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LedgerAccount),
                0,
                AuditLogOperation.Created,
                command.ActorUserId,
                NewValues: LedgerAccountAtomicSupport.Serialize(account),
                ChangeReason: "Created ledger account."));
        await context.FlushBusinessAsync(ct);

        context.StageOutbox(LedgerAccountAtomicSupport.DataUpdate(
            command.PortfolioId,
            account.Id,
            "create",
            command.DeliveryIdempotencyKey,
            now));

        return new(
            LedgerAccountMutationOutcome.Applied,
            LedgerAccountAtomicSupport.Snapshot(account, hasPostedLines: false));
    }

    public Task AuthorizeReplayAsync(
        CreateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public Task AuthorizeAsync(
        CreateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        LedgerAccountAtomicSupport.AuthorizeReplayAsync(command, _db, ct);

    private static InvalidOperationException RetiredPath() => new(
        "Legacy atomic ledger-account writes are retired; use the shared write executor.");
}

public sealed class UpdateLedgerAccountHandler
    : IAtomicCommandHandler<UpdateLedgerAccountCommand, LedgerAccountMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateLedgerAccountHandler(RentalCommandDbContext db) => _db = db;

    public Task<LedgerAccountMutationResult> HandleAsync(
        UpdateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public async Task<LedgerAccountMutationResult> ExecuteAsync(
        UpdateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LedgerAccountAtomicSupport.ValidateEnvelope(command);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await LedgerAccountAtomicSupport.EnsureAuthorizedAsync(command, _db, now, ct);

        var account = await _db.LedgerAccounts
            .SingleOrDefaultAsync(row =>
                row.Id == command.AccountId && row.PortfolioId == command.PortfolioId, ct);
        if (account is null)
        {
            return new(LedgerAccountMutationOutcome.NotFound, null);
        }

        var parentIdToLock = command.ParentAccountIdSpecified
            ? command.ParentAccountId
            : account.ParentAccountId;
        if (parentIdToLock is int parentId && parentId > 0 && parentId != account.Id)
        {
            await context.AcquireLockAsync("LedgerAccount", parentId, ct);
        }

        var hasPostedLines = await _db.JournalLines
            .AsNoTracking()
            .AnyAsync(line => line.LedgerAccountId == account.Id, ct);

        if (command.Delete)
        {
            LedgerAccountAtomicSupport.ValidateDelete(account, hasPostedLines);
            var hasChildren = await _db.LedgerAccounts
                .AsNoTracking()
                .AnyAsync(child =>
                    child.PortfolioId == command.PortfolioId
                    && child.ParentAccountId == account.Id, ct);
            if (hasChildren)
            {
                throw new DomainValidationException(
                    "A ledger account with child accounts cannot be deleted.");
            }

            var prior = LedgerAccountAtomicSupport.Snapshot(account, hasPostedLines);
            _db.LedgerAccounts.Remove(account);
            context.UseDatabaseWallClockForAudit(now);
            context.BindSemanticAudit(
                account,
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(LedgerAccount),
                    account.Id,
                    AuditLogOperation.Deleted,
                    command.ActorUserId,
                    OldValues: LedgerAccountAtomicSupport.Serialize(prior),
                    ChangeReason: "Deleted ledger account."));
            await context.FlushBusinessAsync(ct);

            context.StageOutbox(LedgerAccountAtomicSupport.DataUpdate(
                command.PortfolioId,
                account.Id,
                "delete",
                command.DeliveryIdempotencyKey,
                now));

            return new(LedgerAccountMutationOutcome.Deleted, prior);
        }

        LedgerAccountAtomicSupport.ValidateUpdateType(command.AccountType);
        var nextType = command.AccountType ?? account.AccountType;
        if (account.IsSystem && command.AccountType.HasValue
            && command.AccountType.Value != account.AccountType)
        {
            throw new DomainValidationException(
                "System ledger accounts cannot be retyped.");
        }

        if (hasPostedLines && nextType != account.AccountType)
        {
            throw new DomainValidationException(
                "A ledger account with posted journal lines cannot be retyped.");
        }

        if (!account.IsSystem && !LedgerAccountAtomicSupport.IsUserAccountType(nextType))
        {
            throw new DomainValidationException(
                "User-created ledger accounts must be Income or Expense accounts.");
        }

        if (nextType != account.AccountType)
        {
            var hasChildren = await _db.LedgerAccounts
                .AsNoTracking()
                .AnyAsync(child =>
                    child.PortfolioId == command.PortfolioId
                    && child.ParentAccountId == account.Id, ct);
            if (hasChildren)
            {
                throw new DomainValidationException(
                    "A ledger account with child accounts cannot be retyped because parent and child account types must match.");
            }
        }

        var nextParentId = command.ParentAccountIdSpecified
            ? command.ParentAccountId
            : account.ParentAccountId;
        if (nextParentId.HasValue)
        {
            nextParentId = await LedgerAccountAtomicSupport.ValidateParentAsync(
                _db,
                command.PortfolioId,
                nextParentId,
                nextType,
                command.AccountId,
                ct);
        }

        var nextName = account.Name;
        if (command.Name is not null)
        {
            nextName = command.Name.Trim();
            if (nextName.Length == 0 || nextName.Length > LedgerAccountAtomicSupport.MaxNameLength)
            {
                throw new DomainValidationException(
                    $"Ledger account name must be between 1 and {LedgerAccountAtomicSupport.MaxNameLength} characters.");
            }
        }

        if (command.IsActive == false
            && account.IsSystem
            && LedgerAccountAtomicSupport.IsProtectedSystemControl(account.SystemKey))
        {
            throw new DomainValidationException(
                "This system control account cannot be deactivated.");
        }

        var nextActive = command.IsActive ?? account.IsActive;
        var nextSchedule = command.ScheduleECategory ?? account.ScheduleECategory;
        var nextNormalBalance = account.IsSystem
            ? account.NormalBalance
            : LedgerAccountAtomicSupport.NormalBalanceFor(nextType);
        var hasChanges = account.Name != nextName
            || account.AccountType != nextType
            || account.ParentAccountId != nextParentId
            || account.ScheduleECategory != nextSchedule
            || account.IsActive != nextActive
            || account.NormalBalance != nextNormalBalance;

        if (!hasChanges)
        {
            return new(
                LedgerAccountMutationOutcome.Applied,
                LedgerAccountAtomicSupport.Snapshot(account, hasPostedLines));
        }

        var priorSnapshot = LedgerAccountAtomicSupport.Snapshot(account, hasPostedLines);
        account.Name = nextName;
        account.AccountType = nextType;
        account.NormalBalance = nextNormalBalance;
        account.ParentAccountId = nextParentId;
        account.ScheduleECategory = nextSchedule;
        account.IsActive = nextActive;
        account.UpdatedAtUtc = now;
        var nextSnapshot = LedgerAccountAtomicSupport.Snapshot(account, hasPostedLines);

        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(
            account,
            new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LedgerAccount),
                account.Id,
                AuditLogOperation.Updated,
                command.ActorUserId,
                OldValues: LedgerAccountAtomicSupport.Serialize(priorSnapshot),
                NewValues: LedgerAccountAtomicSupport.Serialize(nextSnapshot),
                ChangeReason: "Updated ledger account."));
        await context.FlushBusinessAsync(ct);

        context.StageOutbox(LedgerAccountAtomicSupport.DataUpdate(
            command.PortfolioId,
            account.Id,
            "update",
            command.DeliveryIdempotencyKey,
            now));

        return new(LedgerAccountMutationOutcome.Applied, nextSnapshot);
    }

    public Task AuthorizeReplayAsync(
        UpdateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public Task AuthorizeAsync(
        UpdateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        LedgerAccountAtomicSupport.AuthorizeReplayAsync(command, _db, ct);

    private static InvalidOperationException RetiredPath() => new(
        "Legacy atomic ledger-account writes are retired; use the shared write executor.");
}

internal static class LedgerAccountAtomicSupport
{
    internal const int MaxNameLength = 200;
    private const int MaxCodeLength = 20;
    private const int IncomeCodeStart = 4_000;
    private const int IncomeCodeEnd = 4_999;
    private const int ExpenseCodeStart = 5_000;
    private const int ExpenseCodeEnd = 5_999;

    private static readonly HashSet<string> ProtectedSystemKeys = new(StringComparer.Ordinal)
    {
        "operating-cash",
        "undeposited-funds",
        "security-deposit-trust-cash",
        "tenant-accounts-receivable",
        "mortgage-escrow-asset",
        "tenant-security-deposits-payable",
        "mortgage-payable",
        "owner-contributions",
        "owner-distributions",
        "retained-earnings",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal static void ValidateEnvelope(CreateLedgerAccountCommand command)
    {
        ValidateEnvelope(
            command.PortfolioId,
            command.ActorUserId,
            command.ActorAuthSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.DeliveryIdempotencyKey);
    }

    internal static void ValidateEnvelope(UpdateLedgerAccountCommand command)
    {
        ValidateEnvelope(
            command.PortfolioId,
            command.ActorUserId,
            command.ActorAuthSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.DeliveryIdempotencyKey);
        if (command.AccountId <= 0)
        {
            throw new DomainValidationException("A ledger account is required.");
        }
    }

    private static void ValidateEnvelope(
        int portfolioId,
        int actorUserId,
        Guid actorAuthSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        string deliveryIdempotencyKey)
    {
        if (portfolioId <= 0
            || actorUserId <= 0
            || actorAuthSessionId == Guid.Empty
            || actorAccessContextId <= 0
            || actorAccessRevision <= 0)
        {
            throw new DomainValidationException(
                "Portfolio, actor, session, and access context are required.");
        }

        if (string.IsNullOrWhiteSpace(deliveryIdempotencyKey)
            || deliveryIdempotencyKey.Length > 200)
        {
            throw new DomainValidationException(
                "A valid idempotency key is required.");
        }
    }

    internal static void ValidateCreate(CreateLedgerAccountCommand command)
    {
        if (!Enum.IsDefined(command.AccountType))
        {
            throw new DomainValidationException("The ledger account type is invalid.");
        }

        if (!IsUserAccountType(command.AccountType))
        {
            throw new DomainValidationException(
                "User-created ledger accounts must be Income or Expense accounts.");
        }

        if (command.SystemKey is not null)
        {
            throw new DomainValidationException(
                "System keys are assigned only by the chart-of-accounts seed service.");
        }

        if (string.IsNullOrWhiteSpace(command.Name)
            || command.Name.Trim().Length > MaxNameLength)
        {
            throw new DomainValidationException(
                $"Ledger account name must be between 1 and {MaxNameLength} characters.");
        }

        if (command.Code is not null && command.Code.Trim().Length > MaxCodeLength)
        {
            throw new DomainValidationException(
                $"Ledger account code cannot exceed {MaxCodeLength} characters.");
        }

        if (command.ScheduleECategory.HasValue
            && !Enum.IsDefined(command.ScheduleECategory.Value))
        {
            throw new DomainValidationException("The Schedule E category is invalid.");
        }
    }

    internal static void ValidateUpdateType(AccountType? accountType)
    {
        if (accountType.HasValue && !Enum.IsDefined(accountType.Value))
        {
            throw new DomainValidationException("The ledger account type is invalid.");
        }
    }

    internal static void ValidateDelete(LedgerAccount account, bool hasPostedLines)
    {
        if (account.IsSystem)
        {
            throw new DomainValidationException("System ledger accounts cannot be deleted.");
        }

        if (hasPostedLines)
        {
            throw new DomainValidationException(
                "A ledger account with posted journal lines cannot be deleted.");
        }
    }

    internal static bool IsUserAccountType(AccountType accountType) =>
        accountType is AccountType.Income or AccountType.Expense;

    internal static NormalBalance NormalBalanceFor(AccountType accountType) =>
        accountType is AccountType.Asset or AccountType.Expense
            ? NormalBalance.Debit
            : NormalBalance.Credit;

    internal static bool IsProtectedSystemControl(string? systemKey) =>
        systemKey is not null && ProtectedSystemKeys.Contains(systemKey);

    internal static async Task LockAuthorityAsync(
        CreateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", command.ActorAuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.ActorAccessContextId, ct);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
    }

    internal static async Task LockAuthorityAsync(
        UpdateLedgerAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", command.ActorAuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.ActorAccessContextId, ct);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
    }

    internal static async Task EnsureAuthorizedAsync<TCommand>(
        TCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
        where TCommand : IAtomicCommandData
    {
        var authorized = command switch
        {
            CreateLedgerAccountCommand create => await HasCapabilityAsync(
                create.PortfolioId,
                create.ActorUserId,
                create.ActorAuthSessionId,
                create.ActorAccessContextId,
                create.ActorAccessRevision,
                db,
                now,
                ct),
            UpdateLedgerAccountCommand update => await HasCapabilityAsync(
                update.PortfolioId,
                update.ActorUserId,
                update.ActorAuthSessionId,
                update.ActorAccessContextId,
                update.ActorAccessRevision,
                db,
                now,
                ct),
            _ => false,
        };

        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                $"Workspace access changed or no longer grants '{CapabilityKeys.AccountDestructiveActions}'. Refresh and try again.");
        }
    }

    private static Task<bool> HasCapabilityAsync(
        int portfolioId,
        int actorUserId,
        Guid actorAuthSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == portfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.WorkspaceMembership!.AccessContextId == actorAccessContextId
            && assignment.WorkspaceMembership.PortfolioId == portfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(accessContext =>
                accessContext.Id == actorAccessContextId
                && accessContext.UserId == actorUserId
                && accessContext.PortfolioId == portfolioId
                && accessContext.AccessRevision == actorAccessRevision
                && accessContext.Status == WorkspaceAccessContextStatus.Active
                && accessContext.SuspendedAtUtc == null
                && accessContext.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == actorAuthSessionId
                && session.UserId == actorUserId
                && session.ActiveAccessContextId == actorAccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.AccountDestructiveActions
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Workspace), ct);

    internal static async Task AuthorizeReplayAsync<TCommand>(
        TCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
        where TCommand : IAtomicCommandData
    {
        switch (command)
        {
            case CreateLedgerAccountCommand create:
                ValidateEnvelope(create);
                break;
            case UpdateLedgerAccountCommand update:
                ValidateEnvelope(update);
                break;
            default:
                throw new DomainValidationException("The ledger account command is invalid.");
        }

        var now = await db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        await EnsureAuthorizedAsync(command, db, now, ct);
    }

    internal static async Task<string> ResolveCodeAsync(
        RentalCommandDbContext db,
        CreateLedgerAccountCommand command,
        CancellationToken ct)
    {
        var requested = command.Code?.Trim();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var duplicate = await db.LedgerAccounts
                .AsNoTracking()
                .AnyAsync(account =>
                    account.PortfolioId == command.PortfolioId
                    && account.Code == requested, ct);
            if (duplicate)
            {
                throw new DomainValidationException(
                    "A ledger account with this code already exists.",
                    statusCode: 409);
            }

            return requested;
        }

        var (start, end) = command.AccountType == AccountType.Income
            ? (IncomeCodeStart, IncomeCodeEnd)
            : (ExpenseCodeStart, ExpenseCodeEnd);
        var next = await db.Database.SqlQuery<int>($"""
            SELECT candidate AS "Value"
            FROM generate_series({start}, {end}) AS candidate
            WHERE NOT EXISTS (
                SELECT 1
                FROM "LedgerAccounts" AS account
                WHERE account."PortfolioId" = {command.PortfolioId}
                  AND account."Code" = candidate::text)
            ORDER BY candidate
            LIMIT 1
            """).SingleOrDefaultAsync(ct);
        if (next == 0)
        {
            throw new DomainValidationException(
                "No available ledger account code remains in this account range.",
                statusCode: 409);
        }

        return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task<int?> ValidateParentAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int? parentAccountId,
        AccountType childType,
        int? parentAccountIdToExclude,
        CancellationToken ct)
    {
        if (!parentAccountId.HasValue)
        {
            return null;
        }

        if (parentAccountId.Value <= 0)
        {
            throw new DomainValidationException("The parent ledger account is invalid.");
        }

        var parentExists = await db.LedgerAccounts
            .AsNoTracking()
            .AnyAsync(parent =>
                parent.Id == parentAccountId.Value
                && parent.Id != parentAccountIdToExclude
                && parent.PortfolioId == portfolioId
                && parent.AccountType == childType, ct);
        if (!parentExists)
        {
            throw new DomainValidationException(
                "The parent ledger account must be in the same portfolio and have the same account type.");
        }

        return parentAccountId.Value;
    }

    internal static LedgerAccountSnapshot Snapshot(LedgerAccount account, bool hasPostedLines) =>
        new(
            account.Id,
            account.PublicId,
            account.PortfolioId,
            account.Code,
            account.Name,
            account.AccountType,
            account.NormalBalance,
            account.ParentAccountId,
            account.SystemKey,
            account.ScheduleECategory,
            account.IsSystem,
            account.IsActive,
            hasPostedLines);

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    internal static OutboxMessage DataUpdate(
        int portfolioId,
        int accountId,
        string operation,
        string deliveryIdempotencyKey,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType = nameof(LedgerAccount),
            entityId = accountId,
            operation,
        }),
        IdempotencyKey = $"ledger-account-{operation}:{deliveryIdempotencyKey}",
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
    };
}
