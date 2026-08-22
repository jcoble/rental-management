using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentService"/>
public sealed class DocumentService : IDocumentService
{
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;
    private readonly IFileStorage _storage;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly ILogger<DocumentService> _logger;
    private readonly TimeProvider _timeProvider;

    public DocumentService(
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        IFileStorage storage,
        IPendingFileUploadStore pendingUploads,
        ILogger<DocumentService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _writes = writes;
        _storage = storage;
        _pendingUploads = pendingUploads;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public Task<PendingFileUploadAdmission> PrepareUploadAsync(
        int portfolioId,
        int actorUserId,
        string clientOperationId,
        string requestFingerprint,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default) =>
        _pendingUploads.PrepareAsync(
            portfolioId,
            actorUserId,
            "stored-document",
            NormalizeOperationId(clientOperationId),
            requestFingerprint,
            fileName,
            contentType,
            sizeBytes,
            _timeProvider.UtcNow(),
            ct);

    public async Task<DocumentDto?> GetFinalizedUploadAsync(
        int portfolioId,
        PendingFileUploadAdmission admission,
        CancellationToken ct = default)
    {
        if (admission.State != Core.Enums.PendingFileUploadState.Finalized
            || !admission.StoredFileId.HasValue)
        {
            return null;
        }

        var row = await _db.StoredFiles.AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == admission.StoredFileId.Value
                && file.PortfolioId == portfolioId
                && file.DeletedAt == null, ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(
        int portfolioId,
        string entityType,
        long entityId,
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
        Guid pendingUploadId,
        int portfolioId,
        StoredDocumentTarget target,
        long entityId,
        int userId,
        int? tenantId,
        bool isStaff,
        WorkspaceReadScope? staffScope,
        string clientOperationId,
        string requestFingerprint,
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

        var command = new CreateStoredDocumentCommand(
                    pendingUploadId,
                    portfolioId,
                    target,
                    entityId,
                    userId,
                    tenantId,
                    isStaff,
                    normalizedOperationId,
                    requestFingerprint,
                    normalizedHash,
                    fileName,
                    storagePath,
                    contentType,
                    sizeBytes,
                    _timeProvider.UtcNow(),
                    staffScope is { } access
                        ? new StoredDocumentManagementAccess(
                            access.SessionId,
                            access.UserId,
                            access.AccessContextId,
                            access.AccessRevision)
                        : null);
        var outcome = await _writes.ExecuteAsync(
            StoredDocumentWriteSupport.CreateIdempotencyKey(
                portfolioId, userId, Digest(normalizedOperationId)),
            CreateRule(command),
            ct);

        if (outcome.Value.Outcome is not (
            StoredDocumentMutationOutcome.Created or
            StoredDocumentMutationOutcome.ReusedExisting))
        {
            return null;
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
        WorkspaceReadScope? staffScope,
        string clientOperationId,
        CancellationToken ct = default)
    {
        var normalizedOperationId = NormalizeOperationId(clientOperationId);
        var command = new DeleteStoredDocumentCommand(
                portfolioId,
                id,
                userId,
                tenantId,
                isStaff,
                normalizedOperationId,
                _timeProvider.UtcNow(),
                staffScope is { } access
                    ? new StoredDocumentManagementAccess(
                        access.SessionId,
                        access.UserId,
                        access.AccessContextId,
                        access.AccessRevision)
                    : null);
        var outcome = await _writes.ExecuteAsync(
            StoredDocumentWriteSupport.DeleteIdempotencyKey(
                portfolioId, id, Digest(normalizedOperationId)),
            DeleteRule(command),
            ct);
        return outcome.Value.Outcome == StoredDocumentMutationOutcome.Deleted;
    }

    private TransactionalWrite<CreateStoredDocumentCommand, CreateStoredDocumentResult> CreateRule(
        CreateStoredDocumentCommand command) => StoredDocumentWriteSupport.Create(
        command,
        (request, context, ct) =>
            CreateStoredDocumentRule.ExecuteAsync(_db, request, context, ct),
        (request, context, ct) =>
            CreateStoredDocumentRule.AuthorizeAsync(_db, request, context, ct));

    private TransactionalWrite<DeleteStoredDocumentCommand, DeleteStoredDocumentResult> DeleteRule(
        DeleteStoredDocumentCommand command) => StoredDocumentWriteSupport.Delete(
        command,
        (request, context, ct) =>
            DeleteStoredDocumentRule.ExecuteAsync(_db, request, context, ct),
        (request, context, ct) =>
            DeleteStoredDocumentRule.AuthorizeAsync(_db, request, context, ct));

    private static string NormalizeOperationId(string clientOperationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var normalized = clientOperationId.Trim();
        if (normalized.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientOperationId),
                "A request key cannot exceed 160 characters.");
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
