using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IInspectionService"/>
public class InspectionService : IInspectionService
{
    private const string EntityType = "Inspection";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IWorkOrderService _workOrders;
    private readonly IFileStorage _storage;
    private readonly IInspectionReportPdfGenerator _pdf;
    private readonly ILogger<InspectionService> _logger;

    public InspectionService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IWorkOrderService workOrders,
        IFileStorage storage,
        IInspectionReportPdfGenerator pdf,
        ILogger<InspectionService> logger)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _workOrders = workOrders;
        _storage = storage;
        _pdf = pdf;
        _logger = logger;
    }

    public async Task<IReadOnlyList<InspectionResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Inspections
            .AsNoTracking()
            .Include(i => i.Property)
            .Include(i => i.Unit)
            .Where(i => i.PortfolioId == portfolioId);

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

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(InspectionResponse.FromEntity).ToList();
    }

    public async Task<InspectionDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
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
            .ToListAsync(ct);

        return InspectionDetailResponse.FromEntity(entity, items);
    }

    public async Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct = default)
    {
        // Built-ins (code-defined) first, then any portfolio-custom templates from the DB.
        var result = InspectionTemplateCatalog.BuiltIns
            .Select(InspectionTemplateResponse.FromEntity)
            .ToList();

        var custom = await _db.InspectionTemplates
            .AsNoTracking()
            .Include(t => t.Items)
            .Where(t => t.PortfolioId == portfolioId)
            .ToListAsync(ct);

        result.AddRange(custom.Select(InspectionTemplateResponse.FromEntity));
        return result;
    }

    public async Task<InspectionDetailResponse?> CreateAsync(int portfolioId, CreateInspectionRequest request, CancellationToken ct = default)
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

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
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

        var now = DateTime.UtcNow;
        var entity = new Inspection
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            LeaseId = request.LeaseId,
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
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<InspectionResponse?> UpdateAsync(int portfolioId, int id, UpdateInspectionRequest request, CancellationToken ct = default)
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

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
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
        if (request.LeaseId.HasValue) entity.LeaseId = request.LeaseId;
        if (request.Type.HasValue) entity.Type = request.Type.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.ScheduledFor.HasValue) entity.ScheduledFor = request.ScheduledFor.Value.ToUtc();
        if (request.CompletedAt.HasValue) entity.CompletedAt = request.CompletedAt.ToUtc();
        if (request.Outcome != null) entity.Outcome = request.Outcome;
        if (request.Notes != null) entity.Notes = request.Notes;
        if (request.Inspector != null) entity.Inspector = request.Inspector;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = InspectionResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
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

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    public async Task<InspectionItemResponse?> UpdateItemAsync(int portfolioId, int inspectionId, int itemId, UpdateInspectionItemRequest request, CancellationToken ct = default)
    {
        var item = await _db.InspectionItems
            .FirstOrDefaultAsync(it => it.Id == itemId && it.InspectionId == inspectionId && it.PortfolioId == portfolioId, ct);
        if (item == null)
        {
            return null;
        }

        if (request.Result.HasValue) item.Result = request.Result.Value;
        if (request.Note != null) item.Note = request.Note;

        await TouchInspectionAsync(inspectionId, portfolioId, ct);
        await _db.SaveChangesAsync(ct);

        return InspectionItemResponse.FromEntity(item);
    }

    public async Task<InspectionItemResponse?> AttachItemPhotoAsync(int portfolioId, int inspectionId, int itemId, int storedFileId, CancellationToken ct = default)
    {
        var item = await _db.InspectionItems
            .FirstOrDefaultAsync(it => it.Id == itemId && it.InspectionId == inspectionId && it.PortfolioId == portfolioId, ct);
        if (item == null)
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

        item.PhotoStoredFileId = storedFileId;
        await TouchInspectionAsync(inspectionId, portfolioId, ct);
        await _db.SaveChangesAsync(ct);

        return InspectionItemResponse.FromEntity(item);
    }

    public async Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAsync(int portfolioId, int id, int userId, CancellationToken ct = default)
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

        var now = DateTime.UtcNow;
        var createdWorkOrderIds = new List<int>();

        // ---- Auto-create a work order per Fail item (links SpawnedWorkOrderId + initial status event). ----
        foreach (var item in items.Where(it => it.Result == InspectionItemResult.Fail))
        {
            // Skip if this item already spawned a work order (idempotency on re-entry).
            if (item.SpawnedWorkOrderId.HasValue)
            {
                createdWorkOrderIds.Add(item.SpawnedWorkOrderId.Value);
                continue;
            }

            var title = string.IsNullOrWhiteSpace(item.Label) ? "Inspection follow-up" : item.Label.Trim();
            var area = string.IsNullOrWhiteSpace(item.Area) ? "General" : item.Area.Trim();
            var description = string.IsNullOrWhiteSpace(item.Note)
                ? $"Failed inspection item ({area}) from inspection #{id}."
                : item.Note.Trim();

            var wo = await _workOrders.CreateAsync(
                portfolioId,
                new CreateWorkOrderRequest
                {
                    PropertyId = inspection.PropertyId,
                    UnitId = inspection.UnitId,
                    Title = title.Length > 200 ? title[..200] : title,
                    Description = description.Length > 4000 ? description[..4000] : description,
                    Category = "Inspection",
                    Priority = WorkOrderPriority.Normal,
                    Status = WorkOrderStatus.New,
                    RequestedAt = now,
                },
                changedByUserId: userId,
                changedByLabel: "Inspection",
                ct);

            if (wo == null)
            {
                // A work order couldn't be created (should not happen — property is already validated).
                _logger.LogWarning("Failed to spawn work order for inspection {InspectionId} item {ItemId}", id, item.Id);
                continue;
            }

            item.SpawnedWorkOrderId = wo.Id;
            createdWorkOrderIds.Add(wo.Id);
        }

        // ---- Status + completion timestamp (saved with the spawned-WO links). ----
        inspection.Status = InspectionStatus.Completed;
        inspection.CompletedAt ??= now;
        inspection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

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

        var response = InspectionResponse.FromEntity(inspection);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, inspection.Id, response, ct);
        return (summary, null);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)?> GetReportAsync(int portfolioId, int id, CancellationToken ct = default)
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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the (Area, Label, SortOrder) tuples for a built-in (negative id) or in-portfolio custom
    /// template. Returns null when the id is not a known built-in nor a custom template in this portfolio.
    /// </summary>
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

        var custom = await _db.InspectionTemplates
            .AsNoTracking()
            .Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (custom == null)
        {
            return null;
        }

        return custom.Items
            .OrderBy(i => i.SortOrder)
            .Select(i => (i.Area, i.Label, i.SortOrder))
            .ToList();
    }

    private async Task TouchInspectionAsync(int inspectionId, int portfolioId, CancellationToken ct)
    {
        var inspection = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == inspectionId && i.PortfolioId == portfolioId, ct);
        if (inspection != null)
        {
            inspection.UpdatedAt = DateTime.UtcNow;
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
            UploadedAt = DateTime.UtcNow,
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
