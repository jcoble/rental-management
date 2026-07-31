using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Api.Services;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicCoreCrudMutationDomain { Property, OwnerEntity, Tenant, Vendor }
public enum AtomicCoreCrudMutationOperation { Create, Update, Delete, Setup }

public sealed record AtomicCoreCrudMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicCoreCrudMutationDomain Domain,
    AtomicCoreCrudMutationOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore]
    DateTime? CreatedAtUtc = null,
    [property: AtomicFingerprintIgnore]
    DateTime? ChangedAtUtc = null) : IAtomicCommandData;

public sealed record AtomicCoreCrudMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null);

public sealed class AtomicCoreCrudMutationHandler
    : IAtomicCommandHandler<AtomicCoreCrudMutationCommand, AtomicCoreCrudMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicCoreCrudMutationHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicCoreCrudMutationResult> HandleAsync(
        AtomicCoreCrudMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        if (command.EntityId > 0)
        {
            var resource = command.Domain switch
            {
                AtomicCoreCrudMutationDomain.Property => "Property",
                AtomicCoreCrudMutationDomain.OwnerEntity => "OwnerEntity",
                AtomicCoreCrudMutationDomain.Tenant => "Tenant",
                AtomicCoreCrudMutationDomain.Vendor => "Vendor",
                _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
            };
            await attempt.AcquireLockAsync(resource, command.EntityId, ct);
        }

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        return command.Domain switch
        {
            AtomicCoreCrudMutationDomain.Property => await MutatePropertyAsync(command, attempt, now, ct),
            AtomicCoreCrudMutationDomain.OwnerEntity => await MutateOwnerEntityAsync(command, attempt, now, ct),
            AtomicCoreCrudMutationDomain.Tenant => await MutateTenantAsync(command, attempt, now, ct),
            AtomicCoreCrudMutationDomain.Vendor => await MutateVendorAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicCoreCrudMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var authorized = command.Domain switch
        {
            AtomicCoreCrudMutationDomain.Property when command.Operation == AtomicCoreCrudMutationOperation.Create ||
                (command.Operation == AtomicCoreCrudMutationOperation.Setup && command.EntityId == 0) =>
                await AuthorizeAllPropertiesAsync(command, _db, now, CapabilityKeys.RentalsManage, ct),
            AtomicCoreCrudMutationDomain.Property =>
                await AuthorizePropertyEntityAsync(command, _db, now, CapabilityKeys.RentalsManage, ct),
            AtomicCoreCrudMutationDomain.OwnerEntity when command.Operation == AtomicCoreCrudMutationOperation.Create =>
                await AuthorizeAllPropertiesAsync(command, _db, now, CapabilityKeys.RentalsManage, ct),
            AtomicCoreCrudMutationDomain.OwnerEntity =>
                await AuthorizeOwnerEntityAsync(command, _db, now, ct),
            AtomicCoreCrudMutationDomain.Tenant when command.Operation == AtomicCoreCrudMutationOperation.Create =>
                await AuthorizeAllPropertiesEitherAsync(command, _db, now,
                    CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage, ct),
            AtomicCoreCrudMutationDomain.Tenant =>
                await AuthorizeTenantAsync(command, _db, now, ct),
            AtomicCoreCrudMutationDomain.Vendor when command.Operation == AtomicCoreCrudMutationOperation.Create =>
                await AuthorizeAnyPropertyAsync(command, _db, now, CapabilityKeys.WorkManage, ct),
            AtomicCoreCrudMutationDomain.Vendor =>
                await AuthorizeAllPropertiesAsync(command, _db, now, CapabilityKeys.WorkManage, ct),
            _ => false,
        };
        if (!authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private async Task<AtomicCoreCrudMutationResult> MutatePropertyAsync(
        AtomicCoreCrudMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        const string entityType = nameof(Property);
        var db = _db;
        if (command.Operation == AtomicCoreCrudMutationOperation.Setup)
            return await SetupPropertyAsync(command, attempt, now, ct);

        if (command.Operation == AtomicCoreCrudMutationOperation.Create)
            throw Conflict("Properties must be created with the atomic Property setup command so their Units are committed together.");

        var property = await db.Set<Property>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (property is null) return Missing();
        if (!await AuthorizePropertyEntityAsync(command, db, now, CapabilityKeys.RentalsManage, ct))
            throw Denied();

        if (command.Operation == AtomicCoreCrudMutationOperation.Delete)
        {
            var unitState = await db.Set<Unit>().AsNoTracking()
                .Where(unit => unit.PortfolioId == command.PortfolioId
                    && unit.PropertyId == property.Id && unit.DeletedAt == null)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Count = group.Count(),
                    FirstId = group.Min(unit => unit.Id),
                    FirstNumber = group.OrderBy(unit => unit.Id).Select(unit => unit.UnitNumber).First(),
                }).SingleOrDefaultAsync(ct);
            if (unitState is not null)
            {
                var noun = unitState.Count == 1 ? "unit" : "units";
                throw Conflict($"This property still has {unitState.Count} {noun}. Remove the {noun} before deleting this property.");
            }
            await EnsurePropertyHasNoHistoryAsync(command.PortfolioId, property.Id, db, ct);
            property.DeletedAt = now;
            property.UpdatedAt = now;
            attempt.BindSemanticAudit(property, Audit(command, entityType,
                AuditLogOperation.Deleted, $"Property {property.Name} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, entityType, property.Id, now, "property", deleted: true);
            return Applied(property.Id);
        }

        if (command.Operation != AtomicCoreCrudMutationOperation.Update)
            throw new ArgumentException("Unsupported Property mutation operation.");
        var mutationNow = command.ChangedAtUtc ?? now;
        var update = Read<UpdatePropertyRequest>(command);
        await EnsurePropertyYearBuiltIsNotFutureAsync(
            update.YearBuilt, command.PortfolioId, db, ct);
        OwnershipLifecycleChange? ownershipChange = null;
        var ownershipRequests = RequestedOwnerships(
            update.Ownerships, update.ClearOwnership);
        if (ownershipRequests is not null)
        {
            var ownerships = await BuildOwnershipsAsync(
                command.PortfolioId, property.Id, ownershipRequests, mutationNow, db, ct);
            ownershipChange = await ReplaceCurrentOwnershipsAsync(
                command, property.Id, ownerships, mutationNow, attempt, ct);
        }
        if (update.Status == PropertyStatus.Inactive && property.Status != PropertyStatus.Inactive)
            await EnsurePropertyHasNoCurrentOccupancyAsync(command.PortfolioId, property.Id, db, ct);

        if (update.Name is not null) property.Name = update.Name;
        if (update.PropertyType.HasValue) property.PropertyType = update.PropertyType.Value;
        if (update.Status.HasValue) property.Status = update.Status.Value;
        if (update.AddressLine1 is not null) property.AddressLine1 = update.AddressLine1;
        if (update.AddressLine2 is not null) property.AddressLine2 = NormalizeOptionalText(update.AddressLine2);
        if (update.City is not null) property.City = update.City;
        if (update.State is not null) property.State = update.State;
        if (update.PostalCode is not null) property.PostalCode = update.PostalCode;
        if (update.YearBuilt.HasValue) property.YearBuilt = update.YearBuilt;
        if (update.ManagementFeePercent.HasValue) property.ManagementFeePercent = update.ManagementFeePercent;
        if (update.Notes is not null) property.Notes = update.Notes;
        if (update.PurchasePrice.HasValue) property.PurchasePrice = update.PurchasePrice;
        if (update.LandValue.HasValue) property.LandValue = update.LandValue;
        if (update.InServiceDate.HasValue) property.InServiceDate = Utc(update.InServiceDate);
        if (update.ManualAnnualDepreciation.HasValue) property.ManualAnnualDepreciation = update.ManualAnnualDepreciation;
        db.ChangeTracker.DetectChanges();
        if (db.Entry(property).State == EntityState.Modified)
        {
            property.UpdatedAt = mutationNow;
            db.ChangeTracker.DetectChanges();
            attempt.BindSemanticAudit(property, Audit(command, entityType,
                AuditLogOperation.Updated, $"Property {property.Name} updated"));
        }

        await attempt.FlushBusinessAsync(ct);
        StageOwnershipLifecycleAudits(attempt, command, property.Id, ownershipChange, mutationNow);
        StageDataUpdate(attempt, command, entityType, property.Id, mutationNow, "property");
        return Applied(property.Id, await SnapshotPropertyAsync(property, db, ct, mutationNow));
    }

    private async Task<AtomicCoreCrudMutationResult> SetupPropertyAsync(
        AtomicCoreCrudMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        var setup = Read<SetupPropertyRequest>(command);
        ValidateSetup(setup, command.EntityId);

        var updated = command.EntityId > 0;
        var mutationNow = updated
            ? command.ChangedAtUtc ?? now
            : command.CreatedAtUtc ?? now;
        if (updated)
        {
            if (!await AuthorizePropertyEntityAsync(command, db, now, CapabilityKeys.RentalsManage, ct))
                throw Denied();
        }
        else if (!await AuthorizeAllPropertiesAsync(
                     command, db, now, CapabilityKeys.RentalsManage, ct))
        {
            throw Denied();
        }

        var request = setup.Property;
        await EnsurePropertyYearBuiltIsNotFutureAsync(
            request.YearBuilt, command.PortfolioId, db, ct);
        var requestedOwnerships = RequestedOwnerships(
            request.Ownerships, request.ClearOwnership) ?? [];
        if (requestedOwnerships.Count == 0 && !request.ClearOwnership)
        {
            var primaryOwnerId = await db.Set<OwnerEntity>().AsNoTracking()
                .Where(owner => owner.PortfolioId == command.PortfolioId && owner.IsPrimary
                    && owner.DeletedAt == null)
                .Select(owner => (int?)owner.Id)
                .FirstOrDefaultAsync(ct);
            if (primaryOwnerId.HasValue)
                requestedOwnerships = [new PropertyOwnershipRequest { OwnerEntityId = primaryOwnerId.Value }];
        }

        var requestedUnitNumbers = setup.Units
            .Select(unit => unit.UnitNumber.Trim().ToLowerInvariant())
            .ToArray();

        Property property;
        var existingUnitCount = 0;
        if (updated)
        {
            property = await db.Set<Property>().SingleOrDefaultAsync(entity =>
                entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
                && entity.DeletedAt == null, ct) ?? throw Conflict("Property not found.");

            existingUnitCount = await db.Set<Unit>().AsNoTracking().CountAsync(unit =>
                unit.PortfolioId == command.PortfolioId && unit.PropertyId == property.Id
                && unit.DeletedAt == null, ct);
            if (requestedUnitNumbers.Length > 0 && await db.Set<Unit>().AsNoTracking().AnyAsync(unit =>
                    unit.PortfolioId == command.PortfolioId && unit.PropertyId == property.Id
                    && unit.DeletedAt == null
                    && requestedUnitNumbers.Contains(unit.UnitNumber.ToLower()), ct))
                throw Conflict("One or more Unit numbers already exist on this Property.");

            if (property.RentalStructure == RentalStructure.MultiRental
                && request.RentalStructure == RentalStructure.SingleRental)
            {
                var historicalUnitCount = await db.Set<Unit>().IgnoreQueryFilters().AsNoTracking()
                    .CountAsync(unit => unit.PortfolioId == command.PortfolioId
                        && unit.PropertyId == property.Id, ct);
                if (historicalUnitCount != existingUnitCount
                    || existingUnitCount + setup.Units.Count != 1)
                    throw Conflict("A MultiRental Property with multiple Unit records or Unit history cannot be converted to SingleRental.");
            }

            ApplyPropertySetup(property, request, mutationNow);
            var ownerships = await BuildOwnershipsAsync(
                command.PortfolioId, property.Id, requestedOwnerships, mutationNow, db, ct);
            var ownershipChange = await ReplaceCurrentOwnershipsAsync(
                command, property.Id, ownerships, mutationNow, attempt, ct);
            attempt.BindSemanticAudit(property, Audit(command, nameof(Property),
                AuditLogOperation.Updated, $"Property {property.Name} updated during Guided Setup", property.Id));
            await attempt.FlushBusinessAsync(ct);
            StageOwnershipLifecycleAudits(attempt, command, property.Id, ownershipChange, mutationNow);
        }
        else
        {
            property = new Property
            {
                PortfolioId = command.PortfolioId,
                CreatedAt = mutationNow,
            };
            ApplyPropertySetup(property, request, mutationNow);
            db.Add(property);
            attempt.BindSemanticAudit(property, Audit(command, nameof(Property),
                AuditLogOperation.Created, $"Property {property.Name} created during Guided Setup", entityId: 0));
        }

        var totalUnitCount = existingUnitCount + setup.Units.Count;
        if (request.RentalStructure == RentalStructure.SingleRental && totalUnitCount != 1)
            throw Conflict("A SingleRental Property must contain exactly one Unit.");
        if (request.RentalStructure == RentalStructure.MultiRental && totalUnitCount < 1)
            throw Conflict("A MultiRental Property must contain at least one Unit.");

        if (!updated)
            await attempt.FlushBusinessAsync(ct);

        if (!updated && requestedOwnerships.Count > 0)
        {
            var ownerships = await BuildOwnershipsAsync(
                command.PortfolioId, property.Id, requestedOwnerships, mutationNow, db, ct);
            foreach (var ownership in ownerships)
                db.Add(ownership);
            await attempt.FlushBusinessAsync(ct);
        }

        var units = setup.Units.Select(unit => new Unit
        {
            PortfolioId = command.PortfolioId,
            PropertyId = property.Id,
            Property = property,
            UnitNumber = unit.UnitNumber.Trim(),
            FloorPlan = unit.FloorPlan,
            Bedrooms = unit.Bedrooms,
            Bathrooms = unit.Bathrooms,
            SquareFeet = unit.SquareFeet,
            MarketRent = unit.MarketRent,
            Notes = unit.Notes,
            CreatedAt = mutationNow,
            UpdatedAt = mutationNow,
        }).ToList();

        foreach (var unit in units)
        {
            db.Add(unit);
            attempt.BindSemanticAudit(unit, Audit(command, nameof(Unit),
                AuditLogOperation.Created, $"Unit {unit.UnitNumber} created during Guided Setup", entityId: 0));
        }
        await attempt.FlushBusinessAsync(ct);

        StageDataUpdate(attempt, command, nameof(Property), property.Id, mutationNow, "property");
        for (var index = 0; index < units.Count; index++)
            StageDataUpdate(attempt, command, nameof(Unit), units[index].Id, mutationNow, $"unit-{index + 1}");

        var currentUnits = await db.Set<Unit>().AsNoTracking()
            .Where(unit => unit.PortfolioId == command.PortfolioId
                && unit.PropertyId == property.Id && unit.DeletedAt == null)
            .OrderBy(unit => unit.UnitNumber)
            .ThenBy(unit => unit.Id)
            .ToListAsync(ct);
        var propertySnapshot = JsonSerializer.Deserialize<PropertyResponse>(
            await SnapshotPropertyAsync(property, db, ct, mutationNow))
            ?? throw new AtomicReceiptInvariantException("Property setup response could not be created.");
        var response = new PropertySetupResponse
        {
            Property = propertySnapshot,
            Updated = updated,
            Units = currentUnits.Select(ToUnitResponse).ToList(),
        };
        return Applied(property.Id, JsonSerializer.Serialize(response));
    }

    private void ValidateSetup(SetupPropertyRequest setup, int commandPropertyId)
    {
        if (setup.PropertyId.GetValueOrDefault() != commandPropertyId)
            throw new ArgumentException("The setup Property id does not match the command target.");
        if (!Enum.IsDefined(setup.Property.RentalStructure))
            throw new ArgumentException("RentalStructure must be SingleRental or MultiRental.");
        if (string.IsNullOrWhiteSpace(setup.Property.Name)
            || string.IsNullOrWhiteSpace(setup.Property.AddressLine1)
            || string.IsNullOrWhiteSpace(setup.Property.City)
            || string.IsNullOrWhiteSpace(setup.Property.State)
            || string.IsNullOrWhiteSpace(setup.Property.PostalCode))
            throw new ArgumentException("Property name and address fields are required.");
        if (setup.Units.Any(unit => string.IsNullOrWhiteSpace(unit.UnitNumber)))
            throw new ArgumentException("Every setup Unit requires a Unit number.");
        if (setup.Units.Select(unit => unit.UnitNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != setup.Units.Count)
            throw Conflict("Unit numbers must be unique within the Property.");
    }

    private void ApplyPropertySetup(Property property, CreatePropertyRequest request, DateTime now)
    {
        property.Name = request.Name.Trim();
        property.PropertyType = request.PropertyType;
        property.RentalStructure = request.RentalStructure;
        property.Status = request.Status;
        property.AddressLine1 = request.AddressLine1.Trim();
        property.AddressLine2 = request.AddressLine2;
        property.City = request.City.Trim();
        property.State = request.State.Trim();
        property.PostalCode = request.PostalCode.Trim();
        property.YearBuilt = request.YearBuilt;
        property.ManagementFeePercent = request.ManagementFeePercent;
        property.Notes = request.Notes;
        property.PurchasePrice = request.PurchasePrice;
        property.LandValue = request.LandValue;
        property.InServiceDate = Utc(request.InServiceDate);
        property.ManualAnnualDepreciation = request.ManualAnnualDepreciation;
        property.UpdatedAt = now;
    }

    private async Task EnsurePropertyYearBuiltIsNotFutureAsync(
        int? yearBuilt,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (yearBuilt is not { } requestedYear) return;
        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(db, portfolioId, ct);
        var businessYear = times.BusinessDate.Year;
        if (requestedYear > businessYear)
            throw Conflict($"Year built cannot be later than the portfolio business year ({businessYear}).");
    }

    private IReadOnlyList<PropertyOwnershipRequest>? RequestedOwnerships(
        IReadOnlyList<PropertyOwnershipRequest>? ownerships,
        bool clearOwnership)
    {
        if (clearOwnership && ownerships is { Count: > 0 })
            throw new ArgumentException("Clearing ownership cannot be combined with owner assignments.");
        if (ownerships is not null) return ownerships;
        return clearOwnership ? [] : null;
    }

    private async Task<List<PropertyOwnership>> BuildOwnershipsAsync(
        int portfolioId,
        int propertyId,
        IReadOnlyList<PropertyOwnershipRequest> requests,
        DateTime now,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (requests.Count == 0) return [];
        var ownerIds = requests.Select(request => request.OwnerEntityId).ToArray();
        if (ownerIds.Any(id => id <= 0) || ownerIds.Distinct().Count() != ownerIds.Length)
            throw new ArgumentException("Each ownership row must reference one distinct OwnerEntity.");
        if (requests.Sum(request => request.OwnershipSharePercent) != 100m)
            throw new ArgumentException("Current Property ownership shares must total exactly 100 percent.");

        var owners = await db.Set<OwnerEntity>().AsNoTracking()
            .Where(owner => owner.PortfolioId == portfolioId
                && ownerIds.Contains(owner.Id)
                && owner.DeletedAt == null)
            .Select(owner => new { owner.Id, owner.Name, owner.Email })
            .ToListAsync(ct);
        if (owners.Count != ownerIds.Length)
            throw new AtomicReceiptInvariantException(
                "One or more OwnerEntities are missing or outside this workspace.");

        var ownerById = owners.ToDictionary(owner => owner.Id);
        var result = new List<PropertyOwnership>(requests.Count);
        foreach (var request in requests)
        {
            if (request.OwnershipSharePercent <= 0m || request.OwnershipSharePercent > 100m)
                throw new ArgumentException("Ownership share must be greater than zero and no more than 100 percent.");
            var effectiveFrom = Utc(request.EffectiveFromUtc) ?? now;
            var effectiveTo = Utc(request.EffectiveToUtc);
            if (effectiveTo.HasValue && effectiveTo <= effectiveFrom)
                throw new ArgumentException("Ownership end must be later than its effective start.");
            var owner = ownerById[request.OwnerEntityId];
            var statementRecipientName = request.StatementRecipientName?.Trim();
            var payeeName = request.PayeeName?.Trim();
            result.Add(new PropertyOwnership
            {
                PortfolioId = portfolioId,
                PropertyId = propertyId,
                OwnerEntityId = owner.Id,
                OwnershipSharePercent = request.OwnershipSharePercent,
                EffectiveFromUtc = effectiveFrom,
                EffectiveToUtc = effectiveTo,
                StatementRecipientName = string.IsNullOrWhiteSpace(statementRecipientName)
                    ? owner.Name
                    : statementRecipientName,
                StatementRecipientEmail = request.StatementRecipientEmail?.Trim() ?? owner.Email,
                PayeeName = string.IsNullOrWhiteSpace(payeeName) ? owner.Name : payeeName,
            });
        }
        return result;
    }

    private async Task<OwnershipLifecycleChange> ReplaceCurrentOwnershipsAsync(
        AtomicCoreCrudMutationCommand command,
        int propertyId,
        IReadOnlyList<PropertyOwnership> replacements,
        DateTime now,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        var db = _db;
        var current = await db.Set<PropertyOwnership>()
            .Where(ownership => ownership.PortfolioId == command.PortfolioId
                && ownership.PropertyId == propertyId
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))
            .ToListAsync(ct);
        var remainingReplacements = replacements.ToDictionary(ownership => ownership.OwnerEntityId);
        var ended = new List<PropertyOwnership>();
        var deleted = new List<PropertyOwnership>();
        var updated = new List<PropertyOwnership>();
        foreach (var ownership in current)
        {
            if (remainingReplacements.Remove(ownership.OwnerEntityId, out var requested))
            {
                ownership.OwnershipSharePercent = requested.OwnershipSharePercent;
                ownership.StatementRecipientName = requested.StatementRecipientName;
                ownership.StatementRecipientEmail = requested.StatementRecipientEmail;
                ownership.PayeeName = requested.PayeeName;
                updated.Add(ownership);
                continue;
            }

            if (ownership.EffectiveFromUtc < now)
            {
                ownership.EffectiveToUtc = now;
                ended.Add(ownership);
            }
            else
            {
                ownership.EffectiveFromUtc = now.AddTicks(-10);
                ownership.EffectiveToUtc = now;
                ended.Add(ownership);
            }
        }

        foreach (var ownership in remainingReplacements.Values)
            db.Add(ownership);

        return new OwnershipLifecycleChange(
            ended,
            deleted,
            updated,
            remainingReplacements.Values.ToList());
    }

    private void StageOwnershipLifecycleAudits(
        IAtomicCommandContext attempt,
        AtomicCoreCrudMutationCommand command,
        int propertyId,
        OwnershipLifecycleChange? change,
        DateTime occurredAtUtc)
    {
        if (change is null) return;
        foreach (var ownership in change.Ended)
            attempt.StageSemanticEvent(Audit(command, nameof(PropertyOwnership),
                AuditLogOperation.Updated,
                $"Property ownership for Property {propertyId} ended during owner replacement",
                ownership.Id), occurredAtUtc);
        foreach (var ownership in change.Deleted)
            attempt.StageSemanticEvent(Audit(command, nameof(PropertyOwnership),
                AuditLogOperation.Deleted,
                $"Property ownership for Property {propertyId} removed during owner replacement",
                ownership.Id), occurredAtUtc);
        foreach (var ownership in change.Updated)
            attempt.StageSemanticEvent(Audit(command, nameof(PropertyOwnership),
                AuditLogOperation.Updated,
                $"Property ownership for Property {propertyId} retained during owner replacement",
                ownership.Id), occurredAtUtc);
        foreach (var ownership in change.Created)
            attempt.StageSemanticEvent(Audit(command, nameof(PropertyOwnership),
                AuditLogOperation.Created,
                $"Property ownership for Property {propertyId} created during owner replacement",
                ownership.Id), occurredAtUtc);
    }

    private sealed record OwnershipLifecycleChange(
        IReadOnlyList<PropertyOwnership> Ended,
        IReadOnlyList<PropertyOwnership> Deleted,
        IReadOnlyList<PropertyOwnership> Updated,
        IReadOnlyList<PropertyOwnership> Created);

    private UnitResponse ToUnitResponse(Unit unit) => new()
    {
        Id = unit.Id,
        PropertyId = unit.PropertyId,
        UnitNumber = unit.UnitNumber,
        FloorPlan = unit.FloorPlan,
        Bedrooms = unit.Bedrooms,
        Bathrooms = unit.Bathrooms,
        SquareFeet = unit.SquareFeet,
        MarketRent = unit.MarketRent,
        Status = DerivedUnitStatus.Vacant,
        Notes = unit.Notes,
        CreatedAt = unit.CreatedAt,
        UpdatedAt = unit.UpdatedAt,
    };

    private async Task<AtomicCoreCrudMutationResult> MutateOwnerEntityAsync(
        AtomicCoreCrudMutationCommand command, IAtomicCommandContext attempt, DateTime now, CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicCoreCrudMutationOperation.Create)
        {
            if (!await AuthorizeAllPropertiesAsync(command, db, now, CapabilityKeys.RentalsManage, ct))
                throw Denied();
            var request = Read<CreateOwnerEntityRequest>(command);
            var entity = new OwnerEntity
            {
                PortfolioId = command.PortfolioId, OwnerEntityType = request.OwnerEntityType,
                Name = request.Name, TaxId = request.TaxId, AddressLine1 = request.AddressLine1,
                AddressLine2 = request.AddressLine2, City = request.City, State = request.State,
                PostalCode = request.PostalCode,
                Phone = request.Phone, Email = request.Email, CreatedAt = now, UpdatedAt = now,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerEntity), AuditLogOperation.Created,
                $"Owner {entity.Name} created", entityId: 0));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(OwnerEntity), entity.Id, now);
            return Applied(entity.Id, await SnapshotOwnerAsync(entity, db, ct));
        }
        var owner = await db.Set<OwnerEntity>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (owner is null) return Missing();
        if (!await AuthorizeOwnerEntityAsync(command, db, now, ct)) throw Denied();
        if (command.Operation == AtomicCoreCrudMutationOperation.Delete)
        {
            var deleteGuard = await db.Set<OwnerEntity>()
                .AsNoTracking()
                .Where(candidate =>
                    candidate.PortfolioId == command.PortfolioId &&
                    candidate.Id == owner.Id)
                .Select(candidate => new
                {
                    PropertyCount = db.Set<PropertyOwnership>()
                        .Where(ownership =>
                            ownership.PortfolioId == command.PortfolioId &&
                            ownership.OwnerEntityId == candidate.Id &&
                            (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now) &&
                            ownership.Property != null &&
                            ownership.Property.DeletedAt == null)
                        .Select(ownership => ownership.PropertyId)
                        .Distinct()
                        .Count(),
                    PortalAccessCount = db.Set<OwnerUserAccess>().Count(access =>
                        access.PortfolioId == command.PortfolioId &&
                        access.OwnerEntityId == candidate.Id &&
                        access.RevokedAtUtc == null),
                })
                .SingleAsync(ct);
            if (deleteGuard.PropertyCount > 0)
                throw Conflict($"This owner is assigned to {deleteGuard.PropertyCount} {(deleteGuard.PropertyCount == 1 ? "property" : "properties")}. Reassign or clear those properties before deleting this owner.");
            if (deleteGuard.PortalAccessCount > 0)
                throw Conflict("Revoke this owner's portal access before deleting the owner.");
            var distributions = await db.Set<OwnerDistribution>().AsNoTracking().CountAsync(row =>
                row.PortfolioId == command.PortfolioId && row.OwnerEntityId == owner.Id, ct);
            if (distributions > 0)
                throw Conflict($"This owner has {distributions} recorded {(distributions == 1 ? "distribution" : "distributions")}. Delete or reassign them first.");
            owner.DeletedAt = now;
            owner.UpdatedAt = now;
            attempt.BindSemanticAudit(owner, Audit(command, nameof(OwnerEntity), AuditLogOperation.Deleted,
                $"Owner {owner.Name} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(OwnerEntity), owner.Id, now, deleted: true);
            return Applied(owner.Id);
        }
        if (command.Operation != AtomicCoreCrudMutationOperation.Update)
            throw new ArgumentException("Unsupported OwnerEntity mutation operation.");
        var update = Read<UpdateOwnerEntityRequest>(command);
        if (update.OwnerEntityType.HasValue) owner.OwnerEntityType = update.OwnerEntityType.Value;
        if (update.Name is not null) owner.Name = update.Name;
        if (update.TaxId is not null) owner.TaxId = update.TaxId;
        if (update.AddressLine1 is not null) owner.AddressLine1 = update.AddressLine1;
        if (update.AddressLine2 is not null) owner.AddressLine2 = update.AddressLine2;
        if (update.City is not null) owner.City = update.City;
        if (update.State is not null) owner.State = update.State;
        if (update.PostalCode is not null) owner.PostalCode = update.PostalCode;
        if (update.Phone is not null) owner.Phone = update.Phone;
        if (update.Email is not null) owner.Email = update.Email;
        owner.UpdatedAt = now;
        attempt.BindSemanticAudit(owner, Audit(command, nameof(OwnerEntity), AuditLogOperation.Updated,
            $"Owner {owner.Name} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(OwnerEntity), owner.Id, now);
        return Applied(owner.Id, await SnapshotOwnerAsync(owner, db, ct));
    }

    private async Task<AtomicCoreCrudMutationResult> MutateTenantAsync(
        AtomicCoreCrudMutationCommand command, IAtomicCommandContext attempt, DateTime now, CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicCoreCrudMutationOperation.Create)
        {
            if (!await AuthorizeAllPropertiesEitherAsync(command, db, now,
                    CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage, ct)) throw Denied();
            var createdAtUtc = command.CreatedAtUtc ?? now;
            var request = Read<CreateTenantRequest>(command);
            var entity = new Tenant
            {
                PortfolioId = command.PortfolioId, FirstName = request.FirstName,
                LastName = request.LastName, Email = request.Email, Phone = request.Phone,
                EmergencyContact = request.EmergencyContact, DateOfBirth = Utc(request.DateOfBirth),
                Notes = request.Notes, CreatedAt = createdAtUtc, UpdatedAt = createdAtUtc,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Tenant), AuditLogOperation.Created,
                $"Tenant {entity.FirstName} {entity.LastName} created", entityId: 0));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Tenant), entity.Id, createdAtUtc);
            return Applied(entity.Id, SnapshotTenant(entity, 0, 0));
        }
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (tenant is null) return Missing();
        if (!await AuthorizeTenantAsync(command, db, now, ct)) throw Denied();
        var mutationNow = command.ChangedAtUtc ?? now;
        if (command.Operation == AtomicCoreCrudMutationOperation.Delete)
        {
            var state = await db.Set<LeaseManagementParty>().AsNoTracking()
                .Where(party => party.PortfolioId == command.PortfolioId && party.TenantId == tenant.Id)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    HasHistory = group.Any(),
                    IsCurrentResident = group.Any(party => party.Role != LeaseManagementPartyRole.Guarantor
                        && db.Set<UnitOccupancyProjection>().Any(occupancy =>
                            occupancy.PortfolioId == command.PortfolioId
                            && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                        && db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                            lifecycle.PortfolioId == command.PortfolioId
                            && lifecycle.LeaseManagementId == party.LeaseManagementId
                            && party.EffectiveFrom <= lifecycle.BusinessDate
                            && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate))),
                }).SingleOrDefaultAsync(ct);
            if (state?.IsCurrentResident == true)
                throw Conflict("This tenant is a current resident in an occupied rental; return possession or change the household first.");
            if (state?.HasHistory == true)
                throw Conflict("This tenant has rental relationship history; keep the tenant record to preserve agreements and account history.");
            tenant.DeletedAt = mutationNow;
            tenant.UpdatedAt = mutationNow;
            attempt.BindSemanticAudit(tenant, Audit(command, nameof(Tenant), AuditLogOperation.Deleted,
                $"Tenant {tenant.FirstName} {tenant.LastName} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, mutationNow, deleted: true);
            return Applied(tenant.Id);
        }
        if (command.Operation != AtomicCoreCrudMutationOperation.Update)
            throw new ArgumentException("Unsupported Tenant mutation operation.");
        var update = Read<UpdateTenantRequest>(command);
        if (update.FirstName is not null) tenant.FirstName = update.FirstName;
        if (update.LastName is not null) tenant.LastName = update.LastName;
        if (update.Email is not null) tenant.Email = update.Email;
        if (update.Phone is not null) tenant.Phone = update.Phone;
        if (update.EmergencyContact is not null) tenant.EmergencyContact = update.EmergencyContact;
        if (update.DateOfBirth.HasValue) tenant.DateOfBirth = Utc(update.DateOfBirth);
        if (update.Notes is not null) tenant.Notes = update.Notes;
        tenant.UpdatedAt = mutationNow;
        attempt.BindSemanticAudit(tenant, Audit(command, nameof(Tenant), AuditLogOperation.Updated,
            $"Tenant {tenant.FirstName} {tenant.LastName} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, mutationNow);
        return Applied(tenant.Id, await SnapshotTenantAsync(tenant, db, ct));
    }

    private async Task<AtomicCoreCrudMutationResult> MutateVendorAsync(
        AtomicCoreCrudMutationCommand command, IAtomicCommandContext attempt, DateTime now, CancellationToken ct)
    {
        var db = _db;
        var authorized = command.Operation == AtomicCoreCrudMutationOperation.Create
            ? await AuthorizeAnyPropertyAsync(command, db, now, CapabilityKeys.WorkManage, ct)
            : await AuthorizeAllPropertiesAsync(command, db, now, CapabilityKeys.WorkManage, ct);
        if (!authorized) throw Denied();
        var mutationNow = command.Operation == AtomicCoreCrudMutationOperation.Create
            ? command.CreatedAtUtc ?? now
            : command.ChangedAtUtc ?? now;
        if (command.Operation == AtomicCoreCrudMutationOperation.Create)
        {
            var request = Read<CreateVendorRequest>(command);
            var entity = new Vendor
            {
                PortfolioId = command.PortfolioId, Name = request.Name, ServiceType = request.ServiceType,
                Email = request.Email, Phone = request.Phone, Website = request.Website, TaxId = request.TaxId,
                AddressLine1 = request.AddressLine1, City = request.City, State = request.State,
                PostalCode = request.PostalCode, Is1099Eligible = request.Is1099Eligible,
                W9OnFile = request.W9OnFile, Preferred = request.Preferred, Notes = request.Notes,
                CreatedAt = mutationNow, UpdatedAt = mutationNow,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Vendor), AuditLogOperation.Created,
                $"Vendor {entity.Name} created", entityId: 0));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Vendor), entity.Id, mutationNow);
            return Applied(entity.Id, JsonSerializer.Serialize(VendorResponse.FromEntity(entity)));
        }
        var vendor = await db.Set<Vendor>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (vendor is null) return Missing();
        if (command.Operation == AtomicCoreCrudMutationOperation.Delete)
        {
            var open = await db.Set<WorkOrder>().AsNoTracking().CountAsync(work =>
                work.PortfolioId == command.PortfolioId && work.VendorId == vendor.Id
                && work.Status != WorkOrderStatus.Completed && work.Status != WorkOrderStatus.Cancelled
                && work.Status != WorkOrderStatus.Archived, ct);
            if (open > 0) throw Conflict($"This vendor is assigned to {open} open {(open == 1 ? "work order" : "work orders")}; reassign or close them first.");
            vendor.DeletedAt = mutationNow;
            vendor.UpdatedAt = mutationNow;
            attempt.BindSemanticAudit(vendor, Audit(command, nameof(Vendor), AuditLogOperation.Deleted,
                $"Vendor {vendor.Name} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Vendor), vendor.Id, mutationNow, deleted: true);
            return Applied(vendor.Id);
        }
        if (command.Operation != AtomicCoreCrudMutationOperation.Update)
            throw new ArgumentException("Unsupported Vendor mutation operation.");
        var update = Read<UpdateVendorRequest>(command);
        if (update.Name is not null) vendor.Name = update.Name;
        if (update.ServiceType is not null) vendor.ServiceType = update.ServiceType;
        if (update.Email is not null) vendor.Email = update.Email;
        if (update.Phone is not null) vendor.Phone = update.Phone;
        if (update.Website is not null) vendor.Website = update.Website;
        if (update.TaxId is not null) vendor.TaxId = update.TaxId;
        if (update.AddressLine1 is not null) vendor.AddressLine1 = update.AddressLine1;
        if (update.City is not null) vendor.City = update.City;
        if (update.State is not null) vendor.State = update.State;
        if (update.PostalCode is not null) vendor.PostalCode = update.PostalCode;
        if (update.Is1099Eligible.HasValue) vendor.Is1099Eligible = update.Is1099Eligible.Value;
        if (update.W9OnFile.HasValue) vendor.W9OnFile = update.W9OnFile.Value;
        if (update.Preferred.HasValue) vendor.Preferred = update.Preferred.Value;
        if (update.Notes is not null) vendor.Notes = update.Notes;
        vendor.UpdatedAt = mutationNow;
        attempt.BindSemanticAudit(vendor, Audit(command, nameof(Vendor), AuditLogOperation.Updated,
            $"Vendor {vendor.Name} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Vendor), vendor.Id, mutationNow);
        return Applied(vendor.Id, JsonSerializer.Serialize(VendorResponse.FromEntity(vendor)));
    }

    private Task<bool> AuthorizeAllPropertiesAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CancellationToken ct) =>
        AuthorizeAllPropertiesEitherAsync(command, db, now, capability, capability, ct);

    private Task<bool> AuthorizeAnyPropertyAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CancellationToken ct) =>
        AuthorizedProperties(command, db, now, capability, capability)
            .AnyAsync(ct);

    private Task<bool> AuthorizeAllPropertiesEitherAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability,
        CancellationToken ct) =>
        AuthorizedAssignments(command, db, now, firstCapability, secondCapability)
            .AnyAsync(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);

    private Task<bool> AuthorizePropertyEntityAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CancellationToken ct) =>
        AuthorizedProperties(command, db, now, capability, capability)
            .AnyAsync(property => property.Id == command.EntityId, ct);

    private async Task<bool> AuthorizeOwnerEntityAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var assignments = AuthorizedAssignments(command, db, now,
            CapabilityKeys.RentalsManage, CapabilityKeys.RentalsManage);
        if (await assignments.AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct))
            return true;
        var authorized = AuthorizedProperties(command, db, now,
            CapabilityKeys.RentalsManage, CapabilityKeys.RentalsManage);
        return await db.Set<OwnerEntity>().AsNoTracking().AnyAsync(owner =>
            owner.Id == command.EntityId && owner.PortfolioId == command.PortfolioId
            && db.Set<PropertyOwnership>().Any(ownership =>
                ownership.PortfolioId == command.PortfolioId
                && ownership.OwnerEntityId == owner.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null)
            && !db.Set<PropertyOwnership>().Any(ownership =>
                ownership.PortfolioId == command.PortfolioId
                && ownership.OwnerEntityId == owner.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null
                && !authorized.Any(allowed => allowed.Id == ownership.PropertyId)), ct);
    }

    private async Task<bool> AuthorizeTenantAsync(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        if (await AuthorizeAllPropertiesEitherAsync(command, db, now,
                CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage, ct))
            return true;
        var authorized = AuthorizedProperties(command, db, now,
            CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage);
        return await db.Set<Tenant>().AsNoTracking().AnyAsync(tenant =>
            tenant.Id == command.EntityId && tenant.PortfolioId == command.PortfolioId
            && tenant.LeaseManagementParties.Any(party =>
                party.PortfolioId == command.PortfolioId && party.LeaseManagement != null
                && authorized.Any(property => property.Id == party.LeaseManagement.PropertyId)), ct);
    }

    private IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                (grant.CapabilityDefinition!.Key == firstCapability
                    || grant.CapabilityDefinition.Key == secondCapability)
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));

    private IQueryable<Property> AuthorizedProperties(
        AtomicCoreCrudMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability)
    {
        var assignments = AuthorizedAssignments(command, db, now, firstCapability, secondCapability);
        return db.Set<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == command.PortfolioId
                        && selected.PropertyId == property.Id))));
    }

    private async Task EnsurePropertyHasNoCurrentOccupancyAsync(
        int portfolioId, int propertyId, RentalCommandDbContext db, CancellationToken ct)
    {
        var guard = await db.Set<Property>().AsNoTracking()
            .Where(property => property.PortfolioId == portfolioId && property.Id == propertyId)
            .Select(property => new
            {
                Occupied = db.Set<UnitOccupancyProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id && row.IsOccupied),
                Current = db.Set<LeaseManagementLifecycleProjection>().Any(row =>
                    row.PortfolioId == portfolioId && row.PropertyId == property.Id
                    && row.Lifecycle != "Canceled" && row.Lifecycle != "Closed"
                    && row.Lifecycle != "AccountingCloseout"),
            }).SingleAsync(ct);
        if (guard.Occupied) throw Conflict("This property has an occupied unit. Return possession before marking it inactive.");
        if (guard.Current) throw Conflict("This property has a planned or current rental relationship. Cancel or complete it before marking it inactive.");
    }

    private async Task EnsurePropertyHasNoHistoryAsync(
        int portfolioId, int propertyId, RentalCommandDbContext db, CancellationToken ct)
    {
        var guard = await db.Set<Property>().IgnoreQueryFilters().AsNoTracking()
            .Where(property => property.PortfolioId == portfolioId && property.Id == propertyId)
            .Select(property => new
            {
                Occupied = db.Set<UnitOccupancyProjection>().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id && row.IsOccupied),
                Current = db.Set<LeaseManagementLifecycleProjection>().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id && row.Lifecycle != "Canceled" && row.Lifecycle != "Closed" && row.Lifecycle != "AccountingCloseout"),
                Lease = db.Set<LeaseManagement>().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Work = db.Set<WorkOrder>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Appointment = db.Set<Appointment>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Inspection = db.Set<Inspection>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Expense = db.Set<Expense>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Application = db.Set<RentalApplication>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Recurring = db.Set<RecurringExpense>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Loan = db.Set<Loan>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.PropertyId == property.Id),
                Document = db.Set<StoredFile>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.EntityType == nameof(Property) && row.EntityId == property.Id),
            }).SingleAsync(ct);
        if (guard.Occupied) throw Conflict("This property has an occupied unit. Return possession before deleting the property.");
        if (guard.Current) throw Conflict("This property has a planned or current rental relationship. Cancel or complete it before deleting the property.");
        if (guard.Lease) throw Conflict("This property has rental relationship, legal, or financial history and cannot be deleted.");
        if (guard.Work) throw Conflict("This property has work order history. Archive it instead of deleting the property.");
        if (guard.Appointment) throw Conflict("This property has appointment history. Archive it instead of deleting the property.");
        if (guard.Inspection) throw Conflict("This property has inspection history. Archive it instead of deleting the property.");
        if (guard.Expense) throw Conflict("This property has expense history. Archive it instead of deleting the property.");
        if (guard.Application) throw Conflict("This property has application history. Archive it instead of deleting the property.");
        if (guard.Recurring) throw Conflict("This property has recurring expense history. Archive it instead of deleting the property.");
        if (guard.Loan) throw Conflict("This property has loan history. Archive it instead of deleting the property.");
        if (guard.Document) throw Conflict("This property has document history. Archive it instead of deleting the property.");
    }

    private async Task EnsureUnitHasNoHistoryAsync(
        int portfolioId,
        int unitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var guard = await db.Set<Unit>().IgnoreQueryFilters().AsNoTracking()
            .Where(unit => unit.PortfolioId == portfolioId && unit.Id == unitId)
            .Select(unit => new
            {
                IsOccupied = db.Set<UnitOccupancyProjection>().Any(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.UnitId == unit.Id && occupancy.IsOccupied),
                HasCurrent = db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId && lifecycle.UnitId == unit.Id
                    && lifecycle.Lifecycle != "Canceled" && lifecycle.Lifecycle != "Closed"
                    && lifecycle.Lifecycle != "AccountingCloseout"),
                HasLease = db.Set<LeaseManagement>().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasWork = db.Set<WorkOrder>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasAppointment = db.Set<Appointment>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasInspection = db.Set<Inspection>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasExpense = db.Set<Expense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasApplication = db.Set<RentalApplication>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasRecurringExpense = db.Set<RecurringExpense>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasDocument = db.Set<StoredFile>().IgnoreQueryFilters().Any(row =>
                    row.PortfolioId == portfolioId && row.EntityType == nameof(Unit) && row.EntityId == unit.Id),
            })
            .SingleAsync(ct);
        if (guard.IsOccupied) throw Conflict("This unit is occupied. Return possession before deleting the unit.");
        if (guard.HasCurrent) throw Conflict("This unit has a planned or current rental relationship. Cancel or complete it before deleting the unit.");
        if (guard.HasLease) throw Conflict("This unit has rental relationship, legal, or financial history and cannot be deleted.");
        if (guard.HasWork) throw Conflict("This unit has work order history. Archive the work order history instead of deleting the unit.");
        if (guard.HasAppointment) throw Conflict("This unit has appointment history. Archive the appointment history instead of deleting the unit.");
        if (guard.HasInspection) throw Conflict("This unit has inspection history. Archive the inspection history instead of deleting the unit.");
        if (guard.HasExpense) throw Conflict("This unit has expense history. Archive the expense history instead of deleting the unit.");
        if (guard.HasApplication) throw Conflict("This unit has application history. Archive the applications instead of deleting the unit.");
        if (guard.HasRecurringExpense) throw Conflict("This unit has recurring expense history. Archive the recurring expense history instead of deleting the unit.");
        if (guard.HasDocument) throw Conflict("This unit has document history. Archive the documents instead of deleting the unit.");
    }


    private async Task<string> SnapshotPropertyAsync(
        Property entity,
        RentalCommandDbContext db,
        CancellationToken ct,
        DateTime? effectiveNowUtc = null)
    {
        var now = effectiveNowUtc ?? await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(db, ct);
        var facts = await db.Set<Property>().AsNoTracking()
            .Where(property => property.PortfolioId == entity.PortfolioId && property.Id == entity.Id)
            .Select(property => new
            {
                Ownerships = property.Ownerships
                    .Where(ownership => ownership.EffectiveFromUtc <= now
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))
                    .OrderBy(ownership => ownership.OwnerEntityId)
                    .Select(ownership => new PropertyOwnershipResponse
                    {
                        Id = ownership.Id,
                        OwnerEntityId = ownership.OwnerEntityId,
                        OwnerName = ownership.OwnerEntity!.Name,
                        OwnershipSharePercent = ownership.OwnershipSharePercent,
                        EffectiveFromUtc = ownership.EffectiveFromUtc,
                        EffectiveToUtc = ownership.EffectiveToUtc,
                        StatementRecipientName = ownership.StatementRecipientName,
                        StatementRecipientEmail = ownership.StatementRecipientEmail,
                        PayeeName = ownership.PayeeName,
                    }).ToList(),
                UnitCount = property.Units.Count,
                OccupiedUnits = db.Set<UnitOccupancyProjection>().Count(occupancy =>
                    occupancy.PortfolioId == entity.PortfolioId
                    && occupancy.PropertyId == property.Id && occupancy.IsOccupied),
            }).SingleAsync(ct);
        var response = PropertyResponse.FromEntity(entity, facts.UnitCount, facts.OccupiedUnits);
        response.Ownerships = facts.Ownerships;
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotOwnerAsync(
        OwnerEntity entity,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(db, ct);
        var assigned = await db.Set<PropertyOwnership>().AsNoTracking()
            .Where(ownership =>
                ownership.PortfolioId == entity.PortfolioId
                && ownership.OwnerEntityId == entity.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null)
            .Select(ownership => ownership.PropertyId)
            .Distinct()
            .CountAsync(ct);
        return JsonSerializer.Serialize(OwnerEntityResponse.FromEntity(entity, assigned));
    }

    private async Task<string> SnapshotTenantAsync(
        Tenant entity,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var counts = await db.Set<Tenant>().AsNoTracking()
            .Where(tenant => tenant.PortfolioId == entity.PortfolioId && tenant.Id == entity.Id)
            .Select(tenant => new
            {
                Active = db.Set<LeaseManagementParty>()
                    .Where(party => party.PortfolioId == entity.PortfolioId
                        && party.TenantId == tenant.Id
                        && party.Role != LeaseManagementPartyRole.Guarantor
                        && db.Set<UnitOccupancyProjection>().Any(occupancy =>
                            occupancy.PortfolioId == entity.PortfolioId
                            && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                        && db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                            lifecycle.PortfolioId == entity.PortfolioId
                            && lifecycle.LeaseManagementId == party.LeaseManagementId
                            && party.EffectiveFrom <= lifecycle.BusinessDate
                            && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)))
                    .Select(party => party.LeaseManagementId).Distinct().Count(),
                History = db.Set<LeaseManagementParty>()
                    .Where(party => party.PortfolioId == entity.PortfolioId && party.TenantId == tenant.Id)
                    .Select(party => party.LeaseManagementId).Distinct().Count(),
            }).SingleAsync(ct);
        return SnapshotTenant(entity, counts.Active, counts.History);
    }

    private string SnapshotTenant(Tenant entity, int active, int history)
    {
        var response = TenantResponse.FromEntity(entity);
        response.ActiveLeaseCount = active;
        response.LeaseHistoryCount = history;
        response.CanDelete = active == 0 && history == 0;
        response.DeleteBlockedReason = active > 0
            ? "This tenant is a current resident in an occupied rental; return possession or change the household first."
            : history > 0
                ? "This tenant has rental relationship history; keep the tenant record to preserve agreements and account history."
                : null;
        return JsonSerializer.Serialize(response);
    }

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
        AtomicCoreCrudMutationCommand command,
        string entityType,
        int entityId,
        DateTime now,
        string suffix = "entity",
        bool deleted = false) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = deleted ? "delete" : "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private AtomicSemanticAudit Audit(
        AtomicCoreCrudMutationCommand command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int? entityId = null) => new(
            command.PortfolioId, entityType, entityId ?? command.EntityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private T Read<T>(AtomicCoreCrudMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Core CRUD mutation request payload is invalid.");

    private string? NormalizeOptionalText(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private void Validate(AtomicCoreCrudMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128
            || (command.Operation == AtomicCoreCrudMutationOperation.Setup && command.EntityId < 0)
            || (command.Operation is not AtomicCoreCrudMutationOperation.Create
                    and not AtomicCoreCrudMutationOperation.Setup && command.EntityId <= 0))
            throw new ArgumentException("Portfolio, actor, access revision, operation, and delivery identifiers are required.");
    }

    private DateTime? Utc(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
    private AtomicCoreCrudMutationResult Missing() => new(false, false, 0);
    private AtomicCoreCrudMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, responseJson);
    private UnauthorizedAccessException Denied() => new(
        "The record is not authorized in the current workspace scope.");
    private DomainValidationException Conflict(string message) => new(message, 409);
}

public static class AtomicCoreCrudMutation
{
    public static readonly AtomicJsonResultCodec<AtomicCoreCrudMutationResult> Codec =
        new("rental.core-crud-mutation.v1");

    public static AtomicCoreCrudMutationCommand Command<TRequest>(
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

    public static AtomicCommandIdentity Identity(AtomicCoreCrudMutationCommand command) => new(
        $"rental.{command.Domain.ToString().ToLowerInvariant()}.{command.Operation.ToString().ToLowerInvariant()}",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
        $"{command.EntityId}:{command.DeliveryIdempotencyKey}");
}
