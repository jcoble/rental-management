using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentService"/>
public sealed class DocumentService : IDocumentService
{
    private static readonly AtomicJsonResultCodec<CreateStoredDocumentResult> CreateCodec =
        new("stored-document.create.result.v1");
    private static readonly AtomicJsonResultCodec<DeleteStoredDocumentResult> DeleteCodec =
        new("stored-document.delete.result.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IFileStorage _storage;
    private readonly ILogger<DocumentService> _logger;
    private readonly TimeProvider _timeProvider;

    public DocumentService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        ILogger<DocumentService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _logger = logger;
        _timeProvider = timeProvider;
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

    public async Task<DocumentDto?> CreateAsync(
        int portfolioId,
        StoredDocumentTarget target,
        int entityId,
        int userId,
        int? tenantId,
        bool isStaff,
        string clientOperationId,
        string contentSha256,
        string fileName,
        string contentType,
        long sizeBytes,
        string storagePath,
        CancellationToken ct = default)
    {
        var normalizedOperationId = NormalizeOperationId(clientOperationId);
        var normalizedHash = contentSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Document contentSha256 must be a 64-character SHA-256 hex digest.", nameof(contentSha256));
        }

        AtomicCommandOutcome<CreateStoredDocumentResult> outcome;
        try
        {
            outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "stored-document.create",
                    $"{portfolioId}:{target}:{entityId}:{Digest(normalizedOperationId)}:{normalizedHash}"),
                new CreateStoredDocumentCommand(
                    portfolioId,
                    target,
                    entityId,
                    userId,
                    tenantId,
                    isStaff,
                    normalizedOperationId,
                    normalizedHash,
                    fileName,
                    storagePath,
                    contentType,
                    sizeBytes,
                    _timeProvider.UtcNow()),
                CreateCodec,
                ct);
        }
        catch
        {
            await DeleteUploadedBlobIfUnreferencedAsync(storagePath);
            throw;
        }

        if (outcome.Value.Outcome != StoredDocumentMutationOutcome.Created)
        {
            await DeleteUploadedBlobIfUnreferencedAsync(storagePath);
            return null;
        }

        if (!string.Equals(outcome.Value.StoragePath, storagePath, StringComparison.Ordinal))
        {
            // A retry uploaded a fresh blob before the receipt replayed the first committed row.
            await DeleteUploadedBlobIfUnreferencedAsync(storagePath);
        }

        return ToDto(outcome.Value);
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

    public async Task<bool> DeleteAsync(
        int portfolioId,
        int id,
        int userId,
        int? tenantId,
        bool isStaff,
        string clientOperationId,
        CancellationToken ct = default)
    {
        var normalizedOperationId = NormalizeOperationId(clientOperationId);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "stored-document.delete",
                $"{portfolioId}:{id}:{Digest(normalizedOperationId)}"),
            new DeleteStoredDocumentCommand(
                portfolioId,
                id,
                userId,
                tenantId,
                isStaff,
                normalizedOperationId,
                _timeProvider.UtcNow()),
            DeleteCodec,
            ct);
        return outcome.Value.Outcome == StoredDocumentMutationOutcome.Deleted;
    }

    private async Task DeleteUploadedBlobIfUnreferencedAsync(string storagePath)
    {
        try
        {
            // Unknown commit outcomes are resolved DB-side before touching storage. IgnoreQueryFilters
            // includes soft-deleted rows: an uploaded key referenced anywhere is never compensation-safe.
            var referenced = await _db.StoredFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(file => file.FilePath == storagePath, CancellationToken.None);
            if (!referenced)
            {
                await _storage.DeleteAsync(storagePath, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Fail closed: uncertainty preserves the blob instead of deleting a committed reference.
            _logger.LogWarning(ex, "Could not prove document upload {StoragePath} unreferenced; preserving it.", storagePath);
        }
    }

    private static string NormalizeOperationId(string clientOperationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var normalized = clientOperationId.Trim();
        if (normalized.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientOperationId),
                "Document clientOperationId cannot exceed 160 characters.");
        }

        return normalized;
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

    private static DocumentDto ToDto(CreateStoredDocumentResult result) => new()
    {
        Id = result.StoredFileId,
        FileName = result.FileName,
        ContentType = result.ContentType,
        SizeBytes = result.SizeBytes,
        EntityType = result.EntityType,
        EntityId = result.EntityId,
        IsImage = result.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        UploadedAt = result.UploadedAtUtc,
    };
}
