using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Documents;

public sealed class CreateStoredDocumentHandler
    : IAtomicCommandHandler<CreateStoredDocumentCommand, CreateStoredDocumentResult>
{
    public async Task<CreateStoredDocumentResult> HandleAsync(
        CreateStoredDocumentCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (!await StoredDocumentAuthorization.TargetExistsAsync(command, attempt.Persistence, ct))
        {
            return NotFound(command);
        }

        var entityType = command.Target.ToString();
        var row = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            EntityType = entityType,
            EntityId = command.EntityId,
            FileName = command.FileName,
            ContentType = command.ContentType,
            FileSize = command.SizeBytes,
            FilePath = command.StoragePath,
            UploadedAt = command.UploadedAtUtc,
        };

        attempt.Persistence.Add(row);
        attempt.BindSemanticAudit(row, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(StoredFile),
            0,
            AuditLogOperation.Created,
            UserId: command.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.Target,
                command.EntityId,
                command.FileName,
                command.ContentType,
                command.SizeBytes,
                command.ContentSha256,
                command.ClientOperationId,
            }),
            ChangeReason: $"Document uploaded: {command.FileName}"));
        await attempt.FlushBusinessAsync(ct);

        if (command.Target == StoredDocumentTarget.Unit)
        {
            attempt.StageSemanticEvent(UnitAudit(
                command,
                AuditLogOperation.Updated,
                oldFileName: null,
                newFileName: command.FileName,
                $"Document uploaded: {command.FileName}"));
        }

        return new CreateStoredDocumentResult(
            StoredDocumentMutationOutcome.Created,
            row.Id,
            entityType,
            command.EntityId,
            row.FileName,
            row.FilePath,
            row.ContentType,
            row.FileSize,
            row.UploadedAt);
    }

    private static CreateStoredDocumentResult NotFound(CreateStoredDocumentCommand command) => new(
        StoredDocumentMutationOutcome.NotFound,
        0,
        command.Target.ToString(),
        command.EntityId,
        command.FileName,
        string.Empty,
        command.ContentType,
        command.SizeBytes,
        command.UploadedAtUtc);

    private static AtomicSemanticAudit UnitAudit(
        CreateStoredDocumentCommand command,
        AuditLogOperation operation,
        string? oldFileName,
        string? newFileName,
        string reason) => new(
            command.PortfolioId,
            nameof(Unit),
            command.EntityId,
            operation,
            UserId: command.UserId,
            OldValues: DocumentValue(oldFileName),
            NewValues: DocumentValue(newFileName),
            ChangeReason: reason);

    internal static string DocumentValue(string? fileName) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["Document"] = fileName });
}

public sealed class DeleteStoredDocumentHandler
    : IAtomicCommandHandler<DeleteStoredDocumentCommand, DeleteStoredDocumentResult>
{
    public async Task<DeleteStoredDocumentResult> HandleAsync(
        DeleteStoredDocumentCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.StoredFile, command.StoredFileId, ct);

        // This is one translated, portfolio-scoped authorization query. Tenant-only callers can
        // reach only a document whose WorkOrder belongs to that same tenant.
        StoredFile? row;
        if (command.IsStaff)
        {
            row = await attempt.Persistence.Query<StoredFile>()
                .SingleOrDefaultAsync(file =>
                    file.Id == command.StoredFileId
                    && file.PortfolioId == command.PortfolioId,
                    ct);
        }
        else if (command.TenantId.HasValue)
        {
            row = await (
                from file in attempt.Persistence.Query<StoredFile>()
                join workOrder in attempt.Persistence.Query<WorkOrder>()
                    on file.EntityId equals (int?)workOrder.Id
                where file.Id == command.StoredFileId
                    && file.PortfolioId == command.PortfolioId
                    && file.EntityType == nameof(StoredDocumentTarget.WorkOrder)
                    && workOrder.PortfolioId == command.PortfolioId
                    && workOrder.TenantId == command.TenantId.Value
                select file)
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            row = null;
        }

        if (row is null)
        {
            return NotFound(command);
        }

        var entityType = row.EntityType ?? string.Empty;
        var entityId = row.EntityId ?? 0;
        row.DeletedAt = command.DeletedAtUtc;
        attempt.BindSemanticAudit(row, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(StoredFile),
            row.Id,
            AuditLogOperation.Updated,
            UserId: command.UserId,
            OldValues: JsonSerializer.Serialize(new { row.FileName, DeletedAt = (DateTime?)null }),
            NewValues: JsonSerializer.Serialize(new { row.FileName, row.DeletedAt }),
            ChangeReason: $"Document removed: {row.FileName}"));

        if (string.Equals(entityType, nameof(StoredDocumentTarget.Unit), StringComparison.Ordinal))
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Unit),
                entityId,
                AuditLogOperation.Updated,
                UserId: command.UserId,
                OldValues: CreateStoredDocumentHandler.DocumentValue(row.FileName),
                NewValues: CreateStoredDocumentHandler.DocumentValue(null),
                ChangeReason: $"Document removed: {row.FileName}"));
        }

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = JsonSerializer.Serialize(new { storagePath = row.FilePath }),
            IdempotencyKey = $"stored-file-delete:{row.Id}",
            CreatedAtUtc = command.DeletedAtUtc,
            NextAttemptAtUtc = command.DeletedAtUtc,
        });

        return new DeleteStoredDocumentResult(
            StoredDocumentMutationOutcome.Deleted,
            row.Id,
            entityType,
            entityId,
            row.FileName,
            row.FilePath,
            command.DeletedAtUtc);
    }

    private static DeleteStoredDocumentResult NotFound(DeleteStoredDocumentCommand command) => new(
        StoredDocumentMutationOutcome.NotFound,
        command.StoredFileId,
        string.Empty,
        0,
        string.Empty,
        string.Empty,
        command.DeletedAtUtc);
}

internal static class StoredDocumentAuthorization
{
    public static Task<bool> TargetExistsAsync(
        CreateStoredDocumentCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        // Each branch remains one server-side translated eligibility/authorization statement.
        if (!command.IsStaff)
        {
            if (!command.TenantId.HasValue || command.Target != StoredDocumentTarget.WorkOrder)
            {
                return Task.FromResult(false);
            }

            return persistence.Query<WorkOrder>().AnyAsync(workOrder =>
                workOrder.Id == command.EntityId
                && workOrder.PortfolioId == command.PortfolioId
                && workOrder.TenantId == command.TenantId.Value,
                ct);
        }

        return command.Target switch
        {
            StoredDocumentTarget.Property => persistence.Query<Property>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Unit => persistence.Query<Unit>().AnyAsync(
                entity => entity.Id == command.EntityId
                    && entity.Property != null
                    && entity.Property.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Tenant => persistence.Query<Tenant>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Lease => persistence.Query<Lease>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Payment => persistence.Query<Payment>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Expense => persistence.Query<Expense>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Vendor => persistence.Query<Vendor>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.WorkOrder => persistence.Query<WorkOrder>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Appointment => persistence.Query<Appointment>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.Inspection => persistence.Query<Inspection>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.SecurityDeposit => persistence.Query<SecurityDepositHolding>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            StoredDocumentTarget.OwnerEntity => persistence.Query<OwnerEntity>().AnyAsync(
                entity => entity.Id == command.EntityId && entity.PortfolioId == command.PortfolioId, ct),
            _ => Task.FromResult(false),
        };
    }
}
