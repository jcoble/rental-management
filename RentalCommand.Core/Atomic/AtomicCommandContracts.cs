using System.Text.Json;

namespace RentalCommand.Core.Atomic;

/// <summary>Explicit marker for immutable, service-free command DTOs.</summary>
public interface IAtomicCommandData;

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

/// <summary>One operation and its complete metadata for the shared local write executor.</summary>
public sealed record TransactionalWrite<TCommand, TResult>
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull
{
    public TransactionalWrite(
        string operationName,
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

        OperationName = operationName;
        Request = request;
        ResultContract = resultContract;
        LockPlan = lockPlan;
        ExecuteAsync = executeAsync;
        AuthorizeReplayAsync = authorizeReplayAsync;
    }

    public string OperationName { get; }
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
    AuthorizationScopeApplication,
    AuthorizationScopeTenant,
    AuthorizationScopeScanDraft,
    AuthorizationScopeRentalApplication,
    Portfolio,
    StoredFile,
    WorkOrder,
    VendorDispatchInbound,
    WorkOrderResponsibility,
    WorkspaceAccessContextWorkOrder,
    WorkOrderVendor,
    Vendor,
    AppointmentWorkOrder,
    WorkOrderAppointmentProgression,
    Possession,
    ConfirmMoveIn,
    LeaseParty,
    LeasePartyAccessGrant,
    LeasePartyAccessRevoke,
    TenantAccount,
    RentalApplication,
    TenantAccountRecurringCharge,
    AuthorizationScopeLedgerAccount,
    AuthorizationScopeTenantAccount,
    AuthorizationScopeAccountingConnection,
    AccountingConnection,
    NativeEsignLeaseManagement,
    NativeEsignRequest,
    PrepareMoveIn,
    LeaseManagement,
    LeaseAgreementDraft,
    LeaseTransfer,
    PropertyDisposition,
    BankingPrepareExchange,
    BankingConnection,
    BankingApplyConnection,
    BankingReconciliation,
    BankingRoute,
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
            [WriteLockProtocol.AuthorizationScopeApplication] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "RentalApplication"],
            [WriteLockProtocol.AuthorizationScopeTenant] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Tenant"],
            [WriteLockProtocol.AuthorizationScopeScanDraft] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft"],
            [WriteLockProtocol.AuthorizationScopeRentalApplication] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "RentalApplication"],
            [WriteLockProtocol.Portfolio] = ["Portfolio"],
            [WriteLockProtocol.StoredFile] = ["StoredFile"],
            [WriteLockProtocol.WorkOrder] = ["WorkOrder"],
            [WriteLockProtocol.VendorDispatchInbound] = ["VendorDispatch"],
            [WriteLockProtocol.WorkOrderResponsibility] = [],
            [WriteLockProtocol.WorkspaceAccessContextWorkOrder] =
                ["WorkspaceAccessContext", "WorkOrder"],
            [WriteLockProtocol.WorkOrderVendor] = ["WorkOrder", "Vendor"],
            [WriteLockProtocol.Vendor] = ["Vendor"],
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
            [WriteLockProtocol.RentalApplication] = ["RentalApplication"],
            [WriteLockProtocol.TenantAccountRecurringCharge] =
                ["TenantAccount", "RecurringTenantCharge"],
            [WriteLockProtocol.AuthorizationScopeLedgerAccount] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount"],
            [WriteLockProtocol.AuthorizationScopeTenantAccount] =
                ["AuthSession", "WorkspaceAccessContext", "TenantAccount"],
            [WriteLockProtocol.AuthorizationScopeAccountingConnection] =
                ["AuthSession", "WorkspaceAccessContext", "AccountingConnection"],
            [WriteLockProtocol.AccountingConnection] = ["AccountingConnection"],
            [WriteLockProtocol.NativeEsignLeaseManagement] = ["LeaseManagement"],
            [WriteLockProtocol.NativeEsignRequest] = ["SignatureRequest"],
            [WriteLockProtocol.PrepareMoveIn] = ["Unit"],
            [WriteLockProtocol.LeaseManagement] = ["LeaseManagement"],
            [WriteLockProtocol.LeaseAgreementDraft] =
                ["AuthSession", "WorkspaceAccessContext", "LeaseManagement"],
            [WriteLockProtocol.LeaseTransfer] = ["Unit", "Unit", "LeaseManagement"],
            [WriteLockProtocol.PropertyDisposition] = ["Property"],
            [WriteLockProtocol.BankingPrepareExchange] =
                ["AuthSession", "WorkspaceAccessContext", "BankConnection"],
            [WriteLockProtocol.BankingConnection] = ["BankConnection"],
            [WriteLockProtocol.BankingApplyConnection] = ["BankConnection"],
            [WriteLockProtocol.BankingReconciliation] =
                ["AuthSession", "WorkspaceAccessContext"],
            [WriteLockProtocol.BankingRoute] =
                ["AuthSession", "WorkspaceAccessContext", "BankTransaction"],
        };

    public static WriteLockPlan None { get; } = new();

    private WriteLockPlan() => Locks = [];

    public WriteLockPlan(WriteLockProtocol protocol, params object[] lockIds)
    {
        ArgumentNullException.ThrowIfNull(lockIds);
        var namespaces = ProtocolNamespaces[protocol];
        if (lockIds.Length != namespaces.Length)
        {
            throw new ArgumentException(
                $"Write lock protocol '{protocol}' requires {namespaces.Length} lock ids.",
                nameof(lockIds));
        }

        var locks = namespaces.Zip(lockIds, static (lockNamespace, lockId) => lockId switch
        {
            int integerId => WriteLock.For(lockNamespace, integerId),
            Guid guidId => WriteLock.For(lockNamespace, guidId),
            _ => throw new ArgumentException(
                "A lock identifier must be an integer or GUID.", nameof(lockIds)),
        }).ToArray();

        if (locks.Distinct().Count() != locks.Length)
        {
            throw new ArgumentException(
                "A lock plan cannot acquire the same lock twice.", nameof(lockIds));
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
            WriteLockProtocol.VendorDispatchInbound => ["WorkOrder"],
            WriteLockProtocol.WorkOrderResponsibility => ["WorkspaceAccessContext", "WorkOrder"],
            WriteLockProtocol.LeasePartyAccessGrant =>
                ["TenantIdentityEmail", "WorkspaceAccessContext"],
            WriteLockProtocol.LeasePartyAccessRevoke => ["WorkspaceAccessContext"],
            WriteLockProtocol.NativeEsignRequest => ["LeaseManagement"],
            WriteLockProtocol.PrepareMoveIn => ["RentalApplication"],
            WriteLockProtocol.BankingApplyConnection => ["BankConnection"],
            WriteLockProtocol.BankingReconciliation => ["BankTransaction"],
            WriteLockProtocol.BankingRoute => ["Property"],
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
