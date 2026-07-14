using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IInspectionService"/>
public class InspectionService : IInspectionService
{
    private const string EntityType = "Inspection";
    private const string TemplateEntityType = "InspectionTemplate";
    private const string WorkOrderEntityType = "WorkOrder";
    private const int MaxTemplateItems = 100;
    private const int MaxInspectionItems = 100;
    internal const string InspectionItemOrderValidationSql = """
        WITH requested AS (
            SELECT item."ItemId", item."Position"::integer
            FROM unnest(@itemIds::integer[]) WITH ORDINALITY AS item("ItemId", "Position")
        ), target_inspection AS (
            SELECT inspection."Status"
            FROM "Inspections" inspection
            WHERE inspection."Id" = @inspectionId
              AND inspection."PortfolioId" = @portfolioId
        )
        SELECT
            EXISTS(SELECT 1 FROM target_inspection) AS "InspectionExists",
            COALESCE((SELECT "Status" FROM target_inspection), -1) AS "InspectionStatus",
            COUNT(*) = COUNT(DISTINCT requested."ItemId")
              AND COUNT(*) = (
                  SELECT COUNT(*)
                  FROM "InspectionItems" existing
                  WHERE existing."InspectionId" = @inspectionId
                    AND existing."PortfolioId" = @portfolioId)
              AND COUNT(*) = COUNT(item."Id") AS "IsValid"
        FROM requested
        LEFT JOIN "InspectionItems" item
          ON item."Id" = requested."ItemId"
         AND item."InspectionId" = @inspectionId
         AND item."PortfolioId" = @portfolioId
        """;
    internal const string InspectionItemOrderUpdateSql = """
        WITH requested AS (
            SELECT item."ItemId", item."Position"::integer - 1 AS "SortOrder"
            FROM unnest(@itemIds::integer[]) WITH ORDINALITY AS item("ItemId", "Position")
        ), updated_items AS (
            UPDATE "InspectionItems" item
            SET "SortOrder" = requested."SortOrder"
            FROM requested
            WHERE item."Id" = requested."ItemId"
              AND item."InspectionId" = @inspectionId
              AND item."PortfolioId" = @portfolioId
            RETURNING item."InspectionId"
        )
        UPDATE "Inspections" inspection
        SET "UpdatedAt" = @updatedAt
        WHERE inspection."Id" = @inspectionId
          AND inspection."PortfolioId" = @portfolioId
          AND EXISTS (SELECT 1 FROM updated_items)
        """;
    private static readonly string[] ReadCapabilities = [CapabilityKeys.WorkRead];
    private static readonly string[] WriteCapabilities = [CapabilityKeys.WorkManage];

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly IFileStorage _storage;
    private readonly IInspectionReportPdfGenerator _pdf;
    private readonly ILogger<InspectionService> _logger;
    private readonly TimeProvider _timeProvider;

    public InspectionService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IFileStorage storage,
        IInspectionReportPdfGenerator pdf,
        ILogger<InspectionService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _storage = storage;
        _pdf = pdf;
        _logger = logger;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped inspection mutations require the atomic persistence kernel.");

    // Internal portfolio-id entry points exist only for the focused service tests via
    // InternalsVisibleTo. Production callers resolve IInspectionService, whose only surface requires
    // a server-derived WorkspaceReadScope and therefore cannot bypass capability/property checks.
    internal async Task<IReadOnlyList<InspectionResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, propertyId, query, ct);
        return page.Items;
    }

    internal async Task<InspectionListResponse> ListPageAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Inspections
            .AsNoTracking()
            .Where(i => i.PortfolioId == portfolioId);

        return await ListPageFromQueryAsync(q, propertyId, query, ct);
    }

    public async Task<IReadOnlyList<InspectionResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        ListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(scope, propertyId, query, ct);
        return page.Items;
    }

    public Task<InspectionListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        int? propertyId,
        ListQuery query,
        CancellationToken ct = default)
        => ListPageFromQueryAsync(
            AuthorizedInspections(scope, ReadCapabilities),
            propertyId,
            query,
            ct);

    private static async Task<InspectionListResponse> ListPageFromQueryAsync(
        IQueryable<Inspection> q,
        int? propertyId,
        ListQuery query,
        CancellationToken ct)
    {

        if (propertyId.HasValue)
        {
            q = q.Where(i => i.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(i =>
                (i.Outcome != null && EF.Functions.ILike(i.Outcome, $"%{term}%")) ||
                (i.Notes != null && EF.Functions.ILike(i.Notes, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "status" => query.SortDescending ? q.OrderByDescending(i => i.Status) : q.OrderBy(i => i.Status),
            "type" => query.SortDescending ? q.OrderByDescending(i => i.Type) : q.OrderBy(i => i.Type),
            "scheduledfor" => query.SortDescending ? q.OrderByDescending(i => i.ScheduledFor) : q.OrderBy(i => i.ScheduledFor),
            "completedat" => query.SortDescending ? q.OrderByDescending(i => i.CompletedAt) : q.OrderBy(i => i.CompletedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(i => i.UpdatedAt) : q.OrderBy(i => i.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(i => i.CreatedAt) : q.OrderBy(i => i.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(i => i.ScheduledFor) : q.OrderBy(i => i.ScheduledFor),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(i => new InspectionResponse
            {
                Id = i.Id,
                PortfolioId = i.PortfolioId,
                PropertyId = i.PropertyId,
                UnitId = i.UnitId,
                LeaseManagementId = i.LeaseManagementId,
                LeaseAgreementId = i.LeaseAgreementId,
                Type = i.Type,
                Status = i.Status,
                ScheduledFor = i.ScheduledFor,
                CompletedAt = i.CompletedAt,
                Outcome = i.Outcome,
                Notes = i.Notes,
                TemplateId = i.TemplateId,
                ReportStoredFileId = i.ReportStoredFileId,
                Inspector = i.Inspector,
                PropertyName = i.Property!.Name,
                UnitNumber = i.Unit == null ? null : i.Unit.UnitNumber,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt,
            })
            .ToListAsync(ct);

        return new InspectionListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static IQueryable<InspectionDetailResponse> InspectionDetailQuery(
        IQueryable<Inspection> inspections) =>
        inspections.Select(inspection => new InspectionDetailResponse
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
            Items = inspection.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
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
                })
                .ToList(),
        });

    internal async Task<InspectionDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
        => await InspectionDetailQuery(_db.Inspections.AsNoTracking()
                .Where(inspection => inspection.PortfolioId == portfolioId))
            .SingleOrDefaultAsync(inspection => inspection.Id == id, ct);

    public async Task<InspectionDetailResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
        => await InspectionDetailQuery(AuthorizedInspections(scope, ReadCapabilities))
            .SingleOrDefaultAsync(inspection => inspection.Id == id, ct);

    internal async Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct = default)
    {
        // Built-ins (code-defined) first, then any portfolio-custom templates from the DB.
        var result = InspectionTemplateCatalog.BuiltIns
            .Select(InspectionTemplateResponse.FromEntity)
            .ToList();

        var custom = await _db.InspectionTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId)
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Select(t => new InspectionTemplateResponse
            {
                Id = t.Id,
                PortfolioId = t.PortfolioId,
                Name = t.Name,
                InspectionType = t.InspectionType,
                IsBuiltIn = t.IsBuiltIn,
                Items = t.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .Select(item => new InspectionTemplateItemResponse
                    {
                        Area = item.Area,
                        Label = item.Label,
                        SortOrder = item.SortOrder,
                    }).ToList(),
            })
            .ToListAsync(ct);

        result.AddRange(custom);
        return result;
    }

    public async Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAuthorizedAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default)
        => await HasAllPropertiesAccessAsync(scope, ReadCapabilities, ct)
            ? await ListTemplatesAsync(scope.PortfolioId, ct)
            : [];

    internal async Task<InspectionTemplateResponse?> GetTemplateAsync(int portfolioId, int templateId, CancellationToken ct = default)
    {
        if (InspectionTemplateCatalog.IsBuiltInId(templateId))
        {
            var builtIn = InspectionTemplateCatalog.FindBuiltIn(templateId);
            return builtIn == null ? null : InspectionTemplateResponse.FromEntity(builtIn);
        }

        return await _db.InspectionTemplates.AsNoTracking()
            .Where(template => template.Id == templateId && template.PortfolioId == portfolioId)
            .Select(template => new InspectionTemplateResponse
            {
                Id = template.Id,
                PortfolioId = template.PortfolioId,
                Name = template.Name,
                InspectionType = template.InspectionType,
                IsBuiltIn = template.IsBuiltIn,
                Items = template.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .Select(item => new InspectionTemplateItemResponse
                    {
                        Area = item.Area,
                        Label = item.Label,
                        SortOrder = item.SortOrder,
                    }).ToList(),
            }).SingleOrDefaultAsync(ct);
    }

    public async Task<InspectionTemplateResponse?> GetTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        int templateId,
        CancellationToken ct = default)
        => await HasAllPropertiesAccessAsync(scope, ReadCapabilities, ct)
            ? await GetTemplateAsync(scope.PortfolioId, templateId, ct)
            : null;

    internal Task<InspectionTemplateResponse> CreateTemplateAsync(int portfolioId, CreateInspectionTemplateRequest request, CancellationToken ct = default)
        => CreateTemplateCoreAsync(portfolioId, request, broadcast: true, ct);

    private async Task<InspectionTemplateResponse> CreateTemplateCoreAsync(
        int portfolioId,
        CreateInspectionTemplateRequest request,
        bool broadcast,
        CancellationToken ct)
    {
        var normalized = NormalizeTemplateRequest(request.Name, request.InspectionType, request.Items);
        var entity = new InspectionTemplate
        {
            PortfolioId = portfolioId,
            Name = normalized.Name,
            InspectionType = normalized.InspectionType,
            IsBuiltIn = false,
        };

        AddTemplateItems(entity, normalized.Items);

        _db.InspectionTemplates.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = InspectionTemplateResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, TemplateEntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<InspectionTemplateResponse?> CreateTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateInspectionTemplateRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Create, 0, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionTemplateResponse>(outcome.Value);
    }

    internal Task<InspectionTemplateResponse?> UpdateTemplateAsync(int portfolioId, int templateId, UpdateInspectionTemplateRequest request, CancellationToken ct = default)
        => UpdateTemplateCoreAsync(portfolioId, templateId, request, broadcast: true, ct);

    private async Task<InspectionTemplateResponse?> UpdateTemplateCoreAsync(
        int portfolioId,
        int templateId,
        UpdateInspectionTemplateRequest request,
        bool broadcast,
        CancellationToken ct)
    {
        ThrowIfBuiltInTemplateMutation(templateId);

        var entity = await _db.InspectionTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var normalized = NormalizeTemplateRequest(request.Name, request.InspectionType, request.Items);
        entity.Name = normalized.Name;
        entity.InspectionType = normalized.InspectionType;

        await using var tx = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        await _db.InspectionTemplateItems
            .Where(i => i.TemplateId == templateId)
            .ExecuteDeleteAsync(ct);
        AddTemplateItems(entity, normalized.Items);

        await _db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        var response = InspectionTemplateResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, TemplateEntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<InspectionTemplateResponse?> UpdateTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        int templateId,
        UpdateInspectionTemplateRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Update, templateId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionTemplateResponse>(outcome.Value);
    }

    internal Task<bool> DeleteTemplateAsync(int portfolioId, int templateId, CancellationToken ct = default)
        => DeleteTemplateCoreAsync(portfolioId, templateId, broadcast: true, ct);

    private async Task<bool> DeleteTemplateCoreAsync(
        int portfolioId,
        int templateId,
        bool broadcast,
        CancellationToken ct)
    {
        ThrowIfBuiltInTemplateMutation(templateId);

        var entity = await _db.InspectionTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        _db.InspectionTemplates.Remove(entity);
        await _db.SaveChangesAsync(ct);

        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, TemplateEntityType, templateId, ct);
        }
        return true;
    }

    public async Task<bool> DeleteTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        int templateId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Delete, templateId, 0, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return outcome.Value.Found;
    }

    internal Task<InspectionDetailResponse?> CreateAsync(int portfolioId, CreateInspectionRequest request, CancellationToken ct = default)
        => CreateCoreAsync(portfolioId, request, broadcast: true, ct);

    private async Task<InspectionDetailResponse?> CreateCoreAsync(
        int portfolioId,
        CreateInspectionRequest request,
        bool broadcast,
        CancellationToken ct)
    {
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.LeaseManagementId.HasValue &&
            !await _db.EnsureLeaseManagementInPortfolioAsync(portfolioId, request.LeaseManagementId.Value, ct))
        {
            return null;
        }
        if (request.LeaseAgreementId.HasValue &&
            (!request.LeaseManagementId.HasValue ||
             !await _db.EnsureLeaseAgreementInManagementAsync(portfolioId, request.LeaseManagementId.Value, request.LeaseAgreementId.Value, ct)))
        {
            return null;
        }

        // Resolve the template (built-in or custom) and its items, if one was requested.
        List<(string Area, string Label, int SortOrder)> templateItems = new();
        if (request.TemplateId.HasValue)
        {
            var resolved = await ResolveTemplateItemsAsync(portfolioId, request.TemplateId.Value, ct);
            if (resolved == null)
            {
                // Template id was supplied but not a known built-in or in-portfolio custom template.
                return null;
            }
            templateItems = resolved;
        }

        var now = _timeProvider.UtcNow();
        var entity = new Inspection
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            LeaseManagementId = request.LeaseManagementId,
            LeaseAgreementId = request.LeaseAgreementId,
            Type = request.Type,
            Status = request.Status,
            ScheduledFor = request.ScheduledFor.ToUtc(),
            CompletedAt = request.CompletedAt.ToUtc(),
            Outcome = request.Outcome,
            Notes = request.Notes,
            Inspector = request.Inspector,
            TemplateId = request.TemplateId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var (area, label, sortOrder) in templateItems)
        {
            entity.Items.Add(new InspectionItem
            {
                PortfolioId = portfolioId,
                Area = area,
                Label = label,
                Result = InspectionItemResult.Pending,
                SortOrder = sortOrder,
            });
        }

        _db.Inspections.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = InspectionDetailResponse.FromEntity(entity, entity.Items);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<InspectionDetailResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateInspectionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Create, 0, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionDetailResponse>(outcome.Value);
    }

    internal Task<InspectionResponse?> UpdateAsync(int portfolioId, int id, UpdateInspectionRequest request, CancellationToken ct = default)
        => UpdateCoreAsync(portfolioId, id, request, broadcast: true, ct);

    private async Task<InspectionResponse?> UpdateCoreAsync(
        int portfolioId,
        int id,
        UpdateInspectionRequest request,
        bool broadcast,
        CancellationToken ct)
    {
        var entity = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, entity.PropertyId, ct))
        {
            return null;
        }

        var effectiveLeaseManagementId = request.LeaseManagementId ?? entity.LeaseManagementId;
        if (request.LeaseManagementId.HasValue &&
            !await _db.EnsureLeaseManagementInPortfolioAsync(portfolioId, request.LeaseManagementId.Value, ct))
        {
            return null;
        }
        if (request.LeaseAgreementId.HasValue &&
            (!effectiveLeaseManagementId.HasValue ||
             !await _db.EnsureLeaseAgreementInManagementAsync(portfolioId, effectiveLeaseManagementId.Value, request.LeaseAgreementId.Value, ct)))
        {
            return null;
        }

        // Completing an inspection is NOT a plain status edit: it must spawn a work order per failed item,
        // stamp CompletedAt, and generate the PDF report — all of which live in CompleteAsync behind the
        // no-checklist / all-pending guards. Block a generic PATCH from flipping a not-yet-completed
        // inspection straight to Completed (which would skip every one of those). Re-sending Completed on
        // an already-completed inspection is a harmless no-op and stays allowed, so editing notes/outcome
        // on a completed inspection still works.
        if (request.Status is InspectionStatus.Completed && entity.Status != InspectionStatus.Completed)
        {
            throw new DomainValidationException(
                "Use the Complete action to finish an inspection so its checklist is verified, "
                    + "failed items become work orders, and the report is generated.");
        }

        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.LeaseManagementId.HasValue) entity.LeaseManagementId = request.LeaseManagementId;
        if (request.LeaseAgreementId.HasValue) entity.LeaseAgreementId = request.LeaseAgreementId;
        if (request.Type.HasValue) entity.Type = request.Type.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.ScheduledFor.HasValue) entity.ScheduledFor = request.ScheduledFor.Value.ToUtc();
        if (request.CompletedAt.HasValue) entity.CompletedAt = request.CompletedAt.ToUtc();
        if (request.Outcome != null) entity.Outcome = request.Outcome;
        if (request.Notes != null) entity.Notes = request.Notes;
        if (request.Inspector != null) entity.Inspector = request.Inspector;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = InspectionResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<InspectionResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateInspectionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Update, id, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionResponse>(outcome.Value);
    }

    internal Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
        => DeleteCoreAsync(portfolioId, id, broadcast: true, ct);

    private async Task<bool> DeleteCoreAsync(
        int portfolioId,
        int id,
        bool broadcast,
        CancellationToken ct)
    {
        var entity = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // No soft-delete column on Inspection; cascade removes its checklist items via the FK.
        _db.Inspections.Remove(entity);
        await _db.SaveChangesAsync(ct);

        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        }
        return true;
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Delete, id, 0, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicInspectionMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

    internal async Task<InspectionItemResponse?> CreateItemAsync(int portfolioId, int inspectionId, CreateInspectionItemRequest request, CancellationToken ct = default)
    {
        var inspection = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == inspectionId && i.PortfolioId == portfolioId, ct);
        if (inspection == null)
        {
            return null;
        }

        EnsureChecklistItemEditable(inspection.Status);

        var existingCount = await _db.InspectionItems
            .CountAsync(it => it.InspectionId == inspectionId && it.PortfolioId == portfolioId, ct);
        if (existingCount >= MaxInspectionItems)
        {
            throw new DomainValidationException($"An inspection can have at most {MaxInspectionItems} checklist questions.");
        }

        var normalized = NormalizeInspectionItemText(request.Area, request.Label, existingCount + 1);
        var maxSortOrder = await _db.InspectionItems
            .Where(it => it.InspectionId == inspectionId && it.PortfolioId == portfolioId)
            .Select(it => (int?)it.SortOrder)
            .MaxAsync(ct);

        var item = new InspectionItem
        {
            PortfolioId = portfolioId,
            InspectionId = inspectionId,
            Area = normalized.Area,
            Label = normalized.Label,
            Result = InspectionItemResult.Pending,
            SortOrder = (maxSortOrder ?? -1) + 1,
        };

        _db.InspectionItems.Add(item);
        inspection.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        return InspectionItemResponse.FromEntity(item);
    }

    public async Task<InspectionItemResponse?> CreateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        CreateInspectionItemRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Create, inspectionId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionItemResponse>(outcome.Value);
    }

    internal async Task<InspectionItemResponse?> UpdateItemAsync(int portfolioId, int inspectionId, int itemId, UpdateInspectionItemRequest request, CancellationToken ct = default)
    {
        var target = await _db.InspectionItems
            .Where(it => it.Id == itemId && it.InspectionId == inspectionId && it.PortfolioId == portfolioId)
            .Select(it => new { Item = it, InspectionStatus = it.Inspection!.Status })
            .FirstOrDefaultAsync(ct);
        if (target == null)
        {
            return null;
        }

        EnsureChecklistItemEditable(target.InspectionStatus);

        var item = target.Item;
        if (request.Area != null || request.Label != null)
        {
            var normalized = NormalizeInspectionItemText(
                request.Area ?? item.Area,
                request.Label ?? item.Label,
                item.SortOrder + 1);
            item.Area = normalized.Area;
            item.Label = normalized.Label;
        }
        if (request.Result.HasValue) item.Result = request.Result.Value;
        if (request.Note != null) item.Note = request.Note;

        await TouchInspectionAsync(inspectionId, portfolioId, ct);
        await _db.SaveChangesAsync(ct);

        return InspectionItemResponse.FromEntity(item);
    }

    public async Task<InspectionItemResponse?> UpdateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        UpdateInspectionItemRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Update, inspectionId, itemId, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionItemResponse>(outcome.Value);
    }

    internal async Task<bool> DeleteItemAsync(int portfolioId, int inspectionId, int itemId, CancellationToken ct = default)
    {
        var target = await _db.InspectionItems
            .Where(it => it.Id == itemId && it.InspectionId == inspectionId && it.PortfolioId == portfolioId)
            .Select(it => new { Item = it, InspectionStatus = it.Inspection!.Status })
            .FirstOrDefaultAsync(ct);
        if (target == null)
        {
            return false;
        }

        EnsureChecklistItemEditable(target.InspectionStatus);

        _db.InspectionItems.Remove(target.Item);
        await TouchInspectionAsync(inspectionId, portfolioId, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Delete, inspectionId, itemId, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return outcome.Value.Found;
    }

    internal async Task<IReadOnlyList<InspectionItemResponse>?> ReorderItemsAsync(int portfolioId, int inspectionId, ReorderInspectionItemsRequest request, CancellationToken ct = default)
    {
        var requestedIds = (request.ItemIds ?? []).ToArray();
        if (requestedIds.Length == 0)
        {
            throw new DomainValidationException("Include the checklist questions in the order they should appear.");
        }
        if (requestedIds.Any(id => id <= 0))
        {
            throw new DomainValidationException("Checklist question ids must be positive.");
        }
        if (requestedIds.Distinct().Count() != requestedIds.Length)
        {
            throw new DomainValidationException("Each checklist question can appear only once in the new order.");
        }

        var validation = await ValidateInspectionItemOrderAsync(
            portfolioId, inspectionId, requestedIds, ct);
        if (!validation.InspectionExists)
        {
            return null;
        }

        EnsureChecklistItemEditable((InspectionStatus)validation.InspectionStatus);
        if (!validation.IsValid)
        {
            throw new DomainValidationException("Reorder request must include every checklist question exactly once.");
        }

        await ApplyInspectionItemOrderAsync(
            portfolioId, inspectionId, requestedIds, _timeProvider.UtcNow(), ct);

        var reordered = await _db.InspectionItems
            .AsNoTracking()
            .Where(item => item.InspectionId == inspectionId && item.PortfolioId == portfolioId)
            .OrderBy(it => it.SortOrder)
            .ThenBy(it => it.Id)
            .ToListAsync(ct);
        return reordered.Select(InspectionItemResponse.FromEntity).ToList();
    }

    public async Task<IReadOnlyList<InspectionItemResponse>?> ReorderItemsAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        ReorderInspectionItemsRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Reorder, inspectionId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<IReadOnlyList<InspectionItemResponse>>(outcome.Value);
    }

    internal async Task<InspectionItemResponse?> AttachItemPhotoAsync(int portfolioId, int inspectionId, int itemId, int storedFileId, CancellationToken ct = default)
    {
        var target = await _db.InspectionItems
            .Where(it => it.Id == itemId && it.InspectionId == inspectionId && it.PortfolioId == portfolioId)
            .Select(it => new { Item = it, InspectionStatus = it.Inspection!.Status })
            .FirstOrDefaultAsync(ct);
        if (target == null)
        {
            return null;
        }

        // The file must be a real, active StoredFile in this portfolio (IDOR guard).
        var fileExists = await _db.StoredFiles
            .AnyAsync(f => f.Id == storedFileId && f.PortfolioId == portfolioId, ct);
        if (!fileExists)
        {
            return null;
        }

        EnsureChecklistItemEditable(target.InspectionStatus);

        var item = target.Item;
        item.PhotoStoredFileId = storedFileId;
        await TouchInspectionAsync(inspectionId, portfolioId, ct);
        await _db.SaveChangesAsync(ct);

        return InspectionItemResponse.FromEntity(item);
    }

    public async Task<InspectionItemResponse?> AttachItemPhotoAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        int storedFileId,
        string operationKey,
        CancellationToken ct = default)
    {
        var request = new AttachInspectionItemPhotoRequest { StoredFileId = storedFileId };
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.AttachPhoto, inspectionId, itemId, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionItemResponse>(outcome.Value);
    }

    public async Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Complete, id, 0, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        if (!outcome.Value.Found) return (null, null);
        if (outcome.Value.Error is not null) return (null, outcome.Value.Error);
        var summary = DeserializeSnapshot<CompleteInspectionResponse>(outcome.Value)
            ?? throw new AtomicReceiptInvariantException("Completed inspection receipt has no summary.");

        try
        {
            summary.ReportStoredFileId = await EnsureInspectionReportAsync(
                scope, id, summary, operationKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Inspection {InspectionId} report generation failed; atomic completion remains committed.", id);
        }
        return (summary, null);
    }

    internal async Task<(Stream Stream, string FileName, string ContentType)?> GetReportAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var inspection = await _db.Inspections
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (inspection?.ReportStoredFileId == null)
        {
            return null;
        }

        var file = await _db.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == inspection.ReportStoredFileId.Value && f.PortfolioId == portfolioId, ct);
        if (file == null)
        {
            return null;
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(file.FilePath, ct);
        }
        catch
        {
            return null;
        }

        return (stream, file.FileName, file.ContentType);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)?> GetReportAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var allowed = await AuthorizedInspections(scope, ReadCapabilities)
            .AnyAsync(inspection => inspection.Id == id, ct);
        return allowed ? await GetReportAsync(scope.PortfolioId, id, ct) : null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities)
        => _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());

    private IQueryable<Inspection> AuthorizedInspections(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities)
    {
        var properties = AuthorizedProperties(scope, capabilities);
        return _db.Inspections
            .AsNoTracking()
            .Where(inspection =>
                inspection.PortfolioId == scope.PortfolioId &&
                properties.Any(property =>
                    property.Id == inspection.PropertyId &&
                    property.PortfolioId == inspection.PortfolioId));
    }

    private async Task<bool> HasAllPropertiesAccessAsync(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities,
        CancellationToken ct)
        => await _db.AuthorizedAllPropertyAssignments(
                scope,
                capabilities,
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.UtcNow())
            .AnyAsync(ct);

    private async Task<InspectionItemOrderValidation> ValidateInspectionItemOrderAsync(
        int portfolioId,
        int inspectionId,
        int[] itemIds,
        CancellationToken ct)
        => await _db.Database.SqlQueryRaw<InspectionItemOrderValidation>(
                InspectionItemOrderValidationSql,
                new NpgsqlParameter("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = itemIds },
                new NpgsqlParameter("inspectionId", NpgsqlDbType.Integer) { Value = inspectionId },
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId })
            .SingleAsync(ct);

    private Task<int> ApplyInspectionItemOrderAsync(
        int portfolioId,
        int inspectionId,
        int[] itemIds,
        DateTime updatedAt,
        CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync(
            InspectionItemOrderUpdateSql,
            [
                new NpgsqlParameter("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = itemIds },
                new NpgsqlParameter("inspectionId", NpgsqlDbType.Integer) { Value = inspectionId },
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
                new NpgsqlParameter("updatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt },
            ],
            ct);

    private sealed class InspectionItemOrderValidation
    {
        public bool InspectionExists { get; set; }
        public int InspectionStatus { get; set; }
        public bool IsValid { get; set; }
    }

    private async Task<TResult?> ExecuteForAuthorizedInspectionAsync<TResult>(
        WorkspaceReadScope scope,
        int inspectionId,
        Func<CancellationToken, Task<TResult?>> mutation,
        CancellationToken ct)
        => await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var allowed = await AuthorizedInspections(scope, WriteCapabilities)
                .AnyAsync(inspection => inspection.Id == inspectionId, innerCt);
            return allowed ? await mutation(innerCt) : default;
        }, ct);

    /// <summary>
    /// Returns the (Area, Label, SortOrder) tuples for a built-in (negative id) or in-portfolio custom
    /// template. Returns null when the id is not a known built-in nor a custom template in this portfolio.
    /// </summary>
    private async Task<InspectionTemplate?> LoadCustomTemplateAsync(int portfolioId, int templateId, CancellationToken ct)
    {
        return await _db.InspectionTemplates
            .AsNoTracking()
            .Include(t => t.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .Where(t => t.Id == templateId && t.PortfolioId == portfolioId)
            .FirstOrDefaultAsync(ct);
    }

    private static (string Name, InspectionType InspectionType, List<(string Area, string Label)> Items) NormalizeTemplateRequest(
        string name,
        InspectionType inspectionType,
        IReadOnlyList<UpsertInspectionTemplateItemRequest>? items)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new DomainValidationException("Checklist name is required.");
        }
        if (normalizedName.Length > 200)
        {
            throw new DomainValidationException("Checklist name must be 200 characters or fewer.");
        }

        if (items == null || items.Count == 0)
        {
            throw new DomainValidationException("Add at least one checklist question.");
        }
        if (items.Count > MaxTemplateItems)
        {
            throw new DomainValidationException($"A checklist can have at most {MaxTemplateItems} questions.");
        }

        var normalizedItems = new List<(string Area, string Label)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var area = (item.Area ?? string.Empty).Trim();
            var label = (item.Label ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(area))
            {
                throw new DomainValidationException($"Question {i + 1} needs an area or room.");
            }
            if (area.Length > 120)
            {
                throw new DomainValidationException($"Question {i + 1} area must be 120 characters or fewer.");
            }
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new DomainValidationException($"Question {i + 1} needs a checklist item.");
            }
            if (label.Length > 300)
            {
                throw new DomainValidationException($"Question {i + 1} item must be 300 characters or fewer.");
            }

            normalizedItems.Add((area, label));
        }

        return (normalizedName, inspectionType, normalizedItems);
    }

    private static (string Area, string Label) NormalizeInspectionItemText(string? area, string? label, int questionNumber)
    {
        var normalizedArea = (area ?? string.Empty).Trim();
        var normalizedLabel = (label ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedArea))
        {
            throw new DomainValidationException($"Question {questionNumber} needs an area or room.");
        }
        if (normalizedArea.Length > 120)
        {
            throw new DomainValidationException($"Question {questionNumber} area must be 120 characters or fewer.");
        }
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            throw new DomainValidationException($"Question {questionNumber} needs a checklist item.");
        }
        if (normalizedLabel.Length > 300)
        {
            throw new DomainValidationException($"Question {questionNumber} item must be 300 characters or fewer.");
        }

        return (normalizedArea, normalizedLabel);
    }

    private static void AddTemplateItems(InspectionTemplate template, IReadOnlyList<(string Area, string Label)> items)
    {
        template.Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            template.Items.Add(new InspectionTemplateItem
            {
                TemplateId = template.Id,
                Template = template,
                Area = items[i].Area,
                Label = items[i].Label,
                SortOrder = i,
            });
        }
    }

    private static void ThrowIfBuiltInTemplateMutation(int templateId)
    {
        if (!InspectionTemplateCatalog.IsBuiltInId(templateId))
        {
            return;
        }

        throw new DomainValidationException("Built-in checklists are read-only. Copy one to a custom checklist before editing.");
    }

    private async Task<List<(string, string, int)>?> ResolveTemplateItemsAsync(int portfolioId, int templateId, CancellationToken ct)
    {
        if (InspectionTemplateCatalog.IsBuiltInId(templateId))
        {
            var builtIn = InspectionTemplateCatalog.FindBuiltIn(templateId);
            if (builtIn == null)
            {
                return null;
            }
            return builtIn.Items
                .OrderBy(i => i.SortOrder)
                .Select(i => (i.Area, i.Label, i.SortOrder))
                .ToList();
        }

        var customExists = await _db.InspectionTemplates
            .AsNoTracking()
            .AnyAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (!customExists)
        {
            return null;
        }

        return await _db.InspectionTemplateItems
            .AsNoTracking()
            .Where(i => i.TemplateId == templateId)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .Select(i => new ValueTuple<string, string, int>(i.Area, i.Label, i.SortOrder))
            .ToListAsync(ct);
    }

    private static void EnsureChecklistItemEditable(InspectionStatus inspectionStatus)
    {
        if (inspectionStatus == InspectionStatus.Completed)
        {
            throw new DomainValidationException(
                "This completed inspection is read-only. Reopen or schedule a new inspection before changing checklist items.",
                statusCode: 409);
        }
    }

    private async Task TouchInspectionAsync(int inspectionId, int portfolioId, CancellationToken ct)
    {
        var inspection = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == inspectionId && i.PortfolioId == portfolioId, ct);
        if (inspection != null)
        {
            inspection.UpdatedAt = _timeProvider.UtcNow();
        }
    }

    private async Task<int?> EnsureInspectionReportAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        CompleteInspectionResponse summary,
        string operationKey,
        CancellationToken ct)
    {
        var header = await AuthorizedInspections(scope, WriteCapabilities)
            .Where(inspection => inspection.Id == inspectionId)
            .Select(inspection => new InspectionReportHeader
            {
                ReportStoredFileId = inspection.ReportStoredFileId,
                PropertyId = inspection.PropertyId,
                PropertyName = inspection.Property!.Name,
                AddressLine1 = inspection.Property.AddressLine1,
                City = inspection.Property.City,
                State = inspection.Property.State,
                PostalCode = inspection.Property.PostalCode,
                UnitNumber = inspection.Unit == null ? null : inspection.Unit.UnitNumber,
                Type = inspection.Type,
                Status = inspection.Status,
                ScheduledFor = inspection.ScheduledFor,
                CompletedAt = inspection.CompletedAt,
                Inspector = inspection.Inspector,
            })
            .SingleOrDefaultAsync(ct);
        if (header is null) return null;
        if (header.ReportStoredFileId.HasValue) return header.ReportStoredFileId.Value;

        var authorized = AuthorizedInspections(scope, WriteCapabilities);
        var items = await _db.InspectionItems.AsNoTracking()
            .Where(item => item.PortfolioId == scope.PortfolioId
                && item.InspectionId == inspectionId
                && authorized.Any(inspection => inspection.Id == item.InspectionId))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new InspectionReportItem
            {
                Id = item.Id,
                Area = item.Area,
                Label = item.Label,
                Result = item.Result,
                Note = item.Note,
                SpawnedWorkOrderId = item.SpawnedWorkOrderId,
            })
            .ToListAsync(ct);
        var photoReferences = await _db.InspectionItems.AsNoTracking()
            .Where(item => item.PortfolioId == scope.PortfolioId
                && item.InspectionId == inspectionId
                && item.PhotoStoredFileId != null
                && item.PhotoStoredFile != null
                && item.PhotoStoredFile.DeletedAt == null
                && authorized.Any(inspection => inspection.Id == item.InspectionId))
            .Select(item => new InspectionPhotoReference(item.Id, item.PhotoStoredFile!.FilePath))
            .ToListAsync(ct);

        var photos = new Dictionary<int, byte[]>();
        foreach (var photo in photoReferences)
        {
            try
            {
                await using var stream = await _storage.DownloadAsync(photo.FilePath, ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                photos[photo.ItemId] = buffer.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not load photo for inspection item {ItemId}; omitting from report.", photo.ItemId);
            }
        }

        var data = new InspectionReportData
        {
            Type = header.Type,
            Status = header.Status,
            ScheduledFor = header.ScheduledFor,
            CompletedAt = header.CompletedAt,
            Inspector = header.Inspector,
            TotalItems = summary.TotalItems,
            PassCount = summary.PassCount,
            FailCount = summary.FailCount,
            NotApplicableCount = summary.NotApplicableCount,
            PendingCount = summary.PendingCount,
            Items = items,
            PropertyLine = $"{header.PropertyName} — {header.AddressLine1}, {header.City}, {header.State} {header.PostalCode}",
            UnitLine = header.UnitNumber is null ? null : $"Unit {header.UnitNumber}",
            PhotosByItemId = photos,
        };
        var pdfBytes = _pdf.Generate(data);
        var fileName = $"inspection-{inspectionId}-report.pdf";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{scope.PortfolioId}:{inspectionId}:{operationKey}"))).ToLowerInvariant()[..24];
        var storagePath = $"inspection-{inspectionId}-{digest}-report.pdf";
        await using (var content = new MemoryStream(pdfBytes))
            await _storage.UploadAtAsync(content, storagePath, fileName, "application/pdf", ct);

        var request = new AttachInspectionReportRequest(
            fileName, storagePath, "application/pdf", pdfBytes.LongLength);
        var command = AtomicInspectionMutation.Command(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.AttachReport, inspectionId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        if (!outcome.Value.Found)
            throw new AtomicReceiptInvariantException("Inspection disappeared before its report was attached.");
        if (outcome.Value.Error is not null)
            throw new DomainValidationException(outcome.Value.Error);
        return outcome.Value.ResponseJson is null
            ? throw new AtomicReceiptInvariantException("Inspection report receipt has no StoredFile id.")
            : JsonSerializer.Deserialize<int>(outcome.Value.ResponseJson);
    }

    private sealed class InspectionReportHeader
    {
        public int? ReportStoredFileId { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string AddressLine1 { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string? UnitNumber { get; init; }
        public InspectionType Type { get; init; }
        public InspectionStatus Status { get; init; }
        public DateTime ScheduledFor { get; init; }
        public DateTime? CompletedAt { get; init; }
        public string? Inspector { get; init; }
    }

    private sealed record InspectionPhotoReference(int ItemId, string FilePath);
}
