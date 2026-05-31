using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentService"/>
public sealed class DocumentService : IDocumentService
{
    private readonly RentalCommandDbContext _db;

    public DocumentService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(
        int portfolioId,
        string entityType,
        int entityId,
        CancellationToken ct = default)
    {
        var rows = await _db.StoredFiles
            .AsNoTracking()
            .Where(f =>
                f.PortfolioId == portfolioId &&
                f.EntityType == entityType &&
                f.EntityId == entityId &&
                f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<DocumentDto> CreateAsync(
        int portfolioId,
        string entityType,
        int entityId,
        string fileName,
        string contentType,
        long sizeBytes,
        string storagePath,
        CancellationToken ct = default)
    {
        var row = new StoredFile
        {
            PortfolioId = portfolioId,
            EntityType = entityType,
            EntityId = entityId,
            FileName = fileName,
            ContentType = contentType,
            FileSize = sizeBytes,
            FilePath = storagePath,
            UploadedAt = DateTime.UtcNow
        };

        _db.StoredFiles.Add(row);
        await _db.SaveChangesAsync(ct);

        return ToDto(row);
    }

    public async Task<StoredFile?> FindAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await _db.StoredFiles
            .FirstOrDefaultAsync(f =>
                f.Id == id &&
                f.PortfolioId == portfolioId &&
                f.DeletedAt == null,
                ct);
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var row = await FindAsync(portfolioId, id, ct);
        if (row is null) return false;

        row.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // -------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------

    private static DocumentDto ToDto(StoredFile f) => new()
    {
        Id = f.Id,
        FileName = f.FileName,
        ContentType = f.ContentType,
        SizeBytes = f.FileSize,
        EntityType = f.EntityType ?? string.Empty,
        EntityId = f.EntityId,
        IsImage = f.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        UploadedAt = f.UploadedAt,
    };
}
