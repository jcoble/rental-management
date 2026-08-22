using System.Text.Json;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Atomic;

/// <summary>Shared construction defaults for ordinary authorization-scoped writes.</summary>
public static class TransactionalWriteDefaults
{
    public interface IAuthorizationScopedRequest : IAtomicCommandData
    {
        int PortfolioId { get; }
        int ActorUserId { get; }
        [AtomicFingerprintIgnore] Guid AuthSessionId { get; }
        [AtomicFingerprintIgnore] int AccessContextId { get; }
        [AtomicFingerprintIgnore] long ExpectedAccessRevision { get; }
        [AtomicFingerprintIgnore] string DeliveryIdempotencyKey { get; }
    }

    public sealed record AuthorizationScopedRequest<TDomain>(
        int PortfolioId, int ActorUserId,
        Guid AuthSessionId, int AccessContextId, long ExpectedAccessRevision,
        TDomain Domain, int EntityId, string ResourceKey, string RequestJson,
        string DeliveryIdempotencyKey,
        [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc = default)
        : IAuthorizationScopedRequest where TDomain : struct, Enum;

    public static AuthorizationScopedRequest<TDomain> Request<TDomain, TRequest>(
        WorkspaceReadScope scope, TDomain domain, int entityId, string resourceKey,
        string operationKey, TRequest request, DateTime businessNowUtc = default)
        where TDomain : struct, Enum =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, entityId, resourceKey, JsonSerializer.Serialize(request),
            operationKey, businessNowUtc);

    public static TransactionalWrite<TCommand, TResult> AuthorizationScoped<TCommand, TResult>(
        string operationName, TCommand request, string resultContract,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync,
        WriteLock? entityLock = null)
        where TCommand : notnull, IAuthorizationScopedRequest
        where TResult : notnull =>
        new(operationName, WriteIdempotencyPolicy.Required, request, resultContract,
            AuthorizationLockPlan(request, entityLock),
            executeAsync, authorizeReplayAsync);

    private static WriteLockPlan AuthorizationLockPlan(
        IAuthorizationScopedRequest request, WriteLock? entityLock) => entityLock is null
        ? new WriteLockPlan(WriteLockProtocol.AuthorizationScope,
            WriteLock.For("AuthSession", request.AuthSessionId),
            WriteLock.For("WorkspaceAccessContext", request.AccessContextId),
            WriteLock.For("Portfolio", request.PortfolioId))
        : new WriteLockPlan(entityLock.LockNamespace switch
            {
                "OwnerEntity" => WriteLockProtocol.AuthorizationScopeOwnerEntity,
                "Vendor" => WriteLockProtocol.AuthorizationScopeVendor,
                "Property" => WriteLockProtocol.AuthorizationScopeProperty,
                "Unit" => WriteLockProtocol.AuthorizationScopeUnit,
                "Tenant" => WriteLockProtocol.AuthorizationScopeTenant,
                _ => throw new ArgumentException(
                    "That authorization-scoped entity lock is not supported.", nameof(entityLock)),
            },
            WriteLock.For("AuthSession", request.AuthSessionId),
            WriteLock.For("WorkspaceAccessContext", request.AccessContextId),
            WriteLock.For("Portfolio", request.PortfolioId),
            entityLock);

    public static void ValidateAuthorizationScope(IAuthorizationScopedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PortfolioId <= 0 || request.ActorUserId <= 0
            || request.AuthSessionId == Guid.Empty || request.AccessContextId <= 0
            || request.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(request.DeliveryIdempotencyKey)
            || request.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Workspace, user, access details, and delivery details are required.");
        }
    }

    public static AtomicSemanticAudit Audit(
        IAuthorizationScopedRequest request, string entityType, AuditLogOperation operation,
        string reason, int entityId) =>
        new(request.PortfolioId, entityType, entityId, operation,
            UserId: request.ActorUserId, ChangeReason: reason);

    public static void StageDataUpdate(
        IAuthorizationScopedRequest request, IAtomicCommandContext context,
        string entityType, int entityId, DateTime now,
        string suffix = "data-update", bool deleted = false) =>
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
                { entityType, entityId, operation = deleted ? "delete" : "update", data = new { } }),
            IdempotencyKey = $"{request.DeliveryIdempotencyKey}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
}
