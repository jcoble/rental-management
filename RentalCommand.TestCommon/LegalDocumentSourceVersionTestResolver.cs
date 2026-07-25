using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

/// <summary>
/// Relational-test substitute for the PostgreSQL one-statement resolver. Production registrations
/// always use the DB-side jsonb/ON CONFLICT implementation.
/// </summary>
public sealed class LegalDocumentSourceVersionTestResolver : ILegalDocumentSourceVersionResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RentalCommandDbContext _db;

    public LegalDocumentSourceVersionTestResolver(RentalCommandDbContext db) => _db = db;

    public async Task<ResolvedExactLegalDocumentSourceVersion?> ResolveExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        CancellationToken ct = default)
    {
        var source = await _db.LegalDocumentSourceVersions.AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId && row.Id == documentSourceVersionId)
            .Select(row => new
            {
                row.Id,
                row.SourceKind,
                row.BusinessKey,
                row.RendererKey,
                row.RendererVersion,
                row.SnapshotPayload,
                row.SourceStoredFileId,
                row.SourceLegalDocumentArtifactId,
                row.SourceContentSha256,
                SourceArtifactContentSha256 = row.SourceLegalDocumentArtifact == null
                    ? null
                    : row.SourceLegalDocumentArtifact.ContentSha256,
                DirectStoragePath = row.SourceStoredFile == null ? null : row.SourceStoredFile.FilePath,
                ArtifactStoragePath = row.SourceLegalDocumentArtifact == null
                    || row.SourceLegalDocumentArtifact.StoredFile == null
                        ? null
                        : row.SourceLegalDocumentArtifact.StoredFile.FilePath,
            })
            .SingleOrDefaultAsync(ct);
        if (source is null) return null;

        string? authoredStoragePath = null;
        if (source.SourceKind == LegalDocumentSourceKind.AuthoredTemplateSnapshot)
        {
            using var snapshot = JsonDocument.Parse(source.SnapshotPayload);
            var hasOriginal = snapshot.RootElement.TryGetProperty("originalStoredFileId", out var storedFileId)
                && storedFileId.ValueKind == JsonValueKind.Number;
            var hasCompiled = snapshot.RootElement.TryGetProperty("compiledStoredFileId", out var compiledFileId)
                && compiledFileId.ValueKind == JsonValueKind.Number;
            if (hasOriginal || hasCompiled)
            {
                var id = hasOriginal ? storedFileId.GetInt32() : compiledFileId.GetInt32();
                authoredStoragePath = await _db.StoredFiles.AsNoTracking()
                    .Where(file => file.PortfolioId == portfolioId && file.Id == id && file.DeletedAt == null)
                    .Select(file => file.FilePath)
                    .SingleOrDefaultAsync(ct);
            }
        }

        return new ResolvedExactLegalDocumentSourceVersion(
            source.Id,
            source.SourceKind,
            source.BusinessKey,
            source.RendererKey,
            source.RendererVersion,
            source.SnapshotPayload,
            source.SourceStoredFileId,
            source.SourceLegalDocumentArtifactId,
            source.SourceContentSha256,
            source.SourceArtifactContentSha256,
            source.DirectStoragePath ?? source.ArtifactStoragePath ?? authoredStoragePath);
    }

    public async Task<ResolvedAuthoredDocumentSourceVersion?> ResolveActiveOverlayAsync(
        int portfolioId,
        int propertyId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var templateId = await _db.DocumentTemplates.AsNoTracking()
            .Where(template => template.PortfolioId == portfolioId
                && template.Kind == DocumentTemplateKind.Lease
                && template.Status == DocumentTemplateStatus.Active
                && template.RenderMode == DocumentTemplateRenderMode.Overlay
                && template.ArchivedAtUtc == null
                && template.OriginalStoredFileId != null
                && template.DefaultForPortfolio
                && (template.PropertyId == propertyId || template.PropertyId == null))
            .OrderByDescending(template => template.PropertyId == propertyId ? 1 : 0)
            .ThenByDescending(template => template.UpdatedAtUtc)
            .ThenByDescending(template => template.Id)
            .Select(template => (int?)template.Id)
            .FirstOrDefaultAsync(ct);
        return templateId is null
            ? null
            : await ResolveAuthoredTemplateAsync(
                portfolioId, templateId.Value, actorUserId, createdAtUtc, ct);
    }

    public async Task<ResolvedAuthoredDocumentSourceVersion?> ResolveAuthoredTemplateAsync(
        int portfolioId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var template = await _db.DocumentTemplates.AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId
                && row.Id == documentTemplateId
                && row.Kind == DocumentTemplateKind.Lease
                && row.Status == DocumentTemplateStatus.Active
                && row.ArchivedAtUtc == null)
            .Select(row => new
            {
                row.Id,
                row.Version,
                row.RenderMode,
                row.OriginalStoredFileId,
                row.CompiledStoredFileId,
                row.DraftHtml,
                OriginalStoragePath = row.OriginalStoredFile == null
                    ? null
                    : row.OriginalStoredFile.FilePath,
            })
            .SingleOrDefaultAsync(ct);
        if (template is null) return null;

        var fields = await _db.DocumentTemplateFields.AsNoTracking()
            .Where(field => field.DocumentTemplateId == documentTemplateId)
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Id)
            .ToListAsync(ct);
        var fieldSnapshots = fields.Select(field => new
        {
            field.Id,
            field.FieldKey,
            field.Label,
            kind = field.Kind.ToString(),
            signerRole = field.SignerRole.ToString(),
            field.PageNumber,
            field.XPct,
            field.YPct,
            field.WidthPct,
            field.HeightPct,
            field.Required,
            field.Locked,
            field.SortOrder,
            field.DefaultText,
        }).ToArray();
        var snapshot = JsonSerializer.Serialize(new
        {
            documentTemplateId = template.Id,
            documentTemplateVersion = template.Version,
            renderMode = template.RenderMode.ToString(),
            template.OriginalStoredFileId,
            template.CompiledStoredFileId,
            template.DraftHtml,
            fields = fieldSnapshots,
        }, JsonOptions);
        var businessKey = $"template:{template.Id}:v{template.Version}";
        var source = await _db.LegalDocumentSourceVersions
            .SingleOrDefaultAsync(row => row.PortfolioId == portfolioId
                && row.BusinessKey == businessKey, ct);
        if (source is null)
        {
            source = new LegalDocumentSourceVersion
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
                BusinessKey = businessKey,
                DocumentTemplateId = template.Id,
                DocumentTemplateVersion = template.Version,
                RendererKey = template.RenderMode.ToString().ToLowerInvariant(),
                RendererVersion = 1,
                SnapshotPayload = snapshot,
                CreatedAtUtc = createdAtUtc,
                CreatedByUserId = actorUserId,
            };
            _db.LegalDocumentSourceVersions.Add(source);
            await _db.SaveChangesAsync(ct);
        }

        return new(source.Id, source.SnapshotPayload, template.OriginalStoragePath);
    }

    public async Task<int> ResolveBuiltInAsync(
        int portfolioId,
        string businessKey,
        string rendererKey,
        int rendererVersion,
        string snapshotPayload,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var source = await _db.LegalDocumentSourceVersions
            .SingleOrDefaultAsync(row => row.PortfolioId == portfolioId
                && row.SourceKind == LegalDocumentSourceKind.BuiltInRenderer
                && row.RendererKey == rendererKey
                && row.RendererVersion == rendererVersion, ct);
        if (source is null)
        {
            source = new LegalDocumentSourceVersion
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
                BusinessKey = businessKey,
                RendererKey = rendererKey,
                RendererVersion = rendererVersion,
                SnapshotPayload = snapshotPayload,
                CreatedAtUtc = createdAtUtc,
                CreatedByUserId = actorUserId,
            };
            _db.LegalDocumentSourceVersions.Add(source);
            await _db.SaveChangesAsync(ct);
        }

        return source.Id;
    }
}
