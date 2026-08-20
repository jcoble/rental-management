using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Policies;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Policies;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicCoreCrudMutationDomain { Property, OwnerEntity, Tenant, Vendor }
public enum AtomicCoreCrudMutationOperation { Create, Update, Delete, Setup }

public sealed record AtomicCoreCrudMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null);

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
            || request is { Operation: AtomicCoreCrudMutationOperation.Setup, EntityId: 0 }
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
            || request.Domain is not AtomicCoreCrudMutationDomain.Property
                and not AtomicCoreCrudMutationDomain.OwnerEntity
                and not AtomicCoreCrudMutationDomain.Tenant
                and not AtomicCoreCrudMutationDomain.Vendor
            || request.Operation is not AtomicCoreCrudMutationOperation.Create
                and not AtomicCoreCrudMutationOperation.Update
                and not AtomicCoreCrudMutationOperation.Delete
                and not AtomicCoreCrudMutationOperation.Setup
            || (request.Operation is not AtomicCoreCrudMutationOperation.Create
                    and not AtomicCoreCrudMutationOperation.Setup
                && request.EntityId <= 0))
        {
            throw new ArgumentException(
                "Portfolio, actor, access revision, operation, and delivery identifiers are required.");
        }
    }
}

internal sealed record RentalCrudWriteRequest(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    AtomicRentalMutationDomain Domain,
    AtomicRentalMutationOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey)
    : TransactionalWriteDefaults.IAuthorizationScopedRequest;

internal static class RentalCrudWriteSupport
{
    private static readonly JsonSerializerOptions SparseUpdateJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string ResultContract = "rental.scoped-mutation.v1";

    public static RentalCrudWriteRequest Request<TRequest>(
        WorkspaceReadScope scope,
        AtomicRentalMutationOperation operation,
        int entityId,
        string operationKey,
        TRequest request) => new(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, AtomicRentalMutationDomain.Unit, operation, entityId,
            JsonSerializer.Serialize(request), operationKey);

    public static RentalCrudWriteRequest UnitUpdateRequest(
        WorkspaceReadScope scope,
        int entityId,
        string operationKey,
        UpdateUnitRequest request)
    {
        var payload = JsonSerializer.SerializeToNode(request, SparseUpdateJson)?.AsObject()
            ?? throw new ArgumentException("The unit update is invalid.", nameof(request));
        if (request.FloorPlanSpecified && request.FloorPlan is null)
            payload[nameof(UpdateUnitRequest.FloorPlan)] = null;
        if (request.BedroomsSpecified && request.Bedrooms is null)
            payload[nameof(UpdateUnitRequest.Bedrooms)] = null;
        if (request.BathroomsSpecified && request.Bathrooms is null)
            payload[nameof(UpdateUnitRequest.Bathrooms)] = null;
        if (request.NotesSpecified && request.Notes is null)
            payload[nameof(UpdateUnitRequest.Notes)] = null;

        return new RentalCrudWriteRequest(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, AtomicRentalMutationDomain.Unit,
            AtomicRentalMutationOperation.Update, entityId, payload.ToJsonString(), operationKey);
    }

    public static string IdempotencyKey(RentalCrudWriteRequest request) =>
        $"{request.PortfolioId}:{request.AccessContextId}:{request.Domain}:{request.Operation}:" +
        $"{request.EntityId}:{request.DeliveryIdempotencyKey}";

    public static TransactionalWrite<RentalCrudWriteRequest, AtomicRentalMutationResult> Write(
        RentalCrudWriteRequest request,
        Func<RentalCrudWriteRequest, IAtomicCommandContext, CancellationToken,
            Task<AtomicRentalMutationResult>> executeAsync,
        Func<RentalCrudWriteRequest, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync) =>
        TransactionalWriteDefaults.AuthorizationScoped(
            $"rental.unit.{request.Operation.ToString().ToLowerInvariant()}",
            request,
            ResultContract,
            executeAsync,
            authorizeReplayAsync,
            request.EntityId > 0 ? WriteLock.For("Unit", request.EntityId) : null);

    public static async Task<DateTime> BeginExecutionAsync(
        RentalCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(request);
        return await context.ReadDatabaseClockUtcAsync(ct);
    }

    public static T Read<T>(RentalCrudWriteRequest request) where T : class =>
        JsonSerializer.Deserialize<T>(request.RequestJson)
        ?? throw new ArgumentException("The rental update is invalid.");

    public static void Validate(RentalCrudWriteRequest request)
    {
        TransactionalWriteDefaults.ValidateAuthorizationScope(request);
        if (request.Domain != AtomicRentalMutationDomain.Unit
            || request.Operation is not AtomicRentalMutationOperation.Create
                and not AtomicRentalMutationOperation.Update
                and not AtomicRentalMutationOperation.Delete
            || string.IsNullOrWhiteSpace(request.RequestJson)
            || (request.Operation != AtomicRentalMutationOperation.Create && request.EntityId <= 0))
        {
            throw new ArgumentException(
                "Portfolio, actor, access revision, operation, and delivery identifiers are required.");
        }
    }
}

internal sealed class UnitCrudWriteRules(RentalCommandDbContext db)
{
    public Task<AtomicRentalMutationResult> CreateAsync(
        RentalCrudWriteRequest request, IAtomicCommandContext context, CancellationToken ct) =>
        ExecuteAsync(request, context, AtomicRentalMutationOperation.Create, ct);

    public Task<AtomicRentalMutationResult> UpdateAsync(
        RentalCrudWriteRequest request, IAtomicCommandContext context, CancellationToken ct) =>
        ExecuteAsync(request, context, AtomicRentalMutationOperation.Update, ct);

    public Task<AtomicRentalMutationResult> DeleteAsync(
        RentalCrudWriteRequest request, IAtomicCommandContext context, CancellationToken ct) =>
        ExecuteAsync(request, context, AtomicRentalMutationOperation.Delete, ct);

    public async Task AuthorizeReplayAsync(
        RentalCrudWriteRequest request, IAtomicCommandContext context, CancellationToken ct)
    {
        RentalCrudWriteSupport.Validate(request);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var authorized = request.Operation == AtomicRentalMutationOperation.Create
            ? await AuthorizeCreateAsync(request, now, ct)
            : await AuthorizeUnitAsync(request, now, ct);
        if (!authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private async Task<AtomicRentalMutationResult> ExecuteAsync(
        RentalCrudWriteRequest command,
        IAtomicCommandContext attempt,
        AtomicRentalMutationOperation expectedOperation,
        CancellationToken ct)
    {
        if (command.Operation != expectedOperation)
            throw new ArgumentException("That unit action is not supported.");
        var now = await RentalCrudWriteSupport.BeginExecutionAsync(command, attempt, ct);
        if (command.Operation == AtomicRentalMutationOperation.Create)
        {
            var request = RentalCrudWriteSupport.Read<CreateUnitRequest>(command);
            var normalizedUnitNumber = RequireNonBlank(request.UnitNumber, "Unit number");
            if (!await AuthorizeCreateAsync(command, now, ct)) throw Denied();
            var propertyType = await ReadUnitPropertyTypeAsync(
                command.PortfolioId, request.PropertyId, ct) ?? throw Conflict("Property not found.");
            if (ResidentialUnitPolicy.IsInvalidForCreate(
                    propertyType, request.Bedrooms, request.Bathrooms))
                throw new DomainValidationException(ResidentialUnitPolicy.RequiredDetailsMessage);
            if (await db.Set<Unit>().AnyAsync(unit =>
                    unit.PortfolioId == command.PortfolioId
                    && unit.PropertyId == request.PropertyId
                    && unit.UnitNumber.Trim().ToLower() == normalizedUnitNumber.ToLower()
                    && unit.DeletedAt == null, ct))
                throw new DomainValidationException(
                    $"Unit number \"{normalizedUnitNumber}\" already exists on this property.", 409);

            var entity = new Unit
            {
                PortfolioId = command.PortfolioId,
                PropertyId = request.PropertyId,
                UnitNumber = normalizedUnitNumber,
                FloorPlan = request.FloorPlan,
                Bedrooms = request.Bedrooms ?? 0m,
                Bathrooms = request.Bathrooms ?? 0m,
                SquareFeet = request.SquareFeet,
                MarketRent = request.MarketRent,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, AuditLogOperation.Created,
                $"Unit {entity.UnitNumber} created"));
            await FlushUnitAsync(attempt, ct);
            TransactionalWriteDefaults.StageDataUpdate(
                command, attempt, nameof(Unit), entity.Id, now, "entity");
            return Applied(entity.Id, JsonSerializer.Serialize(new UnitResponse
            {
                Id = entity.Id,
                PropertyId = entity.PropertyId,
                PropertyType = propertyType,
                UnitNumber = entity.UnitNumber,
                FloorPlan = entity.FloorPlan,
                Bedrooms = entity.Bedrooms,
                Bathrooms = entity.Bathrooms,
                SquareFeet = entity.SquareFeet,
                MarketRent = entity.MarketRent,
                Status = DerivedUnitStatus.Vacant,
                Notes = entity.Notes,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
            }));
        }

        var unit = await db.Set<Unit>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (unit is null) return new AtomicRentalMutationResult(false, false, 0);
        if (!await AuthorizePropertyAsync(command, now, unit.PropertyId, ct)) throw Denied();

        if (command.Operation == AtomicRentalMutationOperation.Delete)
        {
            await EnsureUnitHasNoHistoryAsync(command.PortfolioId, unit.Id, ct);
            unit.DeletedAt = now;
            unit.UpdatedAt = now;
            var flush = await attempt.FlushBusinessAsync(ct);
            var mutation = flush.Mutations.Single(candidate =>
                ReferenceEquals(candidate.EntityReference, unit));
            attempt.EnrichMutation(mutation, Audit(command, AuditLogOperation.Deleted,
                $"Unit {unit.UnitNumber} deleted"));
            TransactionalWriteDefaults.StageDataUpdate(
                command, attempt, nameof(Unit), unit.Id, now, "entity", deleted: true);
            return Applied(unit.Id);
        }

        var update = RentalCrudWriteSupport.Read<UpdateUnitRequest>(command);
        var propertyTypeForUpdate = await ReadUnitPropertyTypeAsync(
            command.PortfolioId, unit.PropertyId, ct) ?? throw Conflict("Property not found.");
        if (ResidentialUnitPolicy.IsInvalidForUpdate(
                propertyTypeForUpdate, update.BedroomsSpecified, update.Bedrooms,
                update.BathroomsSpecified, update.Bathrooms))
            throw new DomainValidationException(ResidentialUnitPolicy.RequiredDetailsMessage);
        var normalizedUpdateNumber = update.UnitNumber is null
            ? null : RequireNonBlank(update.UnitNumber, "Unit number");
        if (normalizedUpdateNumber is not null
            && !string.Equals(normalizedUpdateNumber, unit.UnitNumber, StringComparison.Ordinal)
            && await db.Set<Unit>().AnyAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.PropertyId == unit.PropertyId
                && candidate.UnitNumber.Trim().ToLower() == normalizedUpdateNumber.ToLower()
                && candidate.Id != unit.Id && candidate.DeletedAt == null, ct))
            throw new DomainValidationException(
                $"Unit number \"{normalizedUpdateNumber}\" already exists on this property.", 409);

        var changed = false;
        if (normalizedUpdateNumber is not null
            && !string.Equals(unit.UnitNumber, normalizedUpdateNumber, StringComparison.Ordinal))
        {
            unit.UnitNumber = normalizedUpdateNumber;
            changed = true;
        }
        if (update.FloorPlanSpecified)
        {
            var floorPlan = Normalize(update.FloorPlan);
            if (!string.Equals(unit.FloorPlan, floorPlan, StringComparison.Ordinal))
            {
                unit.FloorPlan = floorPlan;
                changed = true;
            }
        }
        if (update.BedroomsSpecified && unit.Bedrooms != (update.Bedrooms ?? 0m))
        {
            unit.Bedrooms = update.Bedrooms ?? 0m;
            changed = true;
        }
        if (update.BathroomsSpecified && unit.Bathrooms != (update.Bathrooms ?? 0m))
        {
            unit.Bathrooms = update.Bathrooms ?? 0m;
            changed = true;
        }
        if (update.SquareFeet.HasValue && unit.SquareFeet != update.SquareFeet.Value)
        {
            unit.SquareFeet = update.SquareFeet.Value;
            changed = true;
        }
        if (update.MarketRent.HasValue && unit.MarketRent != update.MarketRent.Value)
        {
            unit.MarketRent = update.MarketRent.Value;
            changed = true;
        }
        if (update.NotesSpecified)
        {
            var notes = Normalize(update.Notes);
            if (!string.Equals(unit.Notes, notes, StringComparison.Ordinal))
            {
                unit.Notes = notes;
                changed = true;
            }
        }
        if (!changed) return Applied(unit.Id);

        unit.UpdatedAt = now;
        attempt.BindSemanticAudit(unit, Audit(command, AuditLogOperation.Updated,
            $"Unit {unit.UnitNumber} updated"));
        await FlushUnitAsync(attempt, ct);
        TransactionalWriteDefaults.StageDataUpdate(
            command, attempt, nameof(Unit), unit.Id, now, "entity");
        return Applied(unit.Id);
    }

    private async Task<bool> AuthorizeCreateAsync(
        RentalCrudWriteRequest command, DateTime now, CancellationToken ct)
    {
        var request = RentalCrudWriteSupport.Read<CreateUnitRequest>(command);
        if (!await db.Set<Property>().AsNoTracking().AnyAsync(property =>
                property.Id == request.PropertyId && property.PortfolioId == command.PortfolioId
                && property.DeletedAt == null, ct))
            return false;
        return await AuthorizePropertyAsync(command, now, request.PropertyId, ct);
    }

    private async Task<bool> AuthorizeUnitAsync(
        RentalCrudWriteRequest command, DateTime now, CancellationToken ct)
    {
        var units = command.Operation == AtomicRentalMutationOperation.Delete
            ? db.Set<Unit>().IgnoreQueryFilters() : db.Set<Unit>();
        var propertyId = await units.AsNoTracking()
            .Where(unit => unit.Id == command.EntityId && unit.PortfolioId == command.PortfolioId
                && (command.Operation == AtomicRentalMutationOperation.Delete || unit.DeletedAt == null))
            .Select(unit => (int?)unit.PropertyId)
            .SingleOrDefaultAsync(ct);
        return propertyId.HasValue && await AuthorizePropertyAsync(command, now, propertyId, ct);
    }

    private Task<bool> AuthorizePropertyAsync(
        RentalCrudWriteRequest command, DateTime now, int? propertyId, CancellationToken ct) =>
        db.Set<Property>().AsNoTracking()
            .WhereAuthorizedForScope(db, Scope(command), CapabilityKeys.RentalsManage, now)
            .AnyAsync(property => property.Id == propertyId, ct);

    private static WorkspaceReadScope Scope(RentalCrudWriteRequest command) => new(
        command.PortfolioId, command.ActorUserId, command.AuthSessionId,
        command.AccessContextId, command.ExpectedAccessRevision);

    private async Task<PropertyType?> ReadUnitPropertyTypeAsync(
        int portfolioId, int propertyId, CancellationToken ct) =>
        await db.Set<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId
                && property.DeletedAt == null)
            .Select(property => (PropertyType?)property.PropertyType)
            .SingleOrDefaultAsync(ct);

    private async Task EnsureUnitHasNoHistoryAsync(
        int portfolioId, int unitId, CancellationToken ct)
    {
        var guard = await db.UnitDeleteEligibility(portfolioId, unitId).SingleAsync(ct);
        if (guard.IsOccupied) throw Conflict("This unit is occupied. Return possession before deleting the unit.");
        if (guard.HasPlannedOrCurrentRelationship) throw Conflict("This unit has a planned or current rental relationship. Cancel or complete it before deleting the unit.");
        if (guard.HasRentalRelationshipHistory) throw Conflict("This unit has rental relationship, legal, or financial history and cannot be deleted.");
        if (guard.HasWorkOrderHistory) throw Conflict("This unit has work order history. Archive the work order history instead of deleting the unit.");
        if (guard.HasAppointmentHistory) throw Conflict("This unit has appointment history. Archive the appointment history instead of deleting the unit.");
        if (guard.HasInspectionHistory) throw Conflict("This unit has inspection history. Archive the inspection history instead of deleting the unit.");
        if (guard.HasExpenseHistory) throw Conflict("This unit has expense history. Archive the expense history instead of deleting the unit.");
        if (guard.HasApplicationHistory) throw Conflict("This unit has application history. Archive the applications instead of deleting the unit.");
        if (guard.HasRecurringExpenseHistory) throw Conflict("This unit has recurring expense history. Archive the recurring expense history instead of deleting the unit.");
        if (guard.HasDocumentHistory) throw Conflict("This unit has document history. Archive the documents instead of deleting the unit.");
    }

    private static async Task FlushUnitAsync(IAtomicCommandContext context, CancellationToken ct)
    {
        try
        {
            await context.FlushBusinessAsync(ct);
        }
        catch (DbUpdateException ex) when (FindPostgresException(ex) is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation,
              ConstraintName: "IX_Units_PropertyId_UnitNumber_CI" })
        {
            throw new DomainValidationException(
                "A live Unit with this number already exists on the property.", 409);
        }
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres) return postgres;
            if (current.InnerException is null) return null;
        }
        return null;
    }

    private static AtomicSemanticAudit Audit(
        RentalCrudWriteRequest command, AuditLogOperation operation, string reason) => new(
            command.PortfolioId, nameof(Unit), command.EntityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private static string RequireNonBlank(string value, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new DomainValidationException($"{field} is required.");
        return trimmed;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static AtomicRentalMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, ResponseJson: responseJson);
    private static UnauthorizedAccessException Denied() => new(
        "The record is not authorized in the current workspace scope.");
    private static DomainValidationException Conflict(string message) => new(message, 409);
}
