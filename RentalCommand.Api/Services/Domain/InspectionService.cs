using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
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
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _storage = storage;
        _pdf = pdf;
        _logger = logger;
        _timeProvider = timeProvider;
    }

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
            .Include(i => i.Property)
            .Include(i => i.Unit)
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
            AuthorizedInspections(scope, ReadCapabilities)
                .Include(i => i.Property)
                .Include(i => i.Unit),
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
            .ToListAsync(ct);

        return new InspectionListResponse
        {
            Items = items.Select(InspectionResponse.FromEntity).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal async Task<InspectionDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Inspections
            .AsNoTracking()
            .Include(i => i.Property)
            .Include(i => i.Unit)
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var items = await _db.InspectionItems
            .AsNoTracking()
            .Where(it => it.InspectionId == id && it.PortfolioId == portfolioId)
            .OrderBy(it => it.SortOrder)
            .ThenBy(it => it.Id)
            .ToListAsync(ct);

        return InspectionDetailResponse.FromEntity(entity, items);
    }

    public async Task<InspectionDetailResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var entity = await AuthorizedInspections(scope, ReadCapabilities)
            .Include(i => i.Property)
            .Include(i => i.Unit)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (entity == null)
        {
            return null;
        }

        var items = await _db.InspectionItems
            .AsNoTracking()
            .Where(item => item.InspectionId == id && item.PortfolioId == scope.PortfolioId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        return InspectionDetailResponse.FromEntity(entity, items);
    }

    internal async Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct = default)
    {
        // Built-ins (code-defined) first, then any portfolio-custom templates from the DB.
        var result = InspectionTemplateCatalog.BuiltIns
            .Select(InspectionTemplateResponse.FromEntity)
            .ToList();

        var custom = await _db.InspectionTemplates
            .AsNoTracking()
            .Include(t => t.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .Where(t => t.PortfolioId == portfolioId)
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        result.AddRange(custom.Select(InspectionTemplateResponse.FromEntity));
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

        var custom = await LoadCustomTemplateAsync(portfolioId, templateId, ct);
        return custom == null ? null : InspectionTemplateResponse.FromEntity(custom);
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
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
            await HasAllPropertiesAccessAsync(scope, WriteCapabilities, innerCt)
                ? await CreateTemplateCoreAsync(scope.PortfolioId, request, broadcast: false, innerCt)
                : null,
            ct);
        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, TemplateEntityType, response.Id, response, ct);
        }
        return response;
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
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
            await HasAllPropertiesAccessAsync(scope, WriteCapabilities, innerCt)
                ? await UpdateTemplateCoreAsync(scope.PortfolioId, templateId, request, broadcast: false, innerCt)
                : null,
            ct);
        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, TemplateEntityType, response.Id, response, ct);
        }
        return response;
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
        CancellationToken ct = default)
    {
        var deleted = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
            await HasAllPropertiesAccessAsync(scope, WriteCapabilities, innerCt) &&
            await DeleteTemplateCoreAsync(scope.PortfolioId, templateId, broadcast: false, innerCt),
            ct);
        if (deleted)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(
                scope.PortfolioId, TemplateEntityType, templateId, ct);
        }
        return deleted;
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
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var allowed = await AuthorizedProperties(scope, WriteCapabilities)
                .AnyAsync(property => property.Id == request.PropertyId, innerCt);
            return allowed
                ? await CreateCoreAsync(scope.PortfolioId, request, broadcast: false, innerCt)
                : null;
        }, ct);
        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }
        return response;
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
        CancellationToken ct = default)
    {
        var response = await ExecuteForAuthorizedInspectionAsync(
            scope, id,
            innerCt => UpdateCoreAsync(scope.PortfolioId, id, request, broadcast: false, innerCt), ct);
        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                scope.PortfolioId, EntityType, response.Id, response, ct);
        }
        return response;
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
        CancellationToken ct = default)
    {
        var deleted = await ExecuteForAuthorizedInspectionAsync(
            scope, id,
            innerCt => DeleteCoreAsync(scope.PortfolioId, id, broadcast: false, innerCt), ct);
        if (deleted)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        }
        return deleted;
    }

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

    public Task<InspectionItemResponse?> CreateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        CreateInspectionItemRequest request,
        CancellationToken ct = default)
        => ExecuteForAuthorizedInspectionAsync(
            scope, inspectionId,
            innerCt => CreateItemAsync(scope.PortfolioId, inspectionId, request, innerCt), ct);

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

    public Task<InspectionItemResponse?> UpdateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        UpdateInspectionItemRequest request,
        CancellationToken ct = default)
        => ExecuteForAuthorizedInspectionAsync(
            scope, inspectionId,
            innerCt => UpdateItemAsync(scope.PortfolioId, inspectionId, itemId, request, innerCt), ct);

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
        CancellationToken ct = default)
        => await ExecuteForAuthorizedInspectionAsync(
            scope, inspectionId,
            innerCt => DeleteItemAsync(scope.PortfolioId, inspectionId, itemId, innerCt), ct);

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

    public Task<IReadOnlyList<InspectionItemResponse>?> ReorderItemsAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        ReorderInspectionItemsRequest request,
        CancellationToken ct = default)
        => ExecuteForAuthorizedInspectionAsync(
            scope, inspectionId,
            innerCt => ReorderItemsAsync(scope.PortfolioId, inspectionId, request, innerCt), ct);

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

    public Task<InspectionItemResponse?> AttachItemPhotoAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        int storedFileId,
        CancellationToken ct = default)
        => ExecuteForAuthorizedInspectionAsync(
            scope, inspectionId,
            innerCt => AttachItemPhotoAsync(scope.PortfolioId, inspectionId, itemId, storedFileId, innerCt), ct);

    internal Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAsync(int portfolioId, int id, int userId, CancellationToken ct = default)
        => CompleteCoreAsync(portfolioId, id, userId, broadcast: true, ct);

    private async Task<(CompleteInspectionResponse? Result, string? Error)> CompleteCoreAsync(
        int portfolioId,
        int id,
        int userId,
        bool broadcast,
        CancellationToken ct)
    {
        var inspection = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (inspection == null)
        {
            return (null, null); // not found → controller maps to 404
        }

        if (inspection.Status == InspectionStatus.Completed)
        {
            return (null, "Inspection is already completed.");
        }

        var items = await _db.InspectionItems
            .Where(it => it.InspectionId == id && it.PortfolioId == portfolioId)
            .OrderBy(it => it.SortOrder).ThenBy(it => it.Id)
            .ToListAsync(ct);

        // A completed inspection must have actually inspected something. Block completing an
        // inspection with no checklist items, or one where every item is still Pending (nothing
        // was walked). Guards meaningless "Completed, 0 PASS / 0 FAIL" records that are useless
        // for move-out disputes and owner reports.
        if (items.Count == 0)
        {
            return (null, "Add a checklist (pick a template) before completing this inspection — a completed inspection must record what was inspected.");
        }
        if (items.All(it => it.Result == InspectionItemResult.Pending))
        {
            return (null, "Mark at least one checklist item Pass, Fail, or N/A before completing — a completed inspection must record what was inspected.");
        }

        var now = _timeProvider.UtcNow();
        var failedItems = items.Where(it => it.Result == InspectionItemResult.Fail).ToList();
        var workOrderIdByItemId = new Dictionary<int, int>();
        var newWorkOrderLinks = new List<(InspectionItem Item, WorkOrder WorkOrder)>();

        // ---- Auto-create a work order per Fail item (links SpawnedWorkOrderId + initial status event). ----
        foreach (var item in failedItems)
        {
            // Skip if this item already spawned a work order (idempotency on re-entry).
            if (item.SpawnedWorkOrderId.HasValue)
            {
                workOrderIdByItemId[item.Id] = item.SpawnedWorkOrderId.Value;
                continue;
            }

            var workOrder = BuildInspectionWorkOrder(
                portfolioId,
                inspection,
                item,
                now,
                userId);

            item.SpawnedWorkOrder = workOrder;
            _db.WorkOrders.Add(workOrder);
            newWorkOrderLinks.Add((item, workOrder));
        }

        // ---- Status + completion timestamp (saved with the spawned-WO links). ----
        inspection.Status = InspectionStatus.Completed;
        inspection.CompletedAt ??= now;
        inspection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        foreach (var (item, workOrder) in newWorkOrderLinks)
        {
            workOrderIdByItemId[item.Id] = workOrder.Id;
        }

        var createdWorkOrderIds = failedItems
            .Where(item => workOrderIdByItemId.ContainsKey(item.Id))
            .Select(item => workOrderIdByItemId[item.Id])
            .ToList();

        var newWorkOrderIds = newWorkOrderLinks
            .Select(link => link.WorkOrder.Id)
            .ToList();
        if (broadcast)
        {
            foreach (var workOrder in await LoadWorkOrderBroadcastsAsync(portfolioId, newWorkOrderIds, ct))
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, WorkOrderEntityType, workOrder.Id, workOrder, ct);
            }
        }

        // ---- Generate + store the PDF report (commit DB rows first, then blob). ----
        int? reportFileId = null;
        try
        {
            reportFileId = await GenerateAndStoreReportAsync(portfolioId, inspection, items, createdWorkOrderIds, ct);
            if (reportFileId.HasValue)
            {
                inspection.ReportStoredFileId = reportFileId;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Report generation must never block completion: the inspection is already Completed and the
            // work orders are created. Log and return without a report id (the download will 404 until regenerated).
            _logger.LogError(ex, "Inspection {InspectionId} report generation failed; completion stands.", id);
        }

        var summary = new CompleteInspectionResponse
        {
            InspectionId = id,
            Status = inspection.Status,
            TotalItems = items.Count,
            PassCount = items.Count(it => it.Result == InspectionItemResult.Pass),
            FailCount = items.Count(it => it.Result == InspectionItemResult.Fail),
            NotApplicableCount = items.Count(it => it.Result == InspectionItemResult.NotApplicable),
            PendingCount = items.Count(it => it.Result == InspectionItemResult.Pending),
            ReportStoredFileId = reportFileId,
            CreatedWorkOrderIds = createdWorkOrderIds,
        };

        if (broadcast)
        {
            var response = InspectionResponse.FromEntity(inspection);
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, inspection.Id, response, ct);
        }
        return (summary, null);
    }

    public async Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        CancellationToken ct = default)
    {
        var outcome = await ExecuteForAuthorizedInspectionAsync(
            scope, id,
            innerCt => CompleteCoreAsync(scope.PortfolioId, id, userId, broadcast: false, innerCt), ct);
        if (outcome.Result is not null)
        {
            foreach (var workOrder in await LoadWorkOrderBroadcastsAsync(
                         scope.PortfolioId, outcome.Result.CreatedWorkOrderIds, ct))
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(
                    scope.PortfolioId, WorkOrderEntityType, workOrder.Id, workOrder, ct);
            }

            var inspection = await GetAuthorizedAsync(scope, id, ct);
            if (inspection is not null)
            {
                await _dataUpdate.BroadcastEntityUpdateAsync(
                    scope.PortfolioId, EntityType, inspection.Id, inspection, ct);
            }
        }
        return outcome;
    }

    private static WorkOrder BuildInspectionWorkOrder(
        int portfolioId,
        Inspection inspection,
        InspectionItem item,
        DateTime now,
        int changedByUserId)
    {
        var title = string.IsNullOrWhiteSpace(item.Label) ? "Inspection follow-up" : item.Label.Trim();
        var area = string.IsNullOrWhiteSpace(item.Area) ? "General" : item.Area.Trim();
        var description = string.IsNullOrWhiteSpace(item.Note)
            ? $"Failed inspection item ({area}) from inspection #{inspection.Id}."
            : item.Note.Trim();

        var workOrder = new WorkOrder
        {
            PortfolioId = portfolioId,
            PropertyId = inspection.PropertyId,
            UnitId = inspection.UnitId,
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
            PortfolioId = portfolioId,
            FromStatus = null,
            ToStatus = workOrder.Status,
            Note = null,
            ChangedByUserId = changedByUserId,
            ChangedByLabel = "Inspection",
            CreatedAtUtc = now,
        });

        return workOrder;
    }

    private async Task<IReadOnlyList<WorkOrderResponse>> LoadWorkOrderBroadcastsAsync(
        int portfolioId,
        IReadOnlyCollection<int> workOrderIds,
        CancellationToken ct)
    {
        if (workOrderIds.Count == 0)
        {
            return [];
        }

        var rows = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && workOrderIds.Contains(w.Id))
            .Select(w => new InspectionWorkOrderBroadcastRow(
                w,
                w.Property != null ? w.Property.Name : null,
                w.Unit != null ? w.Unit.UnitNumber : null,
                w.Vendor != null ? w.Vendor.Name : null,
                w.Tenant != null ? ((w.Tenant.FirstName + " " + w.Tenant.LastName)).Trim() : null))
            .ToListAsync(ct);

        var responseById = rows.ToDictionary(row => row.WorkOrder.Id, ToWorkOrderBroadcast);
        return workOrderIds
            .Where(responseById.ContainsKey)
            .Select(id => responseById[id])
            .ToList();
    }

    private sealed record InspectionWorkOrderBroadcastRow(
        WorkOrder WorkOrder,
        string? PropertyName,
        string? UnitNumber,
        string? VendorName,
        string? TenantName);

    private static WorkOrderResponse ToWorkOrderBroadcast(InspectionWorkOrderBroadcastRow row)
    {
        var response = WorkOrderResponse.FromEntity(row.WorkOrder);
        response.PropertyName = row.PropertyName;
        response.UnitNumber = row.UnitNumber;
        response.VendorName = row.VendorName;
        response.TenantName = row.TenantName;
        return response;
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

    private async Task<int?> GenerateAndStoreReportAsync(
        int portfolioId,
        Inspection inspection,
        IReadOnlyList<InspectionItem> items,
        IReadOnlyList<int> createdWorkOrderIds,
        CancellationToken ct)
    {
        var property = await _db.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == inspection.PropertyId && p.PortfolioId == portfolioId, ct);

        string? unitLine = null;
        if (inspection.UnitId.HasValue)
        {
            var unit = await _db.Units
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == inspection.UnitId.Value, ct);
            if (unit != null)
            {
                unitLine = $"Unit {unit.UnitNumber}";
            }
        }

        var propertyLine = property == null
            ? $"Property #{inspection.PropertyId}"
            : $"{property.Name} — {property.AddressLine1}, {property.City}, {property.State} {property.PostalCode}";

        // Load photo bytes for items that have a photo (best-effort; skip any that fail to load).
        // Resolve the StoredFile metadata for every photo in ONE query (keyed by file id), instead of
        // a FirstOrDefaultAsync per item (N+1). The per-file binary download still happens in the loop
        // — that's an unavoidable per-object stream fetch from blob storage, not a DB round-trip.
        var photos = new Dictionary<int, byte[]>();
        var photoItems = items.Where(it => it.PhotoStoredFileId.HasValue).ToList();
        if (photoItems.Count > 0)
        {
            var photoFileIds = photoItems.Select(it => it.PhotoStoredFileId!.Value).Distinct().ToList();
            var filePathById = await _db.StoredFiles
                .AsNoTracking()
                .Where(f => photoFileIds.Contains(f.Id) && f.PortfolioId == portfolioId)
                .Select(f => new { f.Id, f.FilePath })
                .ToDictionaryAsync(f => f.Id, f => f.FilePath, ct);

            foreach (var item in photoItems)
            {
                if (!filePathById.TryGetValue(item.PhotoStoredFileId!.Value, out var filePath)) continue;

                try
                {
                    await using var s = await _storage.DownloadAsync(filePath, ct);
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms, ct);
                    photos[item.Id] = ms.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load photo for inspection item {ItemId}; omitting from report.", item.Id);
                }
            }
        }

        var workOrderByItem = items
            .Where(it => it.SpawnedWorkOrderId.HasValue)
            .ToDictionary(it => it.Id, it => it.SpawnedWorkOrderId!.Value);

        var data = new InspectionReportData
        {
            Inspection = inspection,
            Items = items,
            PropertyLine = propertyLine,
            UnitLine = unitLine,
            PhotosByItemId = photos,
            WorkOrderIdByItemId = workOrderByItem,
        };

        var pdfBytes = _pdf.Generate(data);

        // Commit the StoredFile row BEFORE writing the blob name is recorded — here we write the blob
        // first to get its key, then persist the row, then clean up the blob if the row fails.
        var fileName = $"inspection-{inspection.Id}-report.pdf";
        string storageKey;
        await using (var ms = new MemoryStream(pdfBytes))
        {
            storageKey = await _storage.UploadAsync(ms, fileName, "application/pdf", ct);
        }

        var stored = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = pdfBytes.Length,
            EntityType = EntityType,
            EntityId = inspection.Id,
            UploadedAt = _timeProvider.UtcNow(),
        };

        try
        {
            _db.StoredFiles.Add(stored);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await _storage.DeleteAsync(storageKey, ct); } catch { /* best-effort */ }
            throw;
        }

        return stored.Id;
    }
}
