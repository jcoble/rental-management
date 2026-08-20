using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using NotificationCrudWriteRequest = RentalCommand.Core.Atomic.TransactionalWriteDefaults.AuthorizationScopedRequest<RentalCommand.Api.Services.Domain.NotificationCrudOperation>;

namespace RentalCommand.Api.Services.Domain;

internal enum NotificationCrudOperation
{
    MyAlerts = 0,
    DeviceRegister = 8,
    DeviceUnregister = 9,
    MarkRead = 12,
    MarkAllRead = 13,
}

internal static class NotificationCrudWriteSupport
{
    public const string ResultContract = "rental.notification-mutation.v1";

    public static string IdempotencyKey(NotificationCrudWriteRequest request) =>
        TransactionalWriteDefaults.IdempotencyKey(
            request, request.Domain, request.EntityId, request.ResourceKey);

    public static TransactionalWrite<NotificationCrudWriteRequest, AtomicNotificationMutationResult> Write(
        NotificationCrudWriteRequest request,
        Func<NotificationCrudWriteRequest, IAtomicCommandContext, CancellationToken,
            Task<AtomicNotificationMutationResult>> executeAsync,
        Func<NotificationCrudWriteRequest, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync) =>
        TransactionalWriteDefaults.AuthorizationScoped(
            TransactionalWriteDefaults.OperationName("rental.notification", request.Domain),
            request,
            ResultContract,
            executeAsync,
            authorizeReplayAsync);

    public static Task<DateTime> BeginExecutionAsync(
        NotificationCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        TransactionalWriteDefaults.BeginExecutionAsync(
            request,
            context,
            Validate,
            (databaseNow, token) => AuthorizeAsync(request, db, databaseNow, token),
            "Notification time must use UTC.",
            ct);

    public static Task AuthorizeReplayAsync(
        NotificationCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        TransactionalWriteDefaults.AuthorizeReplayAsync(
            request,
            context,
            Validate,
            (databaseNow, token) => AuthorizeAsync(request, db, databaseNow, token),
            ct);

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

    private static void Validate(NotificationCrudWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestJson)
            || request.Domain == NotificationCrudOperation.MarkRead && request.EntityId <= 0)
        {
            throw new ArgumentException(
                "Workspace, user, access details, and delivery details are required.");
        }
    }
}
