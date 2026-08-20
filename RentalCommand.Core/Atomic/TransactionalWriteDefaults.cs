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
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAuthorizationScopedRequest
        where TResult : notnull =>
        new(operationName, WriteIdempotencyPolicy.Required, request, resultContract,
            new WriteLockPlan(WriteLockProtocol.AuthorizationScope,
                WriteLock.For("AuthSession", request.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", request.AccessContextId),
                WriteLock.For("Portfolio", request.PortfolioId)),
            executeAsync, authorizeReplayAsync);

    public static string OperationName<TDomain>(string prefix, TDomain domain)
        where TDomain : struct, Enum => $"{prefix}.{domain.ToString().ToLowerInvariant()}";

    public static string IdempotencyKey<TDomain>(
        IAuthorizationScopedRequest request, TDomain domain, int targetId, string targetKey)
        where TDomain : struct, Enum =>
        $"{request.PortfolioId}:{request.AccessContextId}:{domain}:" +
        $"{targetId}:{targetKey}:{request.DeliveryIdempotencyKey}";

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

    public static async Task<DateTime> BeginExecutionAsync<TDomain>(
        AuthorizationScopedRequest<TDomain> request, IAtomicCommandContext context,
        Action<AuthorizationScopedRequest<TDomain>> validateRequest,
        Func<DateTime, CancellationToken, Task> authorizeAsync,
        string invalidBusinessTimeMessage, CancellationToken ct)
        where TDomain : struct, Enum
    {
        ValidateAuthorizationScope(request);
        validateRequest(request);
        var databaseNow = await context.ReadDatabaseClockUtcAsync(ct);
        var mutationNow = request.BusinessNowUtc == default ? databaseNow
            : request.BusinessNowUtc.Kind == DateTimeKind.Utc ? request.BusinessNowUtc
            : throw new ArgumentException(invalidBusinessTimeMessage);
        context.UseDatabaseWallClockForAudit(mutationNow);
        await authorizeAsync(databaseNow, ct);
        return mutationNow;
    }

    public static async Task AuthorizeReplayAsync<TDomain>(
        AuthorizationScopedRequest<TDomain> request, IAtomicCommandContext context,
        Action<AuthorizationScopedRequest<TDomain>> validateRequest,
        Func<DateTime, CancellationToken, Task> authorizeAsync, CancellationToken ct)
        where TDomain : struct, Enum
    {
        ValidateAuthorizationScope(request);
        validateRequest(request);
        await authorizeAsync(await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    public static AtomicSemanticAudit Audit(
        IAuthorizationScopedRequest request, string entityType, AuditLogOperation operation,
        string reason, int entityId) =>
        new(request.PortfolioId, entityType, entityId, operation,
            UserId: request.ActorUserId, ChangeReason: reason);

    public static void StageDataUpdate(
        IAuthorizationScopedRequest request, IAtomicCommandContext context,
        string entityType, int entityId, DateTime now) =>
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
                { entityType, entityId, operation = "update", data = new { } }),
            IdempotencyKey = $"{request.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
}
