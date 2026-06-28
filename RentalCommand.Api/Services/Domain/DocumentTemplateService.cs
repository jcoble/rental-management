using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentTemplateService"/>
public sealed class DocumentTemplateService : IDocumentTemplateService
{
    private readonly RentalCommandDbContext _db;
    private readonly IDocumentTemplateFieldCatalog _catalog;
    private readonly IFileStorage _files;

    public DocumentTemplateService(RentalCommandDbContext db, IDocumentTemplateFieldCatalog catalog, IFileStorage files)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
    }

    public async Task<IReadOnlyList<DocumentTemplateResponse>> ListAsync(
        int portfolioId, DocumentTemplateKind? kind, DocumentTemplateStatus? status, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, kind, status, query, ct);
        return page.Items;
    }

    public async Task<DocumentTemplateListResponse> ListPageAsync(
        int portfolioId, DocumentTemplateKind? kind, DocumentTemplateStatus? status, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.DocumentTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        if (kind.HasValue)
        {
            q = q.Where(t => t.Kind == kind.Value);
        }

        if (status.HasValue)
        {
            q = q.Where(t => t.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t =>
                EF.Functions.ILike(t.Name, $"%{term}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name),
            "kind" => query.SortDescending ? q.OrderByDescending(t => t.Kind) : q.OrderBy(t => t.Kind),
            "status" => query.SortDescending ? q.OrderByDescending(t => t.Status) : q.OrderBy(t => t.Status),
            "updatedatutc" or "updatedat" => query.SortDescending
                ? q.OrderByDescending(t => t.UpdatedAtUtc)
                : q.OrderBy(t => t.UpdatedAtUtc),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAtUtc) : q.OrderBy(t => t.CreatedAtUtc),
        };

        var totalCount = await q.CountAsync(ct);
        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        var itemIds = items.Select(t => t.Id).ToList();
        var fieldCounts = itemIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.DocumentTemplateFields
                .AsNoTracking()
                .Where(f => itemIds.Contains(f.DocumentTemplateId))
                .GroupBy(f => f.DocumentTemplateId)
                .Select(g => new { DocumentTemplateId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.DocumentTemplateId, x => x.Count, ct);

        return new DocumentTemplateListResponse
        {
            Items = items
                .Select(t => DocumentTemplateResponse.FromEntity(
                    t,
                    [],
                    fieldCounts.GetValueOrDefault(t.Id)))
                .ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<DocumentTemplateResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (template is null)
        {
            return null;
        }

        var fields = await _db.DocumentTemplateFields
            .AsNoTracking()
            .Where(f => f.DocumentTemplateId == id)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);

        return DocumentTemplateResponse.FromEntity(template, fields);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> CreateAsync(
        int portfolioId, CreateDocumentTemplateRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateReferencesAsync(
            portfolioId, request.PropertyId, request.OriginalStoredFileId, request.CompiledStoredFileId, ct);
        if (validation is not null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid(validation);
        }

        var now = DateTime.UtcNow;
        var template = new DocumentTemplate
        {
            PortfolioId = portfolioId,
            Kind = request.Kind,
            RenderMode = request.RenderMode,
            Status = DocumentTemplateStatus.Draft,
            Name = request.Name.Trim(),
            Description = NormalizeNullable(request.Description),
            OriginalStoredFileId = request.OriginalStoredFileId,
            CompiledStoredFileId = request.CompiledStoredFileId,
            DraftHtml = request.DraftHtml,
            DefaultForPortfolio = request.DefaultForPortfolio,
            PropertyId = request.PropertyId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        if (template.DefaultForPortfolio)
        {
            await ClearOtherDefaultsAsync(portfolioId, template.Kind, template.PropertyId, null, now, ct);
        }

        _db.DocumentTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        return DocumentTemplateOperationResult<DocumentTemplateResponse>.Success(
            DocumentTemplateResponse.FromEntity(template, []));
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UploadPdfAsync(
        int portfolioId,
        Stream content,
        string fileName,
        string contentType,
        long sizeBytes,
        string name,
        string? description,
        bool defaultForPortfolio,
        int? propertyId,
        CancellationToken ct = default)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(fileName)
            : name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid("Template name is required.");
        }

        var validation = await ValidateReferencesAsync(
            portfolioId, propertyId, originalFileId: null, compiledFileId: null, ct);
        if (validation is not null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid(validation);
        }

        var safeFileName = DiskFileStorage.SanitizeFileName(fileName);
        var storageKey = await _files.UploadAsync(content, safeFileName, contentType, ct);
        try
        {
            var now = DateTime.UtcNow;
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var stored = new StoredFile
            {
                PortfolioId = portfolioId,
                EntityType = "DocumentTemplate",
                FileName = safeFileName,
                FilePath = storageKey,
                ContentType = contentType,
                FileSize = sizeBytes,
                UploadedAt = now,
            };

            var template = new DocumentTemplate
            {
                PortfolioId = portfolioId,
                Kind = DocumentTemplateKind.Lease,
                RenderMode = DocumentTemplateRenderMode.Overlay,
                Status = DocumentTemplateStatus.Draft,
                Name = normalizedName,
                Description = NormalizeNullable(description),
                OriginalStoredFile = stored,
                DefaultForPortfolio = defaultForPortfolio,
                PropertyId = propertyId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            if (defaultForPortfolio)
            {
                await ClearOtherDefaultsAsync(
                    portfolioId, DocumentTemplateKind.Lease, propertyId, exceptId: null, now, ct);
            }

            _db.StoredFiles.Add(stored);
            _db.DocumentTemplates.Add(template);
            await _db.SaveChangesAsync(ct);

            stored.EntityId = template.Id;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Success(
                DocumentTemplateResponse.FromEntity(template, []));
        }
        catch
        {
            try { await _files.DeleteAsync(storageKey, ct); } catch { /* best-effort orphan cleanup */ }
            throw;
        }
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UpdateAsync(
        int portfolioId, int id, UpdateDocumentTemplateRequest request, CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (template is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.NotFound("Document template not found");
        }

        var propertyId = request.PropertyId ?? template.PropertyId;
        var originalFileId = request.OriginalStoredFileId ?? template.OriginalStoredFileId;
        var compiledFileId = request.CompiledStoredFileId ?? template.CompiledStoredFileId;
        var validation = await ValidateReferencesAsync(portfolioId, propertyId, originalFileId, compiledFileId, ct);
        if (validation is not null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid(validation);
        }

        if (request.Name is not null) template.Name = request.Name.Trim();
        if (request.Description is not null) template.Description = NormalizeNullable(request.Description);
        if (request.Status.HasValue)
        {
            template.Status = request.Status.Value;
            template.ArchivedAtUtc = request.Status.Value == DocumentTemplateStatus.Archived
                ? DateTime.UtcNow
                : null;
        }
        if (request.RenderMode.HasValue) template.RenderMode = request.RenderMode.Value;
        if (request.OriginalStoredFileId.HasValue) template.OriginalStoredFileId = request.OriginalStoredFileId;
        if (request.CompiledStoredFileId.HasValue) template.CompiledStoredFileId = request.CompiledStoredFileId;
        if (request.PropertyId.HasValue) template.PropertyId = request.PropertyId;
        if (request.DraftHtml is not null) template.DraftHtml = request.DraftHtml;
        if (request.DefaultForPortfolio.HasValue) template.DefaultForPortfolio = request.DefaultForPortfolio.Value;

        var now = DateTime.UtcNow;
        template.UpdatedAtUtc = now;
        template.Version++;

        if (template.DefaultForPortfolio)
        {
            await ClearOtherDefaultsAsync(portfolioId, template.Kind, template.PropertyId, template.Id, now, ct);
        }

        await _db.SaveChangesAsync(ct);

        var fields = await _db.DocumentTemplateFields
            .AsNoTracking()
            .Where(f => f.DocumentTemplateId == template.Id)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);

        return DocumentTemplateOperationResult<DocumentTemplateResponse>.Success(
            DocumentTemplateResponse.FromEntity(template, fields));
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> AddFieldAsync(
        int portfolioId, int templateId, CreateDocumentTemplateFieldRequest request, CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (template is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.NotFound("Document template not found");
        }

        var extentError = ValidateExtent(request.XPct, request.YPct, request.WidthPct, request.HeightPct);
        if (extentError is not null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Invalid(extentError);
        }

        var catalogItem = _catalog.Find(template.Kind, request.FieldKey);
        var field = new DocumentTemplateField
        {
            DocumentTemplateId = template.Id,
            FieldKey = request.FieldKey.Trim(),
            Label = NormalizeNullable(request.Label) ?? catalogItem?.Label ?? request.FieldKey.Trim(),
            Kind = request.Kind,
            SignerRole = request.SignerRole,
            PageNumber = request.PageNumber,
            XPct = request.XPct,
            YPct = request.YPct,
            WidthPct = request.WidthPct,
            HeightPct = request.HeightPct,
            Required = request.Required || catalogItem?.RequiredForSignature == true,
            Locked = request.Locked,
            SortOrder = request.SortOrder,
            DefaultText = NormalizeNullable(request.DefaultText),
        };

        _db.DocumentTemplateFields.Add(field);
        BumpTemplateVersion(template);
        await _db.SaveChangesAsync(ct);

        return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Success(
            DocumentTemplateFieldResponse.FromEntity(field));
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> UpdateFieldAsync(
        int portfolioId, int templateId, int fieldId, UpdateDocumentTemplateFieldRequest request, CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (template is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.NotFound("Document template not found");
        }

        var field = await _db.DocumentTemplateFields
            .FirstOrDefaultAsync(f => f.Id == fieldId && f.DocumentTemplateId == templateId, ct);
        if (field is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.NotFound("Document template field not found");
        }

        var nextX = request.XPct ?? field.XPct;
        var nextY = request.YPct ?? field.YPct;
        var nextWidth = request.WidthPct ?? field.WidthPct;
        var nextHeight = request.HeightPct ?? field.HeightPct;
        var extentError = ValidateExtent(nextX, nextY, nextWidth, nextHeight);
        if (extentError is not null)
        {
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Invalid(extentError);
        }

        if (request.FieldKey is not null) field.FieldKey = request.FieldKey.Trim();
        if (request.Label is not null) field.Label = request.Label.Trim();
        if (request.Kind.HasValue) field.Kind = request.Kind.Value;
        if (request.SignerRole.HasValue) field.SignerRole = request.SignerRole.Value;
        if (request.PageNumber.HasValue) field.PageNumber = request.PageNumber.Value;
        field.XPct = nextX;
        field.YPct = nextY;
        field.WidthPct = nextWidth;
        field.HeightPct = nextHeight;
        if (request.Required.HasValue) field.Required = request.Required.Value;
        if (request.Locked.HasValue) field.Locked = request.Locked.Value;
        if (request.SortOrder.HasValue) field.SortOrder = request.SortOrder.Value;
        if (request.DefaultText is not null) field.DefaultText = NormalizeNullable(request.DefaultText);

        BumpTemplateVersion(template);
        await _db.SaveChangesAsync(ct);

        return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Success(
            DocumentTemplateFieldResponse.FromEntity(field));
    }

    public async Task<DocumentTemplateOperationResult<bool>> DeleteFieldAsync(
        int portfolioId, int templateId, int fieldId, CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.PortfolioId == portfolioId, ct);
        if (template is null)
        {
            return DocumentTemplateOperationResult<bool>.NotFound("Document template not found");
        }

        var field = await _db.DocumentTemplateFields
            .FirstOrDefaultAsync(f => f.Id == fieldId && f.DocumentTemplateId == templateId, ct);
        if (field is null)
        {
            return DocumentTemplateOperationResult<bool>.NotFound("Document template field not found");
        }

        _db.DocumentTemplateFields.Remove(field);
        BumpTemplateVersion(template);
        await _db.SaveChangesAsync(ct);

        return DocumentTemplateOperationResult<bool>.Success(true);
    }

    private async Task<string?> ValidateReferencesAsync(
        int portfolioId, int? propertyId, int? originalFileId, int? compiledFileId, CancellationToken ct)
    {
        if (propertyId.HasValue)
        {
            var propertyExists = await _db.Properties
                .AsNoTracking()
                .AnyAsync(p => p.Id == propertyId.Value && p.PortfolioId == portfolioId, ct);
            if (!propertyExists)
            {
                return "Property not found in this portfolio.";
            }
        }

        if (originalFileId.HasValue && !await StoredFileExistsAsync(portfolioId, originalFileId.Value, ct))
        {
            return "Original document file not found in this portfolio.";
        }

        if (compiledFileId.HasValue && !await StoredFileExistsAsync(portfolioId, compiledFileId.Value, ct))
        {
            return "Compiled document file not found in this portfolio.";
        }

        return null;
    }

    private Task<bool> StoredFileExistsAsync(int portfolioId, int fileId, CancellationToken ct) =>
        _db.StoredFiles.AsNoTracking().AnyAsync(f => f.Id == fileId && f.PortfolioId == portfolioId, ct);

    private Task ClearOtherDefaultsAsync(
        int portfolioId,
        DocumentTemplateKind kind,
        int? propertyId,
        int? exceptId,
        DateTime now,
        CancellationToken ct)
    {
        var q = _db.DocumentTemplates
            .Where(t => t.PortfolioId == portfolioId
                && t.Kind == kind
                && t.PropertyId == propertyId
                && t.DefaultForPortfolio);

        if (exceptId.HasValue)
        {
            q = q.Where(t => t.Id != exceptId.Value);
        }

        return q.ExecuteUpdateAsync(setters => setters
            .SetProperty(t => t.DefaultForPortfolio, false)
            .SetProperty(t => t.UpdatedAtUtc, now), ct);
    }

    private static string? ValidateExtent(double x, double y, double width, double height)
    {
        if (x + width > 1)
        {
            return "Field extends past the right edge of the page.";
        }

        if (y + height > 1)
        {
            return "Field extends past the bottom edge of the page.";
        }

        return null;
    }

    private static void BumpTemplateVersion(DocumentTemplate template)
    {
        template.Version++;
        template.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
