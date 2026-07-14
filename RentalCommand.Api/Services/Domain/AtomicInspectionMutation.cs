using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicInspectionMutationDomain { Template, Inspection, Item }
public enum AtomicInspectionMutationOperation { Create, Update, Delete, Reorder, AttachPhoto, Complete, AttachReport }

public sealed record AtomicInspectionMutationCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AtomicInspectionMutationDomain Domain,
    AtomicInspectionMutationOperation Operation,
    int EntityId,
    int RelatedEntityId,
    string RequestJson,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicInspectionMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null,
    string? Error = null) : IAtomicResultData;

public sealed record AttachInspectionReportRequest(
    string FileName,
    string StoragePath,
    string ContentType,
    long FileSize);

public sealed class AtomicInspectionMutationHandler
    : IAtomicCommandHandler<AtomicInspectionMutationCommand, AtomicInspectionMutationResult>,
      IAtomicReplayAuthorizer<AtomicInspectionMutationCommand>
{
    private const int MaxTemplateItems = 100;
    private const int MaxInspectionItems = 100;

    public async Task<AtomicInspectionMutationResult> HandleAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        if (command.EntityId > 0)
        {
            await attempt.Locking.AcquireAsync(
                command.Domain == AtomicInspectionMutationDomain.Template
                    ? AtomicLockResource.InspectionTemplate
                    : AtomicLockResource.Inspection,
                command.EntityId,
                ct);
        }
        if (command.Domain == AtomicInspectionMutationDomain.Item && command.RelatedEntityId > 0)
            await attempt.Locking.AcquireAsync(AtomicLockResource.InspectionItem, command.RelatedEntityId, ct);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeAsync(command, attempt.Persistence, now, ct);

        return command.Domain switch
        {
            AtomicInspectionMutationDomain.Template =>
                await MutateTemplateAsync(command, attempt, now, ct),
            AtomicInspectionMutationDomain.Inspection =>
                await MutateInspectionAsync(command, attempt, now, ct),
            AtomicInspectionMutationDomain.Item =>
                await MutateItemAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    private static async Task<AtomicInspectionMutationResult> MutateItemAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        if (command.Operation == AtomicInspectionMutationOperation.Reorder)
        {
            var request = Read<ReorderInspectionItemsRequest>(command);
            var requestedIds = (request.ItemIds ?? []).ToArray();
            if (requestedIds.Length == 0)
                throw new DomainValidationException("Include the checklist questions in the order they should appear.");
            if (requestedIds.Any(id => id <= 0))
                throw new DomainValidationException("Checklist question ids must be positive.");
            if (requestedIds.Distinct().Count() != requestedIds.Length)
                throw new DomainValidationException("Each checklist question can appear only once in the new order.");

            var reordered = await attempt.Inspections.ReorderItemsAsync(
                command.PortfolioId, command.EntityId, requestedIds, now, ct);
            if (!reordered.InspectionExists) return Missing();
            EnsureChecklistItemEditable((InspectionStatus)reordered.InspectionStatus);
            if (!reordered.IsValid)
                throw new DomainValidationException(
                    "Reorder request must include every checklist question exactly once.");
            if (reordered.HasChanges)
            {
                attempt.StageSemanticEvent(Audit(command, nameof(Inspection), command.EntityId,
                    AuditLogOperation.Updated, "Inspection checklist reordered"), now);
                StageDataUpdate(attempt, command, nameof(Inspection), command.EntityId, now, suffix: "inspection");
            }
            return Applied(command.EntityId,
                await SnapshotInspectionItemsAsync(persistence, command.PortfolioId, command.EntityId, ct));
        }

        if (command.Operation == AtomicInspectionMutationOperation.Create)
        {
            var request = Read<CreateInspectionItemRequest>(command);
            var inspection = await persistence.Query<Inspection>().SingleOrDefaultAsync(entity =>
                entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct);
            if (inspection is null) return Missing();
            EnsureChecklistItemEditable(inspection.Status);

            var manifest = await persistence.Query<InspectionItem>().AsNoTracking()
                .Where(item => item.InspectionId == command.EntityId
                    && item.PortfolioId == command.PortfolioId)
                .GroupBy(_ => 1)
                .Select(group => new { Count = group.Count(), MaxSortOrder = group.Max(item => item.SortOrder) })
                .SingleOrDefaultAsync(ct);
            var count = manifest?.Count ?? 0;
            if (count >= MaxInspectionItems)
                throw new DomainValidationException(
                    $"An inspection can have at most {MaxInspectionItems} checklist questions.");
            var normalized = NormalizeInspectionItemText(request.Area, request.Label, count + 1);
            var item = new InspectionItem
            {
                PortfolioId = command.PortfolioId,
                InspectionId = command.EntityId,
                Area = normalized.Area,
                Label = normalized.Label,
                Result = InspectionItemResult.Pending,
                SortOrder = (manifest?.MaxSortOrder ?? -1) + 1,
            };
            persistence.Add(item);
            inspection.UpdatedAt = now;
            attempt.BindSemanticAudit(item, Audit(command, nameof(InspectionItem), 0,
                AuditLogOperation.Created, "Inspection checklist item created"));
            attempt.BindSemanticAudit(inspection, Audit(command, nameof(Inspection), inspection.Id,
                AuditLogOperation.Updated, "Inspection checklist changed"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(InspectionItem), item.Id, now, suffix: "item");
            StageDataUpdate(attempt, command, nameof(Inspection), inspection.Id, now, suffix: "inspection");
            return Applied(item.Id,
                await SnapshotInspectionItemAsync(persistence, command.PortfolioId, command.EntityId, item.Id, ct));
        }

        var target = await persistence.Query<InspectionItem>()
            .Where(item => item.Id == command.RelatedEntityId
                && item.InspectionId == command.EntityId
                && item.PortfolioId == command.PortfolioId)
            .Select(item => new { Item = item, Inspection = item.Inspection! })
            .SingleOrDefaultAsync(ct);
        if (target is null) return Missing();
        EnsureChecklistItemEditable(target.Inspection.Status);

        if (command.Operation == AtomicInspectionMutationOperation.Delete)
        {
            persistence.Remove(target.Item);
            target.Inspection.UpdatedAt = now;
            attempt.BindSemanticAudit(target.Item, Audit(command, nameof(InspectionItem), target.Item.Id,
                AuditLogOperation.Deleted, "Inspection checklist item deleted"));
            attempt.BindSemanticAudit(target.Inspection, Audit(command, nameof(Inspection), target.Inspection.Id,
                AuditLogOperation.Updated, "Inspection checklist changed"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(InspectionItem), target.Item.Id, now, deleted: true, suffix: "item");
            StageDataUpdate(attempt, command, nameof(Inspection), target.Inspection.Id, now, suffix: "inspection");
            return Applied(target.Item.Id);
        }

        if (command.Operation == AtomicInspectionMutationOperation.AttachPhoto)
        {
            var request = Read<AttachInspectionItemPhotoRequest>(command);
            var fileExists = await persistence.Query<StoredFile>().AsNoTracking().AnyAsync(file =>
                file.Id == request.StoredFileId && file.PortfolioId == command.PortfolioId
                && file.DeletedAt == null, ct);
            if (!fileExists) return Missing();
            target.Item.PhotoStoredFileId = request.StoredFileId;
            target.Inspection.UpdatedAt = now;
            attempt.BindSemanticAudit(target.Item, Audit(command, nameof(InspectionItem), target.Item.Id,
                AuditLogOperation.Updated, "Inspection checklist photo attached"));
            attempt.BindSemanticAudit(target.Inspection, Audit(command, nameof(Inspection), target.Inspection.Id,
                AuditLogOperation.Updated, "Inspection checklist changed"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(InspectionItem), target.Item.Id, now, suffix: "item");
            StageDataUpdate(attempt, command, nameof(Inspection), target.Inspection.Id, now, suffix: "inspection");
            return Applied(target.Item.Id,
                await SnapshotInspectionItemAsync(persistence, command.PortfolioId, command.EntityId, target.Item.Id, ct));
        }

        if (command.Operation != AtomicInspectionMutationOperation.Update)
            throw new ArgumentOutOfRangeException(nameof(command.Operation));
        var update = Read<UpdateInspectionItemRequest>(command);
        if (update.Area is not null || update.Label is not null)
        {
            var normalized = NormalizeInspectionItemText(
                update.Area ?? target.Item.Area,
                update.Label ?? target.Item.Label,
                target.Item.SortOrder + 1);
            target.Item.Area = normalized.Area;
            target.Item.Label = normalized.Label;
        }
        if (update.Result.HasValue) target.Item.Result = update.Result.Value;
        if (update.Note is not null) target.Item.Note = update.Note;
        target.Inspection.UpdatedAt = now;
        attempt.BindSemanticAudit(target.Item, Audit(command, nameof(InspectionItem), target.Item.Id,
            AuditLogOperation.Updated, "Inspection checklist item updated"));
        attempt.BindSemanticAudit(target.Inspection, Audit(command, nameof(Inspection), target.Inspection.Id,
            AuditLogOperation.Updated, "Inspection checklist changed"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(InspectionItem), target.Item.Id, now, suffix: "item");
        StageDataUpdate(attempt, command, nameof(Inspection), target.Inspection.Id, now, suffix: "inspection");
        return Applied(target.Item.Id,
            await SnapshotInspectionItemAsync(persistence, command.PortfolioId, command.EntityId, target.Item.Id, ct));
    }

    public async Task AuthorizeReplayAsync(
        AtomicInspectionMutationCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, persistence, await persistence.ReadDatabaseClockUtcAsync(ct), ct);
    }

    private static async Task<AtomicInspectionMutationResult> MutateTemplateAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (command.Operation != AtomicInspectionMutationOperation.Create
            && InspectionTemplateCatalog.IsBuiltInId(command.EntityId))
        {
            throw new DomainValidationException(
                "Built-in checklists are read-only. Copy one to a custom checklist before editing.");
        }

        var persistence = attempt.Persistence;
        if (command.Operation == AtomicInspectionMutationOperation.Create)
        {
            var request = Read<CreateInspectionTemplateRequest>(command);
            var normalized = NormalizeTemplate(request.Name, request.InspectionType, request.Items);
            var template = new InspectionTemplate
            {
                PortfolioId = command.PortfolioId,
                Name = normalized.Name,
                InspectionType = normalized.Type,
                IsBuiltIn = false,
            };
            AddTemplateItems(template, normalized.Items);
            persistence.Add(template);
            await attempt.FlushBusinessAsync(ct);
            attempt.StageSemanticEvent(Audit(command, nameof(InspectionTemplate), template.Id,
                AuditLogOperation.Created, "Custom inspection checklist created"), now);
            StageDataUpdate(attempt, command, nameof(InspectionTemplate), template.Id, now);
            return Applied(template.Id, await SnapshotTemplateAsync(persistence, command.PortfolioId, template.Id, ct));
        }

        var existing = await persistence.Query<InspectionTemplate>()
            .SingleOrDefaultAsync(template => template.Id == command.EntityId
                && template.PortfolioId == command.PortfolioId && !template.IsBuiltIn, ct);
        if (existing is null) return Missing();

        if (command.Operation == AtomicInspectionMutationOperation.Delete)
        {
            persistence.Remove(existing);
            await attempt.FlushBusinessAsync(ct);
            attempt.StageSemanticEvent(Audit(command, nameof(InspectionTemplate), existing.Id,
                AuditLogOperation.Deleted, "Custom inspection checklist deleted"), now);
            StageDataUpdate(attempt, command, nameof(InspectionTemplate), existing.Id, now, deleted: true);
            return Applied(existing.Id);
        }

        if (command.Operation != AtomicInspectionMutationOperation.Update)
            throw new ArgumentOutOfRangeException(nameof(command.Operation));

        var update = Read<UpdateInspectionTemplateRequest>(command);
        var replacement = NormalizeTemplate(update.Name, update.InspectionType, update.Items);
        await persistence.Query<InspectionTemplateItem>()
            .Where(item => item.TemplateId == existing.Id)
            .ExecuteDeleteAsync(ct);
        existing.Name = replacement.Name;
        existing.InspectionType = replacement.Type;
        AddTemplateItems(existing, replacement.Items);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(command, nameof(InspectionTemplate), existing.Id,
            AuditLogOperation.Updated, "Custom inspection checklist replaced"), now);
        StageDataUpdate(attempt, command, nameof(InspectionTemplate), existing.Id, now);
        return Applied(existing.Id, await SnapshotTemplateAsync(persistence, command.PortfolioId, existing.Id, ct));
    }

    private static async Task<AtomicInspectionMutationResult> MutateInspectionAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        if (command.Operation == AtomicInspectionMutationOperation.Create)
        {
            var request = Read<CreateInspectionRequest>(command);
            if (request.Status == InspectionStatus.Completed)
                throw new DomainValidationException(
                    "Use the Complete action to finish an inspection so its checklist, work orders, and report stay consistent.");

            var references = await AuthorizedProperties(command, persistence, now)
                .Where(property => property.Id == request.PropertyId)
                .Select(property => new InspectionReferenceValidation(
                    true,
                    request.UnitId == null || persistence.Query<Unit>().Any(unit =>
                        unit.Id == request.UnitId && unit.PortfolioId == command.PortfolioId
                        && unit.PropertyId == property.Id && unit.DeletedAt == null),
                    request.LeaseManagementId == null || persistence.Query<LeaseManagement>().Any(management =>
                        management.Id == request.LeaseManagementId && management.PortfolioId == command.PortfolioId
                        && management.PropertyId == property.Id
                        && (request.UnitId == null || management.UnitId == request.UnitId)),
                    request.LeaseAgreementId == null || (request.LeaseManagementId != null
                        && persistence.Query<LeaseAgreement>().Any(agreement =>
                            agreement.Id == request.LeaseAgreementId
                            && agreement.PortfolioId == command.PortfolioId
                            && agreement.LeaseManagementId == request.LeaseManagementId)),
                    request.TemplateId == null || request.TemplateId < 0
                        || persistence.Query<InspectionTemplate>().Any(template =>
                            template.Id == request.TemplateId && template.PortfolioId == command.PortfolioId
                            && !template.IsBuiltIn)))
                .SingleOrDefaultAsync(ct);
            if (references is null || !references.UnitValid || !references.ManagementValid
                || !references.AgreementValid || !references.TemplateValid)
                return Missing();
            if (request.TemplateId is < 0 && InspectionTemplateCatalog.FindBuiltIn(request.TemplateId.Value) is null)
                return Missing();

            var entity = new Inspection
            {
                PortfolioId = command.PortfolioId,
                PropertyId = request.PropertyId,
                UnitId = request.UnitId,
                LeaseManagementId = request.LeaseManagementId,
                LeaseAgreementId = request.LeaseAgreementId,
                Type = request.Type,
                Status = request.Status,
                ScheduledFor = Utc(request.ScheduledFor),
                CompletedAt = request.CompletedAt.HasValue ? Utc(request.CompletedAt.Value) : null,
                Outcome = request.Outcome,
                Notes = request.Notes,
                Inspector = request.Inspector,
                TemplateId = request.TemplateId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Inspection), 0,
                AuditLogOperation.Created, "Inspection created"));

            if (request.TemplateId is int templateId)
            {
                if (templateId < 0)
                {
                    foreach (var item in InspectionTemplateCatalog.FindBuiltIn(templateId)!.Items
                                 .OrderBy(item => item.SortOrder))
                    {
                        entity.Items.Add(NewItem(command.PortfolioId, item.Area, item.Label, item.SortOrder));
                    }
                }
                else
                {
                    var templateItems = await persistence.Query<InspectionTemplateItem>().AsNoTracking()
                        .Where(item => item.TemplateId == templateId)
                        .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                        .Select(item => new TemplateItemProjection(item.Area, item.Label, item.SortOrder))
                        .ToListAsync(ct);
                    foreach (var item in templateItems)
                        entity.Items.Add(NewItem(command.PortfolioId, item.Area, item.Label, item.SortOrder));
                }
            }

            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Inspection), entity.Id, now);
            return Applied(entity.Id,
                await SnapshotInspectionDetailAsync(persistence, command.PortfolioId, entity.Id, ct));
        }

        if (command.Operation == AtomicInspectionMutationOperation.Complete)
            return await CompleteInspectionAsync(command, attempt, now, ct);

        if (command.Operation == AtomicInspectionMutationOperation.AttachReport)
            return await AttachInspectionReportAsync(command, attempt, now, ct);

        var inspection = await persistence.Query<Inspection>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct);
        if (inspection is null) return Missing();

        if (command.Operation == AtomicInspectionMutationOperation.Delete)
        {
            persistence.Remove(inspection);
            attempt.BindSemanticAudit(inspection, Audit(command, nameof(Inspection), inspection.Id,
                AuditLogOperation.Deleted, "Inspection deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Inspection), inspection.Id, now, deleted: true);
            return Applied(inspection.Id);
        }

        if (command.Operation != AtomicInspectionMutationOperation.Update)
            throw new ArgumentOutOfRangeException(nameof(command.Operation));

        var requestUpdate = Read<UpdateInspectionRequest>(command);
        if (requestUpdate.Status is InspectionStatus.Completed && inspection.Status != InspectionStatus.Completed)
            throw new DomainValidationException(
                "Use the Complete action to finish an inspection so its checklist is verified, failed items become work orders, and the report is generated.");

        var effectiveUnitId = requestUpdate.UnitId ?? inspection.UnitId;
        var effectiveManagementId = requestUpdate.LeaseManagementId ?? inspection.LeaseManagementId;
        var referenceValid = await persistence.Query<Property>().AsNoTracking()
            .Where(property => property.Id == inspection.PropertyId && property.PortfolioId == command.PortfolioId)
            .Select(property => new
            {
                UnitValid = requestUpdate.UnitId == null || persistence.Query<Unit>().Any(unit =>
                    unit.Id == requestUpdate.UnitId && unit.PortfolioId == command.PortfolioId
                    && unit.PropertyId == property.Id && unit.DeletedAt == null),
                ManagementValid = requestUpdate.LeaseManagementId == null
                    || persistence.Query<LeaseManagement>().Any(management =>
                        management.Id == requestUpdate.LeaseManagementId
                        && management.PortfolioId == command.PortfolioId
                        && management.PropertyId == property.Id
                        && (effectiveUnitId == null || management.UnitId == effectiveUnitId)),
                AgreementValid = requestUpdate.LeaseAgreementId == null || (effectiveManagementId != null
                    && persistence.Query<LeaseAgreement>().Any(agreement =>
                        agreement.Id == requestUpdate.LeaseAgreementId
                        && agreement.PortfolioId == command.PortfolioId
                        && agreement.LeaseManagementId == effectiveManagementId)),
            }).SingleAsync(ct);
        if (!referenceValid.UnitValid || !referenceValid.ManagementValid || !referenceValid.AgreementValid)
            return Missing();

        if (requestUpdate.UnitId.HasValue) inspection.UnitId = requestUpdate.UnitId;
        if (requestUpdate.LeaseManagementId.HasValue) inspection.LeaseManagementId = requestUpdate.LeaseManagementId;
        if (requestUpdate.LeaseAgreementId.HasValue) inspection.LeaseAgreementId = requestUpdate.LeaseAgreementId;
        if (requestUpdate.Type.HasValue) inspection.Type = requestUpdate.Type.Value;
        if (requestUpdate.Status.HasValue) inspection.Status = requestUpdate.Status.Value;
        if (requestUpdate.ScheduledFor.HasValue) inspection.ScheduledFor = Utc(requestUpdate.ScheduledFor.Value);
        if (requestUpdate.CompletedAt.HasValue) inspection.CompletedAt = Utc(requestUpdate.CompletedAt.Value);
        if (requestUpdate.Outcome is not null) inspection.Outcome = requestUpdate.Outcome;
        if (requestUpdate.Notes is not null) inspection.Notes = requestUpdate.Notes;
        if (requestUpdate.Inspector is not null) inspection.Inspector = requestUpdate.Inspector;
        inspection.UpdatedAt = now;
        attempt.BindSemanticAudit(inspection, Audit(command, nameof(Inspection), inspection.Id,
            AuditLogOperation.Updated, "Inspection updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Inspection), inspection.Id, now);
        return Applied(inspection.Id,
            await SnapshotInspectionAsync(persistence, command.PortfolioId, inspection.Id, ct));
    }

    private static async Task<AtomicInspectionMutationResult> CompleteInspectionAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var state = await persistence.Query<Inspection>().AsNoTracking()
            .Where(inspection => inspection.Id == command.EntityId
                && inspection.PortfolioId == command.PortfolioId)
            .Select(inspection => new CompletionState(
                inspection.Status,
                inspection.Items.Count(),
                inspection.Items.Count(item => item.Result == InspectionItemResult.Pass),
                inspection.Items.Count(item => item.Result == InspectionItemResult.Fail),
                inspection.Items.Count(item => item.Result == InspectionItemResult.NotApplicable),
                inspection.Items.Count(item => item.Result == InspectionItemResult.Pending)))
            .SingleOrDefaultAsync(ct);
        if (state is null) return Missing();
        if (state.Status == InspectionStatus.Completed)
            return Rejected(command.EntityId, "Inspection is already completed.");
        if (state.TotalItems == 0)
            return Rejected(command.EntityId,
                "Add a checklist (pick a template) before completing this inspection — a completed inspection must record what was inspected.");
        if (state.TotalItems == state.PendingCount)
            return Rejected(command.EntityId,
                "Mark at least one checklist item Pass, Fail, or N/A before completing — a completed inspection must record what was inspected.");

        var inspection = await persistence.Query<Inspection>().SingleAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct);
        var failedItems = await persistence.Query<InspectionItem>()
            .Where(item => item.InspectionId == command.EntityId
                && item.PortfolioId == command.PortfolioId
                && item.Result == InspectionItemResult.Fail)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(ct);
        var newWorkOrders = new List<WorkOrder>();
        foreach (var item in failedItems)
        {
            if (item.SpawnedWorkOrderId.HasValue) continue;
            var workOrder = BuildInspectionWorkOrder(command, inspection, item, now);
            item.SpawnedWorkOrder = workOrder;
            persistence.Add(workOrder);
            attempt.BindSemanticAudit(item, Audit(command, nameof(InspectionItem), item.Id,
                AuditLogOperation.Updated, "Inspection failure linked to work order"));
            attempt.BindSemanticAudit(workOrder, Audit(command, nameof(WorkOrder), 0,
                AuditLogOperation.Created, "Work order created from failed inspection item"));
            newWorkOrders.Add(workOrder);
        }

        inspection.Status = InspectionStatus.Completed;
        inspection.CompletedAt ??= now;
        inspection.UpdatedAt = now;
        attempt.BindSemanticAudit(inspection, Audit(command, nameof(Inspection), inspection.Id,
            AuditLogOperation.Updated, "Inspection completed"));
        await attempt.FlushBusinessAsync(ct);

        var workOrderIds = await persistence.Query<InspectionItem>().AsNoTracking()
            .Where(item => item.InspectionId == command.EntityId
                && item.PortfolioId == command.PortfolioId
                && item.Result == InspectionItemResult.Fail
                && item.SpawnedWorkOrderId != null)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => item.SpawnedWorkOrderId!.Value)
            .ToListAsync(ct);
        StageDataUpdate(attempt, command, nameof(Inspection), inspection.Id, now, suffix: "inspection");
        foreach (var workOrder in newWorkOrders)
            StageDataUpdate(attempt, command, nameof(WorkOrder), workOrder.Id, now, suffix: $"work-order-{workOrder.Id}");

        var summary = new CompleteInspectionResponse
        {
            InspectionId = inspection.Id,
            Status = inspection.Status,
            TotalItems = state.TotalItems,
            PassCount = state.PassCount,
            FailCount = state.FailCount,
            NotApplicableCount = state.NotApplicableCount,
            PendingCount = state.PendingCount,
            ReportStoredFileId = inspection.ReportStoredFileId,
            CreatedWorkOrderIds = workOrderIds,
        };
        return Applied(inspection.Id, JsonSerializer.Serialize(summary));
    }

    private static async Task<AtomicInspectionMutationResult> AttachInspectionReportAsync(
        AtomicInspectionMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var inspection = await persistence.Query<Inspection>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct);
        if (inspection is null) return Missing();
        if (inspection.Status != InspectionStatus.Completed)
            return Rejected(command.EntityId, "Complete the inspection before attaching its report.");
        if (inspection.ReportStoredFileId.HasValue)
            return Applied(inspection.ReportStoredFileId.Value,
                JsonSerializer.Serialize(inspection.ReportStoredFileId.Value));

        var request = Read<AttachInspectionReportRequest>(command);
        if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.StoragePath)
            || string.IsNullOrWhiteSpace(request.ContentType) || request.FileSize <= 0)
            throw new ArgumentException("Inspection report file metadata is incomplete.");
        var stored = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = request.FileName,
            FilePath = request.StoragePath,
            ContentType = request.ContentType,
            FileSize = request.FileSize,
            EntityType = nameof(Inspection),
            EntityId = inspection.Id,
            UploadedAt = now,
        };
        persistence.Add(stored);
        attempt.BindSemanticAudit(stored, Audit(command, nameof(StoredFile), 0,
            AuditLogOperation.Created, "Inspection report stored"));
        await attempt.FlushBusinessAsync(ct);
        inspection.ReportStoredFileId = stored.Id;
        inspection.UpdatedAt = now;
        attempt.BindSemanticAudit(inspection, Audit(command, nameof(Inspection), inspection.Id,
            AuditLogOperation.Updated, "Inspection report attached"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(StoredFile), stored.Id, now, suffix: "report-file");
        StageDataUpdate(attempt, command, nameof(Inspection), inspection.Id, now, suffix: "inspection");
        return Applied(stored.Id, JsonSerializer.Serialize(stored.Id));
    }

    private static WorkOrder BuildInspectionWorkOrder(
        AtomicInspectionMutationCommand command,
        Inspection inspection,
        InspectionItem item,
        DateTime now)
    {
        var title = string.IsNullOrWhiteSpace(item.Label) ? "Inspection follow-up" : item.Label.Trim();
        var area = string.IsNullOrWhiteSpace(item.Area) ? "General" : item.Area.Trim();
        var description = string.IsNullOrWhiteSpace(item.Note)
            ? $"Failed inspection item ({area}) from inspection #{inspection.Id}."
            : item.Note.Trim();
        var workOrder = new WorkOrder
        {
            PortfolioId = command.PortfolioId,
            PropertyId = inspection.PropertyId,
            UnitId = inspection.UnitId,
            LeaseManagementId = inspection.LeaseManagementId,
            Title = title.Length > 200 ? title[..200] : title,
            Description = description.Length > 4000 ? description[..4000] : description,
            Category = "Inspection",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        workOrder.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            FromStatus = null,
            ToStatus = workOrder.Status,
            ChangedByUserId = command.ActorUserId,
            ChangedByLabel = "Inspection",
            CreatedAtUtc = now,
        });
        return workOrder;
    }

    private static async Task AuthorizeAsync(
        AtomicInspectionMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var authorized = command.Domain switch
        {
            AtomicInspectionMutationDomain.Template =>
                await AuthorizedAssignments(command, persistence, now)
                    .AnyAsync(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct),
            AtomicInspectionMutationDomain.Inspection
                when command.Operation == AtomicInspectionMutationOperation.Create =>
                await AuthorizedProperties(command, persistence, now)
                    .AnyAsync(property => property.Id == Read<CreateInspectionRequest>(command).PropertyId, ct),
            AtomicInspectionMutationDomain.Inspection or AtomicInspectionMutationDomain.Item =>
                await persistence.Query<Inspection>().AsNoTracking().AnyAsync(inspection =>
                    inspection.Id == command.EntityId && inspection.PortfolioId == command.PortfolioId
                    && AuthorizedProperties(command, persistence, now)
                        .Any(property => property.Id == inspection.PropertyId), ct),
            _ => false,
        };
        if (!authorized)
            throw new UnauthorizedAccessException("The inspection is outside the current Team role and property scope.");
    }

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        AtomicInspectionMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now) =>
        persistence.Query<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
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
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.WorkManage
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));

    private static IQueryable<Property> AuthorizedProperties(
        AtomicInspectionMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = AuthorizedAssignments(command, persistence, now);
        return persistence.Query<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == command.PortfolioId
                        && selected.PropertyId == property.Id))));
    }

    private static IQueryable<InspectionResponse> InspectionSnapshotQuery(
        IAtomicPersistenceSession persistence,
        int portfolioId) =>
        persistence.Query<Inspection>().AsNoTracking().Where(inspection => inspection.PortfolioId == portfolioId)
            .Select(inspection => new InspectionResponse
            {
                Id = inspection.Id,
                PortfolioId = inspection.PortfolioId,
                PropertyId = inspection.PropertyId,
                UnitId = inspection.UnitId,
                LeaseManagementId = inspection.LeaseManagementId,
                LeaseAgreementId = inspection.LeaseAgreementId,
                Type = inspection.Type,
                Status = inspection.Status,
                ScheduledFor = inspection.ScheduledFor,
                CompletedAt = inspection.CompletedAt,
                Outcome = inspection.Outcome,
                Notes = inspection.Notes,
                TemplateId = inspection.TemplateId,
                ReportStoredFileId = inspection.ReportStoredFileId,
                Inspector = inspection.Inspector,
                PropertyName = inspection.Property!.Name,
                UnitNumber = inspection.Unit == null ? null : inspection.Unit.UnitNumber,
                CreatedAt = inspection.CreatedAt,
                UpdatedAt = inspection.UpdatedAt,
            });

    private static async Task<string> SnapshotInspectionAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int inspectionId, CancellationToken ct) =>
        JsonSerializer.Serialize(await InspectionSnapshotQuery(persistence, portfolioId)
            .SingleAsync(inspection => inspection.Id == inspectionId, ct));

    private static async Task<string> SnapshotInspectionDetailAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int inspectionId, CancellationToken ct)
    {
        var detail = await persistence.Query<Inspection>().AsNoTracking()
            .Where(inspection => inspection.PortfolioId == portfolioId && inspection.Id == inspectionId)
            .Select(inspection => new InspectionDetailResponse
            {
                Id = inspection.Id,
                PortfolioId = inspection.PortfolioId,
                PropertyId = inspection.PropertyId,
                UnitId = inspection.UnitId,
                LeaseManagementId = inspection.LeaseManagementId,
                LeaseAgreementId = inspection.LeaseAgreementId,
                Type = inspection.Type,
                Status = inspection.Status,
                ScheduledFor = inspection.ScheduledFor,
                CompletedAt = inspection.CompletedAt,
                Outcome = inspection.Outcome,
                Notes = inspection.Notes,
                TemplateId = inspection.TemplateId,
                ReportStoredFileId = inspection.ReportStoredFileId,
                Inspector = inspection.Inspector,
                PropertyName = inspection.Property!.Name,
                UnitNumber = inspection.Unit == null ? null : inspection.Unit.UnitNumber,
                CreatedAt = inspection.CreatedAt,
                UpdatedAt = inspection.UpdatedAt,
                Items = inspection.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .Select(item => new InspectionItemResponse
                    {
                        Id = item.Id,
                        InspectionId = item.InspectionId,
                        Area = item.Area,
                        Label = item.Label,
                        Result = item.Result,
                        Note = item.Note,
                        PhotoStoredFileId = item.PhotoStoredFileId,
                        SpawnedWorkOrderId = item.SpawnedWorkOrderId,
                        SortOrder = item.SortOrder,
                    }).ToList(),
            }).SingleAsync(ct);
        return JsonSerializer.Serialize(detail);
    }

    private static IQueryable<InspectionItemResponse> InspectionItemSnapshotQuery(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int inspectionId) =>
        persistence.Query<InspectionItem>().AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId && item.InspectionId == inspectionId)
            .Select(item => new InspectionItemResponse
            {
                Id = item.Id,
                InspectionId = item.InspectionId,
                Area = item.Area,
                Label = item.Label,
                Result = item.Result,
                Note = item.Note,
                PhotoStoredFileId = item.PhotoStoredFileId,
                SpawnedWorkOrderId = item.SpawnedWorkOrderId,
                SortOrder = item.SortOrder,
            });

    private static async Task<string> SnapshotInspectionItemAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int inspectionId,
        int itemId,
        CancellationToken ct) =>
        JsonSerializer.Serialize(await InspectionItemSnapshotQuery(persistence, portfolioId, inspectionId)
            .SingleAsync(item => item.Id == itemId, ct));

    private static async Task<string> SnapshotInspectionItemsAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int inspectionId,
        CancellationToken ct) =>
        JsonSerializer.Serialize(await InspectionItemSnapshotQuery(persistence, portfolioId, inspectionId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(ct));

    private static async Task<string> SnapshotTemplateAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int templateId, CancellationToken ct)
    {
        var template = await persistence.Query<InspectionTemplate>().AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId && row.Id == templateId)
            .Select(row => new InspectionTemplateResponse
            {
                Id = row.Id,
                PortfolioId = row.PortfolioId,
                Name = row.Name,
                InspectionType = row.InspectionType,
                IsBuiltIn = row.IsBuiltIn,
                Items = row.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .Select(item => new InspectionTemplateItemResponse
                    {
                        Area = item.Area,
                        Label = item.Label,
                        SortOrder = item.SortOrder,
                    }).ToList(),
            }).SingleAsync(ct);
        return JsonSerializer.Serialize(template);
    }

    private static NormalizedTemplate NormalizeTemplate(
        string name,
        InspectionType type,
        IReadOnlyList<UpsertInspectionTemplateItemRequest>? items)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length is 0 or > 200)
            throw new DomainValidationException("Checklist name is required and must be 200 characters or fewer.");
        if (items is null || items.Count == 0)
            throw new DomainValidationException("Add at least one checklist question.");
        if (items.Count > MaxTemplateItems)
            throw new DomainValidationException($"A checklist can have at most {MaxTemplateItems} questions.");
        var normalized = new List<NormalizedTemplateItem>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var area = (items[index].Area ?? string.Empty).Trim();
            var label = (items[index].Label ?? string.Empty).Trim();
            if (area.Length is 0 or > 120)
                throw new DomainValidationException($"Question {index + 1} area is required and must be 120 characters or fewer.");
            if (label.Length is 0 or > 300)
                throw new DomainValidationException($"Question {index + 1} item is required and must be 300 characters or fewer.");
            normalized.Add(new NormalizedTemplateItem(area, label));
        }
        return new NormalizedTemplate(normalizedName, type, normalized);
    }

    private static (string Area, string Label) NormalizeInspectionItemText(
        string? area,
        string? label,
        int questionNumber)
    {
        var normalizedArea = (area ?? string.Empty).Trim();
        var normalizedLabel = (label ?? string.Empty).Trim();
        if (normalizedArea.Length is 0 or > 120)
            throw new DomainValidationException(
                $"Question {questionNumber} area is required and must be 120 characters or fewer.");
        if (normalizedLabel.Length is 0 or > 300)
            throw new DomainValidationException(
                $"Question {questionNumber} item is required and must be 300 characters or fewer.");
        return (normalizedArea, normalizedLabel);
    }

    private static void EnsureChecklistItemEditable(InspectionStatus status)
    {
        if (status == InspectionStatus.Completed)
            throw new DomainValidationException(
                "This completed inspection is read-only. Reopen or schedule a new inspection before changing checklist items.",
                statusCode: 409);
    }

    private static void AddTemplateItems(InspectionTemplate template, IReadOnlyList<NormalizedTemplateItem> items)
    {
        for (var index = 0; index < items.Count; index++)
            template.Items.Add(new InspectionTemplateItem
            {
                Template = template,
                Area = items[index].Area,
                Label = items[index].Label,
                SortOrder = index,
            });
    }

    private static InspectionItem NewItem(int portfolioId, string area, string label, int sortOrder) => new()
    {
        PortfolioId = portfolioId,
        Area = area,
        Label = label,
        Result = InspectionItemResult.Pending,
        SortOrder = sortOrder,
    };

    private static void StageDataUpdate(
        IAtomicWriteAttempt attempt,
        AtomicInspectionMutationCommand command,
        string entityType,
        int entityId,
        DateTime now,
        bool deleted = false,
        string suffix = "entity") =>
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
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:" +
                $"{command.Domain.ToString().ToLowerInvariant()}:" +
                $"{command.Operation.ToString().ToLowerInvariant()}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private static AtomicSemanticAudit Audit(
        AtomicInspectionMutationCommand command,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        string reason) => new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private static T Read<T>(AtomicInspectionMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Inspection mutation request payload is invalid.");

    private static void Validate(AtomicInspectionMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128
            || (command.Domain == AtomicInspectionMutationDomain.Item && command.EntityId <= 0)
            || (command.Operation != AtomicInspectionMutationOperation.Create && command.EntityId <= 0)
            || (command.Domain == AtomicInspectionMutationDomain.Item
                && command.Operation is AtomicInspectionMutationOperation.Update
                    or AtomicInspectionMutationOperation.Delete
                    or AtomicInspectionMutationOperation.AttachPhoto
                && command.RelatedEntityId <= 0))
            throw new ArgumentException(
                "Portfolio, actor, access revision, operation, and delivery identifiers are required.");
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : value.ToUniversalTime();
    private static AtomicInspectionMutationResult Missing() => new(false, false, 0);
    private static AtomicInspectionMutationResult Rejected(int id, string error) =>
        new(true, false, id, Error: error);
    private static AtomicInspectionMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, responseJson);

    private sealed record InspectionReferenceValidation(
        bool PropertyValid, bool UnitValid, bool ManagementValid, bool AgreementValid, bool TemplateValid);
    private sealed record TemplateItemProjection(string Area, string Label, int SortOrder);
    private sealed record CompletionState(
        InspectionStatus Status,
        int TotalItems,
        int PassCount,
        int FailCount,
        int NotApplicableCount,
        int PendingCount);
    private sealed record NormalizedTemplate(string Name, InspectionType Type, IReadOnlyList<NormalizedTemplateItem> Items);
    private sealed record NormalizedTemplateItem(string Area, string Label);
}

public static class AtomicInspectionMutation
{
    public static readonly AtomicJsonResultCodec<AtomicInspectionMutationResult> Codec =
        new("rental.inspection-mutation.v1");

    public static AtomicInspectionMutationCommand Command<TRequest>(
        WorkspaceReadScope scope,
        AtomicInspectionMutationDomain domain,
        AtomicInspectionMutationOperation operation,
        int entityId,
        int relatedEntityId,
        string operationKey,
        TRequest request) => new(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, operation, entityId, relatedEntityId,
            JsonSerializer.Serialize(request), operationKey);

    public static AtomicCommandIdentity Identity(AtomicInspectionMutationCommand command) => new(
        $"rental.inspection.{command.Domain.ToString().ToLowerInvariant()}." +
        command.Operation.ToString().ToLowerInvariant(),
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
        $"{command.EntityId}:{command.RelatedEntityId}:{command.DeliveryIdempotencyKey}");
}
