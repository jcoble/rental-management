using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

internal sealed record CoreCrudWriteRequest(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    AtomicCoreCrudMutationDomain Domain,
    AtomicCoreCrudMutationOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore] DateTime? CreatedAtUtc = null,
    [property: AtomicFingerprintIgnore] DateTime? ChangedAtUtc = null)
    : TransactionalWriteDefaults.IAuthorizationScopedRequest;

internal static class CoreCrudWriteSupport
{
    public const string ResultContract = "rental.core-crud-mutation.v1";

    public static CoreCrudWriteRequest Request<TRequest>(
        WorkspaceReadScope scope,
        AtomicCoreCrudMutationDomain domain,
        AtomicCoreCrudMutationOperation operation,
        int entityId,
        string operationKey,
        TRequest request,
        DateTime? createdAtUtc = null,
        DateTime? changedAtUtc = null) => new(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, operation, entityId,
            JsonSerializer.Serialize(request), operationKey, createdAtUtc, changedAtUtc);

    public static string IdempotencyKey(CoreCrudWriteRequest request) =>
        $"{request.PortfolioId}:{request.AccessContextId}:{request.Domain}:{request.Operation}:" +
        $"{request.EntityId}:{request.DeliveryIdempotencyKey}";

    public static TransactionalWrite<CoreCrudWriteRequest, AtomicCoreCrudMutationResult> Write(
        CoreCrudWriteRequest request,
        Func<CoreCrudWriteRequest, IAtomicCommandContext, CancellationToken,
            Task<AtomicCoreCrudMutationResult>> executeAsync,
        Func<CoreCrudWriteRequest, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync) =>
        TransactionalWriteDefaults.AuthorizationScoped(
            $"rental.{request.Domain.ToString().ToLowerInvariant()}." +
            request.Operation.ToString().ToLowerInvariant(),
            request,
            ResultContract,
            executeAsync,
            authorizeReplayAsync,
            request.EntityId > 0
                ? WriteLock.For(request.Domain.ToString(), request.EntityId)
                : null);

    public static async Task<DateTime> BeginExecutionAsync(
        CoreCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct,
        bool authorize = true)
    {
        Validate(request);
        var databaseNow = await context.ReadDatabaseClockUtcAsync(ct);
        var mutationNow = request.Operation == AtomicCoreCrudMutationOperation.Create
            ? request.CreatedAtUtc ?? databaseNow
            : request.ChangedAtUtc ?? databaseNow;
        if (authorize)
        {
            await AuthorizeAsync(request, db, databaseNow, ct);
        }
        return mutationNow;
    }

    public static Task AuthorizeExecutionAsync(
        CoreCrudWriteRequest request,
        RentalCommandDbContext db,
        DateTime databaseNow,
        CancellationToken ct) =>
        AuthorizeAsync(request, db, databaseNow, ct);

    public static async Task AuthorizeReplayAsync(
        CoreCrudWriteRequest request,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(request);
        await AuthorizeAsync(
            request, db, await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    public static T Read<T>(CoreCrudWriteRequest request) where T : class =>
        JsonSerializer.Deserialize<T>(request.RequestJson)
        ?? throw new ArgumentException("The record update is invalid.");

    private static async Task AuthorizeAsync(
        CoreCrudWriteRequest request,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var assignments = db.AuthorizedAssignmentsForScope(
            Scope(request),
            [request.Domain == AtomicCoreCrudMutationDomain.Vendor
                ? CapabilityKeys.WorkManage
                : CapabilityKeys.RentalsManage],
            CapabilityAuthorizationTargetKind.Property,
            now);

        var authorized = request switch
        {
            { Domain: AtomicCoreCrudMutationDomain.OwnerEntity,
                Operation: AtomicCoreCrudMutationOperation.Create } =>
                await assignments.AnyAsync(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct),
            { Domain: AtomicCoreCrudMutationDomain.OwnerEntity } =>
                await db.OwnerEntities.AsNoTracking().AnyAsync(owner =>
                    owner.Id == request.EntityId && owner.PortfolioId == request.PortfolioId
                    && (assignments.Any(assignment =>
                            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties)
                        || (db.PropertyOwnerships.Any(ownership =>
                                ownership.PortfolioId == request.PortfolioId
                                && ownership.OwnerEntityId == owner.Id
                                && ownership.EffectiveFromUtc <= now
                                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                                && ownership.Property != null
                                && ownership.Property.DeletedAt == null)
                            && !db.PropertyOwnerships.Any(ownership =>
                                ownership.PortfolioId == request.PortfolioId
                                && ownership.OwnerEntityId == owner.Id
                                && ownership.EffectiveFromUtc <= now
                                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                                && ownership.Property != null
                                && ownership.Property.DeletedAt == null
                                && !assignments.Any(assignment =>
                                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                    && assignment.SelectedProperties.Any(selected =>
                                        selected.PortfolioId == request.PortfolioId
                                        && selected.PropertyId == ownership.PropertyId))))), ct),
            { Domain: AtomicCoreCrudMutationDomain.Vendor,
                Operation: AtomicCoreCrudMutationOperation.Create } =>
                await db.Properties.AsNoTracking()
                    .WhereAuthorizedForScope(
                        db, Scope(request), [CapabilityKeys.WorkManage], now)
                    .AnyAsync(ct),
            { Domain: AtomicCoreCrudMutationDomain.Vendor } =>
                await assignments.AnyAsync(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct),
            _ => false,
        };

        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed. Refresh and try again.");
        }
    }

    private static WorkspaceReadScope Scope(CoreCrudWriteRequest request) => new(
        request.PortfolioId,
        request.ActorUserId,
        request.AuthSessionId,
        request.AccessContextId,
        request.ExpectedAccessRevision);

    private static void Validate(CoreCrudWriteRequest request)
    {
        TransactionalWriteDefaults.ValidateAuthorizationScope(request);
        if (string.IsNullOrWhiteSpace(request.RequestJson)
            || request.Domain is not AtomicCoreCrudMutationDomain.OwnerEntity
                and not AtomicCoreCrudMutationDomain.Vendor
            || request.Operation is not AtomicCoreCrudMutationOperation.Create
                and not AtomicCoreCrudMutationOperation.Update
                and not AtomicCoreCrudMutationOperation.Delete
            || (request.Operation != AtomicCoreCrudMutationOperation.Create
                && request.EntityId <= 0))
        {
            throw new ArgumentException(
                "Portfolio, actor, access revision, operation, and delivery identifiers are required.");
        }
    }
}
