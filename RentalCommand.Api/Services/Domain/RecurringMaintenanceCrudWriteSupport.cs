using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

internal enum RecurringMaintenanceWriteOperation { Create, Update, SetActive, Delete }

internal sealed record RecurringMaintenanceWriteRequest(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    RecurringMaintenanceWriteOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey)
    : TransactionalWriteDefaults.IAuthorizationScopedRequest;

internal sealed record RecurringMaintenanceWriteResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null);

internal static class RecurringMaintenanceCrudWriteSupport
{
    public const string ResultContract = "recurring-maintenance.mutation.v1";

    public static RecurringMaintenanceWriteRequest Request<TRequest>(
        WorkspaceReadScope scope,
        RecurringMaintenanceWriteOperation operation,
        int entityId,
        TRequest request,
        string deliveryIdempotencyKey) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            operation,
            entityId,
            JsonSerializer.Serialize(request),
            deliveryIdempotencyKey);

    public static string IdempotencyKey(RecurringMaintenanceWriteRequest request) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(request.DeliveryIdempotencyKey)));

    public static TransactionalWrite<RecurringMaintenanceWriteRequest, RecurringMaintenanceWriteResult> Write(
        RecurringMaintenanceWriteRequest request,
        Func<RecurringMaintenanceWriteRequest, IAtomicCommandContext, CancellationToken,
            Task<RecurringMaintenanceWriteResult>> executeAsync,
        Func<RecurringMaintenanceWriteRequest, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync) =>
        new(
            $"recurring-maintenance.{request.Operation.ToString().ToLowerInvariant()}",
            WriteIdempotencyPolicy.Required,
            request,
            ResultContract,
            new WriteLockPlan(
                WriteLockProtocol.RecurringMaintenance,
                WriteLock.For("AuthSession", request.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", request.AccessContextId),
                WriteLock.For("Portfolio", request.PortfolioId)),
            executeAsync,
            authorizeReplayAsync);

    public static async Task<DateTime> BeginExecutionAsync(
        RecurringMaintenanceWriteRequest request,
        IAtomicCommandContext context,
        Func<DateTime, CancellationToken, Task> authorizeAsync,
        CancellationToken ct)
    {
        Validate(request);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        await authorizeAsync(now, ct);
        return now;
    }

    public static async Task AuthorizeReplayAsync(
        RecurringMaintenanceWriteRequest request,
        IAtomicCommandContext context,
        Func<DateTime, CancellationToken, Task> authorizeAsync,
        CancellationToken ct)
    {
        Validate(request);
        await authorizeAsync(await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    public static T Read<T>(RecurringMaintenanceWriteRequest request) where T : class =>
        JsonSerializer.Deserialize<T>(request.RequestJson)
        ?? throw new ArgumentException("The recurring maintenance update is invalid.");

    private static void Validate(RecurringMaintenanceWriteRequest request)
    {
        TransactionalWriteDefaults.ValidateAuthorizationScope(request);
        if ((request.Operation != RecurringMaintenanceWriteOperation.Create && request.EntityId <= 0)
            || string.IsNullOrWhiteSpace(request.RequestJson))
        {
            throw new ArgumentException("Recurring maintenance atomic command is invalid.");
        }
    }
}
