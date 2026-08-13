using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

internal enum NotificationCrudOperation
{
    MyAlerts = 0,
    DeviceRegister = 8,
    DeviceUnregister = 9,
    MarkRead = 12,
    MarkAllRead = 13,
}

/// <summary>
/// Immutable application input for the preference, device, and notification read-state writes.
/// The persisted fingerprint shape intentionally matches the retired mega-handler envelope so
/// receipts committed before the cutover remain replayable through the shared executor.
/// </summary>
internal sealed record NotificationCrudWriteRequest(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    NotificationCrudOperation Domain,
    int EntityId,
    string ResourceKey,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore]
    DateTime BusinessNowUtc = default) : IAtomicCommandData;

internal static class NotificationCrudWriteSupport
{
    public const string ResultContract = "rental.notification-mutation.v1";

    public static NotificationCrudWriteRequest Request<TRequest>(
        WorkspaceReadScope scope,
        NotificationCrudOperation operation,
        int entityId,
        string resourceKey,
        string operationKey,
        TRequest request,
        DateTime businessNowUtc = default) =>
        new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            operation,
            entityId,
            resourceKey,
            JsonSerializer.Serialize(request),
            operationKey,
            businessNowUtc);

    public static string OperationName(NotificationCrudWriteRequest request) =>
        $"rental.notification.{request.Domain.ToString().ToLowerInvariant()}";

    public static string IdempotencyKey(NotificationCrudWriteRequest request) =>
        $"{request.PortfolioId}:{request.AccessContextId}:{request.Domain}:" +
        $"{request.EntityId}:{request.ResourceKey}:{request.DeliveryIdempotencyKey}";

    public static WriteLockPlan LockPlan(NotificationCrudWriteRequest request) =>
        new(
            WriteLockProtocol.AuthorizationScope,
            WriteLock.For("AuthSession", request.AuthSessionId),
            WriteLock.For("WorkspaceAccessContext", request.AccessContextId),
            WriteLock.For("Portfolio", request.PortfolioId));

    public static async Task<DateTime> BeginExecutionAsync(
        NotificationCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(request);
        var databaseNow = await context.ReadDatabaseClockUtcAsync(ct);
        var mutationNow = BusinessNow(request, databaseNow);
        context.UseDatabaseWallClockForAudit(mutationNow);
        await AuthorizeAsync(request, db, databaseNow, ct);
        return mutationNow;
    }

    public static async Task AuthorizeReplayAsync(
        NotificationCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(request);
        await AuthorizeAsync(request, db, await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    public static AtomicSemanticAudit Audit(
        NotificationCrudWriteRequest request,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int entityId) =>
        new(
            request.PortfolioId,
            entityType,
            entityId,
            operation,
            UserId: request.ActorUserId,
            ChangeReason: reason);

    public static void StageDataUpdate(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        string entityType,
        int entityId,
        DateTime now) =>
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"{request.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    public static AtomicNotificationMutationResult Missing() => new(false, false, 0, 0);

    public static AtomicNotificationMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, 1, responseJson);

    private static async Task AuthorizeAsync(
        NotificationCrudWriteRequest request,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var authorized = await db.WorkspaceAccessContexts.AsNoTracking().AnyAsync(context =>
            context.Id == request.AccessContextId
            && context.UserId == request.ActorUserId
            && context.PortfolioId == request.PortfolioId
            && context.AccessRevision == request.ExpectedAccessRevision
            && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null
            && context.RevokedAtUtc == null
            && db.AuthSessions.Any(session =>
                session.Id == request.AuthSessionId
                && session.UserId == request.ActorUserId
                && session.ActiveAccessContextId == request.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now), ct);

        if (!authorized)
        {
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
        }
    }

    private static DateTime BusinessNow(NotificationCrudWriteRequest request, DateTime databaseNow)
    {
        if (request.BusinessNowUtc == default)
        {
            return databaseNow;
        }

        if (request.BusinessNowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Notification time must use UTC.");
        }

        return request.BusinessNowUtc;
    }

    private static void Validate(NotificationCrudWriteRequest request)
    {
        if (request.PortfolioId <= 0
            || request.ActorUserId <= 0
            || request.AuthSessionId == Guid.Empty
            || request.AccessContextId <= 0
            || request.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(request.RequestJson)
            || string.IsNullOrWhiteSpace(request.DeliveryIdempotencyKey)
            || request.DeliveryIdempotencyKey.Length > 128
            || request.Domain == NotificationCrudOperation.MarkRead && request.EntityId <= 0)
        {
            throw new ArgumentException(
                "Workspace, user, access details, and delivery details are required.");
        }
    }
}
