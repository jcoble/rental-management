using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public static class EvictionCrudWriteSupport
{
    public const string ResultContract = "eviction-case.mutation.v1";

    public static string IdempotencyKey(string deliveryIdempotencyKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryIdempotencyKey)));

    public static TransactionalWrite<CreateEvictionCaseCommand, OperationMutationResult> Write(
        CreateEvictionCaseCommand command,
        Func<CreateEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task<OperationMutationResult>> executeAsync,
        Func<CreateEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
    {
        EvictionOperationValidation.Validate(command);
        return new("eviction-case.create",  command, ResultContract,
            AuthorizationPlan(command.PortfolioId, command.Actor), executeAsync, authorizeReplayAsync);
    }

    public static TransactionalWrite<UpdateEvictionCaseCommand, OperationMutationResult> Write(
        UpdateEvictionCaseCommand command,
        Func<UpdateEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task<OperationMutationResult>> executeAsync,
        Func<UpdateEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
    {
        EvictionOperationValidation.Validate(command);
        return new("eviction-case.update",  command, ResultContract,
            AuthorizationPlan(command.PortfolioId, command.Actor), executeAsync, authorizeReplayAsync);
    }

    public static TransactionalWrite<AddEvictionCaseEventCommand, OperationMutationResult> Write(
        AddEvictionCaseEventCommand command,
        Func<AddEvictionCaseEventCommand, IAtomicCommandContext, CancellationToken, Task<OperationMutationResult>> executeAsync,
        Func<AddEvictionCaseEventCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
    {
        EvictionOperationValidation.Validate(command);
        return new("eviction-case.event.create",  command, ResultContract,
            AuthorizationPlan(command.PortfolioId, command.Actor), executeAsync, authorizeReplayAsync);
    }

    public static TransactionalWrite<DeleteEvictionCaseCommand, OperationMutationResult> Write(
        DeleteEvictionCaseCommand command,
        Func<DeleteEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task<OperationMutationResult>> executeAsync,
        Func<DeleteEvictionCaseCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
    {
        EvictionOperationValidation.Validate(command);
        return new("eviction-case.delete",  command, ResultContract,
            AuthorizationPlan(command.PortfolioId, command.Actor), executeAsync, authorizeReplayAsync);
    }

    private static WriteLockPlan AuthorizationPlan(int portfolioId, StaffOperationActor actor) =>
        new(WriteLockProtocol.AuthorizationScope,
            actor.AuthSessionId,
            actor.AccessContextId,
            portfolioId);

}

public sealed class CreateEvictionCaseRule
{
    public static async Task<OperationMutationResult> ExecuteAsync(
        RentalCommandDbContext db, CreateEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var respondentIds = command.RespondentLeaseManagementPartyIds.Distinct().ToArray();
        var managementContext = await EvictionOperationAuthorization.AuthorizedManagements(
                command.PortfolioId, command.Actor, db, businessNow, securityNow)
            .Where(management => management.Id == command.LeaseManagementId)
            .Select(management => new
            {
                management.PropertyId,
                management.UnitId,
                AgreementValid = !command.LeaseAgreementId.HasValue ||
                    db.Set<LeaseAgreement>().Any(agreement =>
                        agreement.Id == command.LeaseAgreementId.Value &&
                        agreement.PortfolioId == command.PortfolioId &&
                        agreement.LeaseManagementId == management.Id),
                RespondentCount = db.Set<LeaseManagementParty>().Count(party =>
                    respondentIds.Contains(party.Id) && party.PortfolioId == command.PortfolioId &&
                    party.LeaseManagementId == management.Id),
            })
            .SingleOrDefaultAsync(ct);
        if (managementContext is null || !managementContext.AgreementValid ||
            managementContext.RespondentCount != respondentIds.Length)
            return new(OperationMutationOutcome.NotFound, 0);

        var eventDate = command.FiledOnDateUtc?.Date ?? businessNow.Date;
        var entity = new EvictionCase
        {
            PortfolioId = command.PortfolioId,
            LeaseManagementId = command.LeaseManagementId,
            LeaseAgreementId = command.LeaseAgreementId,
            PropertyId = managementContext.PropertyId,
            UnitId = managementContext.UnitId,
            Status = command.Status,
            FiledOnDate = command.FiledOnDateUtc?.Date ??
                (command.Status >= EvictionCaseStatus.Filed ? eventDate : null),
            HearingDate = command.HearingDateUtc?.Date,
            CourtName = EvictionOperationValidation.Normalize(command.CourtName),
            CaseNumber = EvictionOperationValidation.Normalize(command.CaseNumber),
            Notes = EvictionOperationValidation.Normalize(command.Notes),
            CreatedAt = businessNow,
            UpdatedAt = businessNow,
        };
        foreach (var partyId in respondentIds)
        {
            entity.Respondents.Add(new EvictionCaseRespondent
            {
                PortfolioId = command.PortfolioId,
                LeaseManagementPartyId = partyId,
            });
        }

        var initialType = EvictionOperationValidation.InitialEventForStatus(command.Status);
        if (initialType.HasValue)
        {
            entity.Events.Add(new EvictionCaseEvent
            {
                PortfolioId = command.PortfolioId,
                EventType = initialType.Value,
                EventDate = eventDate,
                Notes = entity.Notes ?? "Case opened.",
                CreatedAt = businessNow,
                UpdatedAt = businessNow,
            });
        }

        db.Add(entity);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, EvictionOperationAudit.Case(
            command.PortfolioId, 0, AuditLogOperation.Created, command.Actor.UserId,
            entity.Status, entity.CaseNumber, "Created eviction case."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await EvictionCaseSnapshot.LoadAsync(
            db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(CreateWorkOrderRule.DataUpdate(
            command.PortfolioId, nameof(EvictionCase), entity.Id,
            $"eviction-case-create:{command.DeliveryIdempotencyKey}", businessNow));
        foreach (var evt in entity.Events)
        {
            context.StageOutbox(CreateWorkOrderRule.DataUpdate(
                command.PortfolioId, nameof(EvictionCaseEvent), evt.Id,
                $"eviction-event-create:{command.DeliveryIdempotencyKey}:{evt.Id}", businessNow));
        }
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, CreateEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await EvictionOperationAuthorization.AuthorizedManagements(
                command.PortfolioId, command.Actor, db, command.BusinessNowUtc, securityNow)
            .AnyAsync(management => management.Id == command.LeaseManagementId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this eviction case.");
    }
}

public sealed class UpdateEvictionCaseRule
{
    public static async Task<OperationMutationResult> ExecuteAsync(
        RentalCommandDbContext db, UpdateEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await EvictionOperationAuthorization.AuthorizedCases(
                command.PortfolioId, command.Actor, db,
                businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.EvictionCaseId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.EvictionCaseId);

        if (command.Status.HasValue) entity.Status = command.Status.Value;
        if (command.FiledOnDateUtc.HasValue) entity.FiledOnDate = command.FiledOnDateUtc.Value.Date;
        if (command.HearingDateUtc.HasValue) entity.HearingDate = command.HearingDateUtc.Value.Date;
        if (command.ResolvedOnDateUtc.HasValue) entity.ResolvedOnDate = command.ResolvedOnDateUtc.Value.Date;
        if (command.CourtName is not null) entity.CourtName = EvictionOperationValidation.Normalize(command.CourtName);
        if (command.CaseNumber is not null) entity.CaseNumber = EvictionOperationValidation.Normalize(command.CaseNumber);
        if (command.Resolution is not null) entity.Resolution = EvictionOperationValidation.Normalize(command.Resolution);
        if (command.Notes is not null) entity.Notes = EvictionOperationValidation.Normalize(command.Notes);
        entity.UpdatedAt = businessNow;

        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, EvictionOperationAudit.Case(
            command.PortfolioId, entity.Id, AuditLogOperation.Updated, command.Actor.UserId,
            entity.Status, entity.CaseNumber, "Updated eviction case."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await EvictionCaseSnapshot.LoadAsync(
            db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(CreateWorkOrderRule.DataUpdate(
            command.PortfolioId, nameof(EvictionCase), entity.Id,
            $"eviction-case-update:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, UpdateEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await EvictionOperationAuthorization.AuthorizedCases(
                command.PortfolioId, command.Actor, db,
                command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.EvictionCaseId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this eviction case.");
    }
}

public sealed class AddEvictionCaseEventRule
{
    public static async Task<OperationMutationResult> ExecuteAsync(
        RentalCommandDbContext db, AddEvictionCaseEventCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await EvictionOperationAuthorization.AuthorizedCases(
                command.PortfolioId, command.Actor, db,
                businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.EvictionCaseId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.EvictionCaseId);

        var evt = new EvictionCaseEvent
        {
            PortfolioId = command.PortfolioId,
            EvictionCaseId = entity.Id,
            EventType = command.EventType,
            EventDate = command.EventDateUtc.Date,
            Notes = EvictionOperationValidation.Normalize(command.Notes),
            CreatedAt = businessNow,
            UpdatedAt = businessNow,
        };
        db.Add(evt);
        EvictionOperationValidation.ApplyEventToCase(entity, evt, businessNow);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(evt, new AtomicSemanticAudit(
            command.PortfolioId, nameof(EvictionCaseEvent), 0, AuditLogOperation.Created,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                evt.EvictionCaseId, evt.EventType, evt.EventDate,
            }), ChangeReason: "Added eviction case event."));
        context.BindSemanticAudit(entity, EvictionOperationAudit.Case(
            command.PortfolioId, entity.Id, AuditLogOperation.Updated, command.Actor.UserId,
            entity.Status, entity.CaseNumber, "Applied eviction case event."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await EvictionCaseSnapshot.LoadAsync(
            db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(CreateWorkOrderRule.DataUpdate(
            command.PortfolioId, nameof(EvictionCaseEvent), evt.Id,
            $"eviction-event-create:{command.DeliveryIdempotencyKey}", businessNow));
        context.StageOutbox(CreateWorkOrderRule.DataUpdate(
            command.PortfolioId, nameof(EvictionCase), entity.Id,
            $"eviction-case-event:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, AddEvictionCaseEventCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await EvictionOperationAuthorization.AuthorizedCases(
                command.PortfolioId, command.Actor, db,
                command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.EvictionCaseId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this eviction case.");
    }
}

public sealed class DeleteEvictionCaseRule
{
    public static async Task<OperationMutationResult> ExecuteAsync(
        RentalCommandDbContext db, DeleteEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await EvictionOperationAuthorization.AuthorizedCases(
                command.PortfolioId, command.Actor, db,
                businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.EvictionCaseId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.EvictionCaseId);
        entity.DeletedAt = businessNow;
        entity.UpdatedAt = businessNow;
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, EvictionOperationAudit.Case(
            command.PortfolioId, entity.Id, AuditLogOperation.Deleted, command.Actor.UserId,
            entity.Status, entity.CaseNumber, "Deleted eviction case."));
        context.StageOutbox(CreateWorkOrderRule.DataUpdate(
            command.PortfolioId, nameof(EvictionCase), entity.Id,
            $"eviction-case-delete:{command.DeliveryIdempotencyKey}", businessNow, operation: "delete"));
        return new(OperationMutationOutcome.Applied, entity.Id);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, DeleteEvictionCaseCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        EvictionOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var propertyId = await db.Set<EvictionCase>().IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == command.EvictionCaseId && item.PortfolioId == command.PortfolioId)
            .Select(item => (int?)item.PropertyId)
            .SingleOrDefaultAsync(ct);
        if (!propertyId.HasValue || !await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId, command.Actor, propertyId.Value, CapabilityKeys.RentalsManage,
                db, command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this eviction case.");
    }
}

internal static class EvictionOperationAuthorization
{
    internal static IQueryable<LeaseManagement> AuthorizedManagements(
        int portfolioId, StaffOperationActor actor, RentalCommandDbContext db,
        DateTime businessNow, DateTime securityNow)
    {
        var properties = StaffOperationAuthorization.AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.RentalsManage, db, businessNow, securityNow);
        return db.Set<LeaseManagement>().AsNoTracking().Where(management =>
            management.PortfolioId == portfolioId &&
            properties.Any(property => property.Id == management.PropertyId));
    }

    internal static IQueryable<EvictionCase> AuthorizedCases(
        int portfolioId, StaffOperationActor actor, RentalCommandDbContext db,
        DateTime businessNow, DateTime securityNow, bool tracking)
    {
        var properties = StaffOperationAuthorization.AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.RentalsManage, db, businessNow, securityNow);
        var query = db.Set<EvictionCase>().Where(item =>
            item.PortfolioId == portfolioId && properties.Any(property => property.Id == item.PropertyId));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }
}

internal static class EvictionOperationValidation
{
    internal static void Validate(CreateEvictionCaseCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.LeaseManagementId <= 0 || command.RespondentLeaseManagementPartyIds.Length == 0 ||
            command.RespondentLeaseManagementPartyIds.Any(id => id <= 0))
            throw new DomainValidationException("A lease relationship and respondent are required.");
    }

    internal static void Validate(UpdateEvictionCaseCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        ValidateIdentity(command.EvictionCaseId);
    }

    internal static void Validate(AddEvictionCaseEventCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        ValidateIdentity(command.EvictionCaseId);
        if (command.EventDateUtc == default)
            throw new DomainValidationException("An eviction event date is required.");
    }

    internal static void Validate(DeleteEvictionCaseCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        ValidateIdentity(command.EvictionCaseId);
    }

    private static void ValidateCommon(int portfolioId, StaffOperationActor actor, string key)
    {
        if (portfolioId <= 0 || actor.UserId <= 0 || actor.AuthSessionId == Guid.Empty ||
            actor.AccessContextId <= 0 || actor.AccessRevision <= 0 || string.IsNullOrWhiteSpace(key))
            throw new DomainValidationException("A complete eviction command scope is required.");
    }

    private static void ValidateIdentity(int id)
    {
        if (id <= 0) throw new DomainValidationException("Eviction case identity is invalid.");
    }

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static EvictionEventType? InitialEventForStatus(EvictionCaseStatus status) => status switch
    {
        EvictionCaseStatus.NoticeServed => EvictionEventType.NoticeServed,
        EvictionCaseStatus.Filed => EvictionEventType.Filed,
        EvictionCaseStatus.HearingScheduled => EvictionEventType.HearingScheduled,
        EvictionCaseStatus.Judgment => EvictionEventType.Judgment,
        EvictionCaseStatus.MoveOut => EvictionEventType.MoveOut,
        EvictionCaseStatus.Settled => EvictionEventType.Settlement,
        EvictionCaseStatus.Dismissed => EvictionEventType.Dismissal,
        _ => null,
    };

    internal static void ApplyEventToCase(EvictionCase entity, EvictionCaseEvent evt, DateTime now)
    {
        switch (evt.EventType)
        {
            case EvictionEventType.NoticeServed: entity.Status = EvictionCaseStatus.NoticeServed; break;
            case EvictionEventType.Filed: entity.Status = EvictionCaseStatus.Filed; entity.FiledOnDate ??= evt.EventDate; break;
            case EvictionEventType.HearingScheduled: entity.Status = EvictionCaseStatus.HearingScheduled; entity.HearingDate = evt.EventDate; break;
            case EvictionEventType.Judgment: entity.Status = EvictionCaseStatus.Judgment; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Judgment"; break;
            case EvictionEventType.MoveOut: entity.Status = EvictionCaseStatus.MoveOut; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Move-out completed"; break;
            case EvictionEventType.Settlement: entity.Status = EvictionCaseStatus.Settled; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Settlement"; break;
            case EvictionEventType.Dismissal: entity.Status = EvictionCaseStatus.Dismissed; entity.ResolvedOnDate ??= evt.EventDate; entity.Resolution = Normalize(evt.Notes) ?? "Dismissed"; break;
            case EvictionEventType.PaymentPlan:
            case EvictionEventType.Note:
                break;
        }
        entity.UpdatedAt = now;
    }
}

internal static class EvictionOperationAudit
{
    internal static AtomicSemanticAudit Case(int portfolioId, int id, AuditLogOperation operation,
        int userId, EvictionCaseStatus status, string? caseNumber, string reason) => new(
        portfolioId, nameof(EvictionCase), id, operation, userId,
        NewValues: JsonSerializer.Serialize(new { Status = status, CaseNumber = caseNumber }),
        ChangeReason: reason);
}

internal static class EvictionCaseSnapshot
{
    internal static async Task<string> LoadAsync(RentalCommandDbContext db,
        int portfolioId, int id, CancellationToken ct)
    {
        var row = await db.Set<EvictionCase>().AsNoTracking()
            .Where(item => item.Id == id && item.PortfolioId == portfolioId)
            .Select(item => new
            {
                item.Id,
                item.PortfolioId,
                item.LeaseManagementId,
                RelationshipNumber = item.LeaseManagement!.RelationshipNumber,
                item.LeaseAgreementId,
                AgreementNumber = item.LeaseAgreement == null ? null : item.LeaseAgreement.AgreementNumber,
                item.PropertyId,
                PropertyName = item.Property!.Name,
                item.UnitId,
                UnitNumber = item.Unit!.UnitNumber,
                Respondents = item.Respondents
                    .OrderBy(response => response.LeaseManagementParty!.Tenant!.LastName)
                    .ThenBy(response => response.LeaseManagementParty!.Tenant!.FirstName)
                    .Select(response => new
                    {
                        response.LeaseManagementPartyId,
                        TenantId = response.LeaseManagementParty!.TenantId,
                        TenantName = (response.LeaseManagementParty.Tenant!.FirstName + " " +
                            response.LeaseManagementParty.Tenant.LastName).Trim(),
                    }).ToList(),
                item.Status,
                item.FiledOnDate,
                item.HearingDate,
                item.ResolvedOnDate,
                item.CourtName,
                item.CaseNumber,
                item.Resolution,
                item.Notes,
                EventCount = item.Events.Count,
                LatestEventDate = item.Events.Select(evt => (DateTime?)evt.EventDate).Max(),
                Events = item.Events.OrderBy(evt => evt.EventDate).ThenBy(evt => evt.Id)
                    .Select(evt => new
                    {
                        evt.Id,
                        evt.PortfolioId,
                        evt.EvictionCaseId,
                        evt.EventType,
                        evt.EventDate,
                        evt.Notes,
                        evt.CreatedAt,
                        evt.UpdatedAt,
                    }).ToList(),
                item.CreatedAt,
                item.UpdatedAt,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(row);
    }
}
