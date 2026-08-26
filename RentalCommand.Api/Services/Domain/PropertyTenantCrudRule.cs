using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Policies;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Policies;
using RentalCommand.Api.Services;

namespace RentalCommand.Api.Services.Domain;

internal sealed class PropertyTenantCrudRule
{
    private readonly RentalCommandDbContext _db;

    public PropertyTenantCrudRule(RentalCommandDbContext db) => _db = db;

    public Task<AtomicCoreCrudMutationResult> SetupPropertyAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Property, AtomicCoreCrudMutationOperation.Setup, ct);

    public Task<AtomicCoreCrudMutationResult> RejectPropertyCreateAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Property, AtomicCoreCrudMutationOperation.Create, ct);

    public Task<AtomicCoreCrudMutationResult> UpdatePropertyAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Property, AtomicCoreCrudMutationOperation.Update, ct);

    public Task<AtomicCoreCrudMutationResult> DeletePropertyAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Property, AtomicCoreCrudMutationOperation.Delete, ct);

    public Task<AtomicCoreCrudMutationResult> CreateTenantAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Tenant, AtomicCoreCrudMutationOperation.Create, ct);

    public Task<AtomicCoreCrudMutationResult> UpdateTenantAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Tenant, AtomicCoreCrudMutationOperation.Update, ct);

    public Task<AtomicCoreCrudMutationResult> DeleteTenantAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, CancellationToken ct) =>
        ExecuteAsync(command, attempt,
            AtomicCoreCrudMutationDomain.Tenant, AtomicCoreCrudMutationOperation.Delete, ct);

    private async Task<AtomicCoreCrudMutationResult> ExecuteAsync(
        CoreCrudWriteRequest command,
        IAtomicCommandContext attempt,
        AtomicCoreCrudMutationDomain expectedDomain,
        AtomicCoreCrudMutationOperation expectedOperation,
        CancellationToken ct)
    {
        Validate(command);
        if (command.Domain != expectedDomain || command.Operation != expectedOperation)
            throw new ArgumentException("That core CRUD action is not supported.");
        var now = await attempt.ReadDatabaseClockUtcAsync(ct);
        return command.Domain == AtomicCoreCrudMutationDomain.Property
            ? await MutatePropertyAsync(command, attempt, now, ct)
            : await MutateTenantAsync(command, attempt, now, ct);
    }

    public async Task AuthorizeReplayAsync(
        CoreCrudWriteRequest command,
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
            AtomicCoreCrudMutationDomain.Tenant when command.Operation == AtomicCoreCrudMutationOperation.Create =>
                await AuthorizeAllPropertiesEitherAsync(command, _db, now,
                    CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage, ct),
            AtomicCoreCrudMutationDomain.Tenant =>
                await AuthorizeTenantAsync(command, _db, now, ct),
            _ => false,
        };
        if (!authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private async Task<AtomicCoreCrudMutationResult> MutatePropertyAsync(
        CoreCrudWriteRequest command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        const string entityType = nameof(Property);
        var db = _db;
        if (command.Operation == AtomicCoreCrudMutationOperation.Setup)
            return await ApplyPropertySetupAsync(command, attempt, now, ct);

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
            throw new ArgumentException("That property action is not supported.");
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

    private async Task<AtomicCoreCrudMutationResult> ApplyPropertySetupAsync(
        CoreCrudWriteRequest command,
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
            var primaryOwnerId = await EnsureSelfOwnerAsync(command, attempt, now, ct);
            requestedOwnerships = [new PropertyOwnershipRequest { OwnerEntityId = primaryOwnerId }];
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
        if (setup.Units.Any(unit => ResidentialUnitPolicy.IsInvalidForCreate(
                request.PropertyType, unit.Bedrooms, unit.Bathrooms)))
            throw new DomainValidationException(
                ResidentialUnitPolicy.RequiredDetailsMessage);

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
            Bedrooms = unit.Bedrooms ?? 0m,
            Bathrooms = unit.Bathrooms ?? 0m,
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
            Units = currentUnits.Select(unit => ToUnitResponse(unit, property.PropertyType)).ToList(),
        };
        return Applied(property.Id, JsonSerializer.Serialize(response));
    }

    private void ValidateSetup(SetupPropertyRequest setup, int commandPropertyId)
    {
        if (setup.PropertyId.GetValueOrDefault() != commandPropertyId)
            throw new ArgumentException("The setup Property id does not match the command target.");
        if (!Enum.IsDefined(setup.Property.RentalStructure))
            throw new ArgumentException("Rental setup must be single-rental or multi-rental.");
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
            throw new ArgumentException("Each ownership row must name a different owner.");
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
                "One or more owners are missing or outside this workspace.");

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

    private async Task<int> EnsureSelfOwnerAsync(
        CoreCrudWriteRequest command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        // Serialize the self-owner get-or-create independently of the broader portfolio mutation
        // lock. The partial unique index below is the database backstop for any other writer, while
        // this transaction-scoped lock makes concurrent first-property retries reuse the committed
        // owner instead of racing an insert.
        await attempt.AcquireLockAsync("PortfolioPrimaryOwner", command.PortfolioId, ct);
        var existingPrimaryId = await db.Set<OwnerEntity>().AsNoTracking()
            .Where(owner => owner.PortfolioId == command.PortfolioId
                && owner.IsPrimary && owner.DeletedAt == null)
            .Select(owner => (int?)owner.Id)
            .SingleOrDefaultAsync(ct);
        if (existingPrimaryId.HasValue)
            return existingPrimaryId.Value;

        var actor = await db.Set<ApplicationUser>().AsNoTracking()
            .Where(user => user.Id == command.ActorUserId)
            .Select(user => new { user.DisplayName, user.Email })
            .SingleOrDefaultAsync(ct)
            ?? throw new AtomicReceiptInvariantException("The landlord account is required to create a self-owner record.");
        var fallbackName = actor.Email?.Split('@', 2)[0].Trim();
        var ownerName = string.IsNullOrWhiteSpace(actor.DisplayName)
            ? (string.IsNullOrWhiteSpace(fallbackName) ? "Property owner" : fallbackName)
            : actor.DisplayName.Trim();
        var owner = new OwnerEntity
        {
            PortfolioId = command.PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = ownerName,
            Email = actor.Email,
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(owner);
        attempt.BindSemanticAudit(owner, Audit(command, nameof(OwnerEntity),
            AuditLogOperation.Created, "Self-owner record created during Property setup", entityId: 0));
        await attempt.FlushBusinessAsync(ct);

        db.Add(new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            AccessContextId = command.AccessContextId,
            ApplicationUserId = command.ActorUserId,
            OwnerEntityId = owner.Id,
            EffectiveFromUtc = now,
            GrantedAtUtc = now,
            GrantedByUserId = command.ActorUserId,
            Reason = "Self-owner relationship created during Property setup",
        });
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(OwnerEntity), owner.Id, now, "self-owner");
        return owner.Id;
    }

    private async Task<OwnershipLifecycleChange> ReplaceCurrentOwnershipsAsync(
        CoreCrudWriteRequest command,
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
        CoreCrudWriteRequest command,
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

    private static UnitResponse ToUnitResponse(Unit unit, PropertyType propertyType) => new()
    {
        Id = unit.Id,
        PropertyId = unit.PropertyId,
        UnitNumber = unit.UnitNumber,
        FloorPlan = unit.FloorPlan,
        PropertyType = propertyType,
        Bedrooms = unit.Bedrooms,
        Bathrooms = unit.Bathrooms,
        SquareFeet = unit.SquareFeet,
        MarketRent = unit.MarketRent,
        Status = DerivedUnitStatus.Vacant,
        Notes = unit.Notes,
        CreatedAt = unit.CreatedAt,
        UpdatedAt = unit.UpdatedAt,
    };

    private async Task<AtomicCoreCrudMutationResult> MutateTenantAsync(
        CoreCrudWriteRequest command, IAtomicCommandContext attempt, DateTime now, CancellationToken ct)
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
            var state = await db.TenantDeleteEligibility(
                command.PortfolioId, tenant.Id).SingleAsync(ct);
            var blockedReason = DeleteEligibilityPolicy.TenantBlockedReason(
                state.ActiveLeaseCount, state.LeaseHistoryCount);
            if (blockedReason is not null) throw Conflict(blockedReason);
            tenant.DeletedAt = mutationNow;
            tenant.UpdatedAt = mutationNow;
            attempt.BindSemanticAudit(tenant, Audit(command, nameof(Tenant), AuditLogOperation.Deleted,
                $"Tenant {tenant.FirstName} {tenant.LastName} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, mutationNow, deleted: true);
            return Applied(tenant.Id);
        }
        if (command.Operation != AtomicCoreCrudMutationOperation.Update)
            throw new ArgumentException("That tenant action is not supported.");
        var update = Read<UpdateTenantRequest>(command);
        if (update.FirstName is not null) tenant.FirstName = update.FirstName;
        if (update.LastName is not null) tenant.LastName = update.LastName;
        if (update.ClearEmail) tenant.Email = null;
        else if (update.Email is not null) tenant.Email = update.Email;
        if (update.ClearPhone) tenant.Phone = null;
        else if (update.Phone is not null) tenant.Phone = update.Phone;
        if (update.ClearEmergencyContact) tenant.EmergencyContact = null;
        else if (update.EmergencyContact is not null) tenant.EmergencyContact = update.EmergencyContact;
        if (update.DateOfBirth.HasValue) tenant.DateOfBirth = Utc(update.DateOfBirth);
        if (update.Notes is not null) tenant.Notes = update.Notes;
        tenant.UpdatedAt = mutationNow;
        attempt.BindSemanticAudit(tenant, Audit(command, nameof(Tenant), AuditLogOperation.Updated,
            $"Tenant {tenant.FirstName} {tenant.LastName} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, mutationNow);
        return Applied(tenant.Id, await SnapshotTenantAsync(tenant, db, ct));
    }

    private Task<bool> AuthorizeAllPropertiesAsync(
        CoreCrudWriteRequest command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CancellationToken ct) =>
        AuthorizeAllPropertiesEitherAsync(command, db, now, capability, capability, ct);

    private Task<bool> AuthorizeAllPropertiesEitherAsync(
        CoreCrudWriteRequest command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability,
        CancellationToken ct) =>
        AuthorizedAssignments(command, db, now, firstCapability, secondCapability)
            .AnyAsync(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);

    private Task<bool> AuthorizePropertyEntityAsync(
        CoreCrudWriteRequest command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CancellationToken ct) =>
        AuthorizedProperties(command, db, now, capability, capability)
            .AnyAsync(property => property.Id == command.EntityId, ct);

    private async Task<bool> AuthorizeTenantAsync(
        CoreCrudWriteRequest command,
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
        CoreCrudWriteRequest command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability) =>
        db.AuthorizedAssignmentsForScope(
            Scope(command),
            [firstCapability, secondCapability],
            CapabilityAuthorizationTargetKind.Property,
            now);

    private IQueryable<Property> AuthorizedProperties(
        CoreCrudWriteRequest command,
        RentalCommandDbContext db,
        DateTime now,
        string firstCapability,
        string secondCapability)
    {
        return db.Set<Property>().AsNoTracking().WhereAuthorizedForScope(
            db,
            Scope(command),
            [firstCapability, secondCapability],
            now);
    }

    private static WorkspaceReadScope Scope(CoreCrudWriteRequest command) => new(
        command.PortfolioId,
        command.ActorUserId,
        command.AuthSessionId,
        command.AccessContextId,
        command.ExpectedAccessRevision);

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
        var guard = await db.PropertyDeleteEligibility(portfolioId, propertyId).SingleAsync(ct);
        if (guard.HasOccupiedUnit) throw Conflict("This property has an occupied unit. Return possession before deleting the property.");
        if (guard.HasPlannedOrCurrentRelationship) throw Conflict("This property has a planned or current rental relationship. Cancel or complete it before deleting the property.");
        if (guard.HasRentalRelationshipHistory) throw Conflict("This property has rental relationship, legal, or financial history and cannot be deleted.");
        if (guard.HasWorkOrderHistory) throw Conflict("This property has work order history. Archive it instead of deleting the property.");
        if (guard.HasAppointmentHistory) throw Conflict("This property has appointment history. Archive it instead of deleting the property.");
        if (guard.HasInspectionHistory) throw Conflict("This property has inspection history. Archive it instead of deleting the property.");
        if (guard.HasExpenseHistory) throw Conflict("This property has expense history. Archive it instead of deleting the property.");
        if (guard.HasApplicationHistory) throw Conflict("This property has application history. Archive it instead of deleting the property.");
        if (guard.HasRecurringExpenseHistory) throw Conflict("This property has recurring expense history. Archive it instead of deleting the property.");
        if (guard.HasLoanHistory) throw Conflict("This property has loan history. Archive it instead of deleting the property.");
        if (guard.HasDocumentHistory) throw Conflict("This property has document history. Archive it instead of deleting the property.");
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

    private async Task<string> SnapshotTenantAsync(
        Tenant entity,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var eligibility = await db.TenantDeleteEligibility(
            entity.PortfolioId, entity.Id).SingleAsync(ct);
        return SnapshotTenant(
            entity, eligibility.ActiveLeaseCount, eligibility.LeaseHistoryCount);
    }

    private string SnapshotTenant(Tenant entity, int active, int history)
    {
        var response = TenantResponse.FromEntity(entity);
        response.ActiveLeaseCount = active;
        response.LeaseHistoryCount = history;
        response.CanDelete = DeleteEligibilityPolicy.CanDeleteTenant(active, history);
        response.DeleteBlockedReason = DeleteEligibilityPolicy.TenantBlockedReason(active, history);
        return JsonSerializer.Serialize(response);
    }

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
        CoreCrudWriteRequest command,
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
        CoreCrudWriteRequest command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int? entityId = null) => new(
            command.PortfolioId, entityType, entityId ?? command.EntityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private T Read<T>(CoreCrudWriteRequest command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("The record update is invalid.");

    private string? NormalizeOptionalText(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private void Validate(CoreCrudWriteRequest command)
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
