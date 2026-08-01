using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IInspectionService"/>
public class InspectionService : IInspectionService
{
    private const string EntityType = "Inspection";
    private static readonly string[] ReadCapabilities = [CapabilityKeys.WorkRead];
    private static readonly string[] WriteCapabilities = [CapabilityKeys.WorkManage];

    private readonly RentalCommandDbContext _db;
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
        _ = dataUpdate;
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

        if (query is InspectionListQuery { UnitId: int unitId })
        {
            q = q.Where(i => i.UnitId == unitId);
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

    public async Task<InspectionTemplateResponse?> CreateTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateInspectionTemplateRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Create, 0, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionTemplateResponse>(outcome.Value);
    }

    public async Task<InspectionTemplateResponse?> UpdateTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        int templateId,
        UpdateInspectionTemplateRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Update, templateId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionTemplateResponse>(outcome.Value);
    }

    public async Task<bool> DeleteTemplateAuthorizedAsync(
        WorkspaceReadScope scope,
        int templateId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Template,
            AtomicInspectionMutationOperation.Delete, templateId, 0, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return outcome.Value.Found;
    }

    public async Task<InspectionDetailResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateInspectionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Create, 0, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionDetailResponse>(outcome.Value);
    }

    public async Task<InspectionResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateInspectionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Update, id, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
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

    public async Task<InspectionItemResponse?> CreateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        CreateInspectionItemRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Create, inspectionId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionItemResponse>(outcome.Value);
    }

    public async Task<InspectionItemResponse?> UpdateItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        UpdateInspectionItemRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Update, inspectionId, itemId, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<InspectionItemResponse>(outcome.Value);
    }

    public async Task<bool> DeleteItemAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        int itemId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Delete, inspectionId, itemId, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return outcome.Value.Found;
    }

    public async Task<IReadOnlyList<InspectionItemResponse>?> ReorderItemsAuthorizedAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        ReorderInspectionItemsRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Item,
            AtomicInspectionMutationOperation.Reorder, inspectionId, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        return DeserializeSnapshot<IReadOnlyList<InspectionItemResponse>>(outcome.Value);
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
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Item,
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
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
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

    public async Task<(RecoverInspectionChronologyResponse? Result, string? Error)> RecoverChronologyAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        RecoverInspectionChronologyRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.RecoverChronology, id, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicInspectionMutation.Identity(command), command, AtomicInspectionMutation.Codec, ct);
        if (!outcome.Value.Found) return (null, null);
        if (outcome.Value.Error is not null) return (null, outcome.Value.Error);
        var result = DeserializeSnapshot<RecoverInspectionChronologyResponse>(outcome.Value)
            ?? throw new AtomicReceiptInvariantException("Inspection chronology recovery receipt has no summary.");

        var summary = await BuildCompletionSummaryAsync(scope.PortfolioId, id, result.CompletedAt, ct);
        if (summary is null) return (null, null);
        result.ReportStoredFileId = await EnsureInspectionReportAsync(scope, id, summary, operationKey, ct);
        return (result, null);
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
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.GetUtcNow().UtcDateTime);

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
                _timeProvider.GetUtcNow().UtcDateTime)
            .AnyAsync(ct);

    private async Task<CompleteInspectionResponse?> BuildCompletionSummaryAsync(
        int portfolioId,
        int inspectionId,
        DateTime completedAt,
        CancellationToken ct)
    {
        var counts = await _db.Inspections.AsNoTracking()
            .Where(inspection => inspection.PortfolioId == portfolioId
                && inspection.Id == inspectionId
                && inspection.Status == InspectionStatus.Completed
                && inspection.CompletedAt == completedAt)
            .Select(inspection => new
            {
                inspection.Id,
                inspection.Status,
                inspection.ReportStoredFileId,
                TotalItems = inspection.Items.Count(),
                PassCount = inspection.Items.Count(item => item.Result == InspectionItemResult.Pass),
                FailCount = inspection.Items.Count(item => item.Result == InspectionItemResult.Fail),
                NotApplicableCount = inspection.Items.Count(item => item.Result == InspectionItemResult.NotApplicable),
                PendingCount = inspection.Items.Count(item => item.Result == InspectionItemResult.Pending),
            })
            .SingleOrDefaultAsync(ct);
        if (counts is null) return null;

        var workOrderIds = await _db.InspectionItems.AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId
                && item.InspectionId == inspectionId
                && item.Result == InspectionItemResult.Fail
                && item.SpawnedWorkOrderId != null)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => item.SpawnedWorkOrderId!.Value)
            .ToListAsync(ct);

        return new CompleteInspectionResponse
        {
            InspectionId = counts.Id,
            Status = counts.Status,
            TotalItems = counts.TotalItems,
            PassCount = counts.PassCount,
            FailCount = counts.FailCount,
            NotApplicableCount = counts.NotApplicableCount,
            PendingCount = counts.PendingCount,
            ReportStoredFileId = counts.ReportStoredFileId,
            CreatedWorkOrderIds = workOrderIds,
        };
    }

    private async Task<int?> EnsureInspectionReportAsync(
        WorkspaceReadScope scope,
        int inspectionId,
        CompleteInspectionResponse summary,
        string operationKey,
        CancellationToken ct)
    {
        var header = await _db.Inspections
            .AsNoTracking()
            .Where(inspection => inspection.PortfolioId == scope.PortfolioId
                && inspection.Id == inspectionId)
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

        var items = await _db.InspectionItems.AsNoTracking()
            .Where(item => item.PortfolioId == scope.PortfolioId
                && item.InspectionId == inspectionId)
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
                && item.PhotoStoredFile.DeletedAt == null)
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
        var command = CreateAtomicCommand(scope, AtomicInspectionMutationDomain.Inspection,
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

    private AtomicInspectionMutationCommand CreateAtomicCommand<TRequest>(
        WorkspaceReadScope scope,
        AtomicInspectionMutationDomain domain,
        AtomicInspectionMutationOperation operation,
        int entityId,
        int relatedEntityId,
        string operationKey,
        TRequest request) =>
        AtomicInspectionMutation.Command(
            scope,
            domain,
            operation,
            entityId,
            relatedEntityId,
            operationKey,
            request,
            _timeProvider.GetUtcNow().UtcDateTime);

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
