using System.Text.Json;

namespace RentalCommand.Core.Atomic;

/// <summary>Explicit marker for immutable, service-free command DTOs.</summary>
public interface IAtomicCommandData;

/// <summary>
/// Receipt-backed transaction boundary over the caller's scoped database context.
/// </summary>
public interface IAtomicUnitOfWork
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

/// <summary>
/// Shared front door for one local, receipt-backed write. The supplied operation runs on the
/// executor-owned scoped context and transaction.
/// </summary>
public interface IWriteExecutor
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string idempotencyKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

public enum WriteIdempotencyPolicy
{
    Required,
}

public enum WriteEntryPointKind
{
    LegacyAtomic,
    Transactional,
}

/// <summary>
/// Marks a write entry point as either still using the compatibility shell or using the shared
/// executor. The single-use attribute makes the two states mutually exclusive.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class WriteEntryPointAttribute(WriteEntryPointKind kind) : Attribute
{
    public WriteEntryPointKind Kind { get; } = kind;
}

/// <summary>One operation and its complete metadata for the shared local write executor.</summary>
public sealed record TransactionalWrite<TCommand, TResult>
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull
{
    public TransactionalWrite(
        string operationName,
        WriteIdempotencyPolicy idempotencyPolicy,
        TCommand request,
        string resultContract,
        WriteLockPlan lockPlan,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultContract);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lockPlan);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(authorizeReplayAsync);
        if (idempotencyPolicy != WriteIdempotencyPolicy.Required)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyPolicy));
        }

        OperationName = operationName;
        IdempotencyPolicy = idempotencyPolicy;
        Request = request;
        ResultContract = resultContract;
        LockPlan = lockPlan;
        ExecuteAsync = executeAsync;
        AuthorizeReplayAsync = authorizeReplayAsync;
    }

    public string OperationName { get; }
    public WriteIdempotencyPolicy IdempotencyPolicy { get; }
    public TCommand Request { get; }
    public string ResultContract { get; }
    public WriteLockPlan LockPlan { get; }
    public Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> ExecuteAsync { get; }
    public Func<TCommand, IAtomicCommandContext, CancellationToken, Task> AuthorizeReplayAsync { get; }
}

/// <summary>
/// Legacy-compatible lock sequences explicitly admitted onto the shared executor. New command
/// families must add their observed legacy protocol here before migration; there is no safe global
/// namespace rank across all existing shells.
/// </summary>
public enum WriteLockProtocol
{
    AuthorizationScope,
    AuthorizationScopeOwnerEntity,
    AuthorizationScopeVendor,
    AuthorizationScopeProperty,
    AuthorizationScopeUnit,
    AuthorizationScopeTenant,
    AuthorizationScopeScanDraft,
    Portfolio,
    StoredFile,
    WorkOrder,
    AppointmentWorkOrder,
    WorkOrderAppointmentProgression,
    Possession,
    ConfirmMoveIn,
    LeaseParty,
    LeasePartyAccessGrant,
    LeasePartyAccessRevoke,
    TenantAccount,
}

public sealed class WriteLockPlan
{
    private static readonly IReadOnlyDictionary<WriteLockProtocol, string[]> ProtocolNamespaces =
        new Dictionary<WriteLockProtocol, string[]>
        {
            [WriteLockProtocol.AuthorizationScope] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio"],
            [WriteLockProtocol.AuthorizationScopeOwnerEntity] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "OwnerEntity"],
            [WriteLockProtocol.AuthorizationScopeVendor] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Vendor"],
            [WriteLockProtocol.AuthorizationScopeProperty] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Property"],
            [WriteLockProtocol.AuthorizationScopeUnit] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Unit"],
            [WriteLockProtocol.AuthorizationScopeTenant] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Tenant"],
            [WriteLockProtocol.AuthorizationScopeScanDraft] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft"],
            [WriteLockProtocol.Portfolio] = ["Portfolio"],
            [WriteLockProtocol.StoredFile] = ["StoredFile"],
            [WriteLockProtocol.WorkOrder] = ["WorkOrder"],
            // The work-order tail is resolved by the rule only after this appointment lock is held.
            [WriteLockProtocol.AppointmentWorkOrder] = ["Appointment"],
            // Both classes are resolved and acquired by the rule in the published progression order.
            [WriteLockProtocol.WorkOrderAppointmentProgression] = [],
            [WriteLockProtocol.Possession] =
                ["Unit", "LeaseManagement"],
            [WriteLockProtocol.ConfirmMoveIn] =
                ["Unit", "LeaseManagement", "TenantAccount"],
            [WriteLockProtocol.LeaseParty] = ["LeaseManagement"],
            [WriteLockProtocol.LeasePartyAccessGrant] = ["LeaseManagement"],
            [WriteLockProtocol.LeasePartyAccessRevoke] = ["LeaseManagement"],
            [WriteLockProtocol.TenantAccount] = ["TenantAccount"],
        };

    public static WriteLockPlan None { get; } = new();

    private WriteLockPlan() => Locks = [];

    public WriteLockPlan(WriteLockProtocol protocol, params WriteLock[] locks)
    {
        ArgumentNullException.ThrowIfNull(locks);
        if (locks.Any(item => item is null))
        {
            throw new ArgumentException("A lock plan cannot contain a null lock.", nameof(locks));
        }

        var expectedNamespaces = ProtocolNamespaces[protocol];
        if (!locks.Select(item => item.LockNamespace).SequenceEqual(expectedNamespaces))
        {
            throw new ArgumentException(
                $"Write locks for protocol '{protocol}' must be exactly: " +
                string.Join(" then ", expectedNamespaces) + ".",
                nameof(locks));
        }

        if (locks.Distinct().Count() != locks.Length)
        {
            throw new ArgumentException("A lock plan cannot acquire the same lock twice.", nameof(locks));
        }

        Protocol = protocol;
        Locks = locks;
    }

    public WriteLockProtocol? Protocol { get; }
    public IReadOnlyList<WriteLock> Locks { get; }
    public IReadOnlyList<string> DeferredLockNamespaces =>
        Protocol switch
        {
            WriteLockProtocol.AppointmentWorkOrder => ["WorkOrder"],
            WriteLockProtocol.WorkOrderAppointmentProgression => ["Appointment", "WorkOrder"],
            WriteLockProtocol.LeasePartyAccessGrant =>
                ["TenantIdentityEmail", "WorkspaceAccessContext"],
            WriteLockProtocol.LeasePartyAccessRevoke => ["WorkspaceAccessContext"],
            _ => [],
        };
}

public sealed record WriteLock
{
    private WriteLock(string lockNamespace, int? integerId, Guid? guidId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockNamespace);
        if (integerId is not null && integerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(integerId));
        }
        if (guidId is not null && guidId == Guid.Empty)
        {
            throw new ArgumentException("A lock identifier cannot be empty.", nameof(guidId));
        }

        LockNamespace = lockNamespace;
        IntegerId = integerId;
        GuidId = guidId;
    }

    public string LockNamespace { get; }
    public int? IntegerId { get; }
    public Guid? GuidId { get; }
    internal string AggregateKey => IntegerId?.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)
        ?? GuidId!.Value.ToString("N");

    public static WriteLock For(string lockNamespace, int aggregateId) =>
        new(lockNamespace, aggregateId, null);

    public static WriteLock For(string lockNamespace, Guid aggregateId) =>
        new(lockNamespace, null, aggregateId);

    public Task AcquireAsync(IAtomicCommandContext context, CancellationToken ct = default) =>
        IntegerId is int integerId
            ? context.AcquireLockAsync(LockNamespace, integerId, ct)
            : context.AcquireLockAsync(LockNamespace, GuidId!.Value, ct);
}

/// <summary>Application command code executed on the same scoped context as the transaction owner.</summary>
public interface IAtomicCommandHandler<in TCommand, TResult>
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull
{
    Task<TResult> HandleAsync(
        TCommand command,
        IAtomicCommandContext context,
        CancellationToken ct);

    Task AuthorizeReplayAsync(
        TCommand command,
        IAtomicCommandContext context,
        CancellationToken ct);
}

public sealed record AtomicSqlMutationTarget(
    string TableName,
    AtomicSqlMutationOperation Operation);

public enum AtomicSqlMutationOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>Explicit JSON receipt codec with a stable, versioned contract name.</summary>
public sealed class AtomicJsonResultCodec<TResult>
    where TResult : notnull
{
    public AtomicJsonResultCodec(string contractName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        if (contractName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractName),
                "Result contract cannot exceed 200 characters.");
        }

        ContractName = contractName;
    }

    public string ContractName { get; }
    public string Serialize(TResult result) => JsonSerializer.Serialize(result);

    public TResult Deserialize(string json) =>
        JsonSerializer.Deserialize<TResult>(json)
        ?? throw new InvalidOperationException(
            $"Receipt result for contract '{ContractName}' deserialized to null.");
}

public enum AtomicCommandDisposition
{
    Executed,
    Replayed,
}

public sealed record AtomicCommandOutcome<TResult>(
    TResult Value,
    AtomicCommandDisposition Disposition,
    Guid AttemptId)
    where TResult : notnull;

public sealed class AtomicReceiptInvariantException : InvalidOperationException
{
    public AtomicReceiptInvariantException(string message) : base(message) { }
}

/// <summary>
/// A caller reused a command idempotency key for a different business payload. This is a stable
/// request conflict, not a receipt corruption or server failure.
/// </summary>
public sealed class AtomicIdempotencyConflictException : InvalidOperationException
{
    public AtomicIdempotencyConflictException()
        : base("The Idempotency-Key has already been used for a different request payload.") { }
}

public sealed class AtomicArchitectureException : InvalidOperationException
{
    public AtomicArchitectureException(string message) : base(message) { }
}
