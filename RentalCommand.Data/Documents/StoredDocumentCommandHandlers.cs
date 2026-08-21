using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Documents;

public static class StoredDocumentWriteSupport
{
    public const string CreateResultContract = "stored-document.create.result.v1";
    public const string DeleteResultContract = "stored-document.delete.result.v1";

    public static string CreateIdempotencyKey(int portfolioId, int userId, string digest) =>
        $"{portfolioId}:{userId}:{digest}";

    public static string DeleteIdempotencyKey(int portfolioId, int storedFileId, string digest) =>
        $"{portfolioId}:{storedFileId}:{digest}";

    public static TransactionalWrite<CreateStoredDocumentCommand, CreateStoredDocumentResult> Create(
        CreateStoredDocumentCommand command,
        Func<CreateStoredDocumentCommand, IAtomicCommandContext, CancellationToken,
            Task<CreateStoredDocumentResult>> executeAsync,
        Func<CreateStoredDocumentCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync) =>
        new("stored-document.create", WriteIdempotencyPolicy.Required, command, CreateResultContract,
            new WriteLockPlan(WriteLockProtocol.Portfolio,
                WriteLock.For("Portfolio", command.PortfolioId)),
            executeAsync, authorizeReplayAsync);

    public static TransactionalWrite<DeleteStoredDocumentCommand, DeleteStoredDocumentResult> Delete(
        DeleteStoredDocumentCommand command,
        Func<DeleteStoredDocumentCommand, IAtomicCommandContext, CancellationToken,
            Task<DeleteStoredDocumentResult>> executeAsync,
        Func<DeleteStoredDocumentCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync) =>
        new("stored-document.delete", WriteIdempotencyPolicy.Required, command, DeleteResultContract,
            new WriteLockPlan(WriteLockProtocol.StoredFile,
                WriteLock.For("StoredFile", command.StoredFileId)),
            executeAsync, authorizeReplayAsync);

}

public sealed class CreateStoredDocumentHandler
{
    public static async Task<CreateStoredDocumentResult> ExecuteAsync(
        RentalCommandDbContext db, CreateStoredDocumentCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        var securityAtUtc = await context.ReadDatabaseClockUtcAsync(ct);

        var pendingUpload = await db.Set<PendingFileUpload>()
            .SingleOrDefaultAsync(upload => upload.Id == command.PendingUploadId
                && upload.PortfolioId == command.PortfolioId
                && upload.State == PendingFileUploadState.Prepared
                && upload.CleanupClaimToken == null
                && upload.RequestFingerprint == command.RequestFingerprint,
                ct)
            ?? throw new InvalidOperationException("The durable upload admission is missing, finalized, or does not match the request.");

        if (!string.Equals(pendingUpload.StoragePath, command.StoragePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored blob path does not match its durable upload admission.");
        }

        if (!await StoredDocumentAuthorization.TargetExistsAsync(
                command, db, ct, securityAtUtc))
        {
            return NotFound(command);
        }

        var entityType = command.Target.ToString();
        var existing = await db.Set<StoredFile>()
            .Where(file => file.PortfolioId == command.PortfolioId
                && file.EntityType == entityType
                && file.EntityId == command.EntityId
                && file.ContentSha256 == command.ContentSha256
                && file.DeletedAt == null)
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            pendingUpload.State = PendingFileUploadState.Finalized;
            pendingUpload.StoredFileId = existing.Id;
            pendingUpload.UpdatedAtUtc = command.UploadedAtUtc;
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "blob-delete",
                Payload = JsonSerializer.Serialize(new
                {
                    pendingUploadId = pendingUpload.Id,
                    storedFileId = existing.Id,
                    storagePath = command.StoragePath,
                }),
                IdempotencyKey = $"stored-document-duplicate-upload:{pendingUpload.Id}",
                CreatedAtUtc = command.UploadedAtUtc,
                NextAttemptAtUtc = command.UploadedAtUtc,
            });

            return new CreateStoredDocumentResult(
                StoredDocumentMutationOutcome.ReusedExisting,
                existing.Id,
                entityType,
                command.EntityId,
                existing.FileName,
                existing.FilePath,
                existing.ContentType,
                existing.FileSize,
                existing.UploadedAt);
        }

        var row = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            EntityType = entityType,
            EntityId = command.EntityId,
            FileName = command.FileName,
            ContentType = command.ContentType,
            ContentSha256 = command.ContentSha256,
            FileSize = command.SizeBytes,
            FilePath = command.StoragePath,
            UploadedAt = command.UploadedAtUtc,
        };

        db.Add(row);
        context.BindSemanticAudit(row, new AtomicSemanticAudit(
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
        await context.FlushBusinessAsync(ct);

        pendingUpload.State = PendingFileUploadState.Finalized;
        pendingUpload.StoredFileId = row.Id;
        pendingUpload.UpdatedAtUtc = command.UploadedAtUtc;

        if (command.Target == StoredDocumentTarget.Unit)
        {
            context.StageSemanticEvent(UnitAudit(
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

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, CreateStoredDocumentCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        var securityAtUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var authorized = await StoredDocumentAuthorization.TargetExistsAsync(
            command, db, ct, securityAtUtc);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The active assignment cannot replay this document upload.");
        }
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
            checked((int)command.EntityId),
            operation,
            UserId: command.UserId,
            OldValues: DocumentValue(oldFileName),
            NewValues: DocumentValue(newFileName),
            ChangeReason: reason);

    internal static string DocumentValue(string? fileName) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["Document"] = fileName });
}

public sealed class DeleteStoredDocumentHandler
{
    public static async Task<DeleteStoredDocumentResult> ExecuteAsync(
        RentalCommandDbContext db, DeleteStoredDocumentCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        var securityAtUtc = await context.ReadDatabaseClockUtcAsync(ct);

        // This is one translated, portfolio-scoped authorization query. Tenant-only callers can
        // reach only a document whose WorkOrder belongs to that same tenant.
        StoredFile? row;
        if (command.IsStaff)
        {
            var target = await db.Set<StoredFile>()
                .Where(file => file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId)
                .Select(file => new { file.EntityType, file.EntityId })
                .SingleOrDefaultAsync(ct);
            row = target is not null && target.EntityId.HasValue &&
                  Enum.TryParse<StoredDocumentTarget>(target.EntityType, true, out var parsed) &&
                  command.ManagementAccess is { } access &&
                  await StoredDocumentAuthorization.StaffTargetExistsAsync(
                      command.PortfolioId, parsed, target.EntityId.Value, access,
                      command.DeletedAtUtc, securityAtUtc, db, ct)
                ? await db.Set<StoredFile>().SingleAsync(file =>
                    file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId, ct)
                : null;
        }
        else if (command.TenantId.HasValue)
        {
            row = await (
                from file in db.Set<StoredFile>()
                join workOrder in db.Set<WorkOrder>()
                    on file.EntityId equals (long?)workOrder.Id
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

        // Soft deletion is captured by the atomic interceptor as a Deleted mutation. Enrich the
        // descriptor after the flush so the semantic detail is bound to that exact classification.
        var flush = await context.FlushBusinessAsync(ct);
        var mutation = flush.Mutations.Single(candidate => ReferenceEquals(candidate.EntityReference, row));
        context.EnrichMutation(mutation, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(StoredFile),
            row.Id,
            AuditLogOperation.Deleted,
            UserId: command.UserId,
            OldValues: JsonSerializer.Serialize(new { row.FileName, DeletedAt = (DateTime?)null }),
            NewValues: JsonSerializer.Serialize(new { row.FileName, row.DeletedAt }),
            ChangeReason: $"Document removed: {row.FileName}"));

        if (string.Equals(entityType, nameof(StoredDocumentTarget.Unit), StringComparison.Ordinal))
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Unit),
                checked((int)entityId),
                AuditLogOperation.Updated,
                UserId: command.UserId,
                OldValues: CreateStoredDocumentHandler.DocumentValue(row.FileName),
                NewValues: CreateStoredDocumentHandler.DocumentValue(null),
                ChangeReason: $"Document removed: {row.FileName}"));
        }

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = JsonSerializer.Serialize(new { storedFileId = row.Id, storagePath = row.FilePath }),
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

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, DeleteStoredDocumentCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        var securityAtUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await db.Set<StoredFile>()
            .IgnoreQueryFilters()
            .Where(file => file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId)
            .Select(file => new { file.EntityType, file.EntityId })
            .SingleOrDefaultAsync(ct);
        if (target is null || !target.EntityId.HasValue)
        {
            throw new UnauthorizedAccessException("The active assignment cannot remove this document.");
        }

        var authorized = command.IsStaff
            ? command.ManagementAccess is { } access
              && Enum.TryParse<StoredDocumentTarget>(target.EntityType, true, out var parsed)
              && await StoredDocumentAuthorization.StaffTargetExistsAsync(
                  command.PortfolioId, parsed, target.EntityId.Value, access,
                  command.DeletedAtUtc, securityAtUtc, db, ct)
            : command.TenantId.HasValue
              && await (
                  from file in db.Set<StoredFile>().IgnoreQueryFilters()
                  join workOrder in db.Set<WorkOrder>()
                      on file.EntityId equals (long?)workOrder.Id
                  where file.Id == command.StoredFileId
                      && file.PortfolioId == command.PortfolioId
                      && file.EntityType == nameof(StoredDocumentTarget.WorkOrder)
                      && workOrder.PortfolioId == command.PortfolioId
                      && workOrder.TenantId == command.TenantId.Value
                  select file.Id)
                  .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException("The active assignment cannot remove this document.");
        }
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
        RentalCommandDbContext db,
        CancellationToken ct,
        DateTime securityAtUtc)
    {
        // Each branch remains one server-side translated eligibility/authorization statement.
        if (!command.IsStaff)
        {
            if (!command.TenantId.HasValue || command.Target != StoredDocumentTarget.WorkOrder)
            {
                return Task.FromResult(false);
            }

            return db.Set<WorkOrder>().AnyAsync(workOrder =>
                workOrder.Id == command.EntityId
                && workOrder.PortfolioId == command.PortfolioId
                && workOrder.TenantId == command.TenantId.Value,
                ct);
        }

        return command.ManagementAccess is not { } access
            ? Task.FromResult(false)
            : StaffTargetExistsAsync(
                command.PortfolioId,
                command.Target,
                command.EntityId,
                access,
                command.UploadedAtUtc,
                securityAtUtc,
                db,
                ct,
                allowAssignedWork: true);
    }

    internal static Task<bool> StaffTargetExistsAsync(
        int portfolioId,
        StoredDocumentTarget target,
        long entityId,
        StoredDocumentManagementAccess access,
        DateTime businessAtUtc,
        DateTime securityAtUtc,
        RentalCommandDbContext db,
        CancellationToken ct,
        bool allowAssignedWork = false)
    {
        if (target == StoredDocumentTarget.WorkOrder)
            return allowAssignedWork
                ? AssignedOrManagedWorkOrderExistsAsync(
                    portfolioId, entityId, access, businessAtUtc, securityAtUtc, db, ct)
                : ManagedWorkOrderExistsAsync(
                    portfolioId, entityId, access, businessAtUtc, securityAtUtc, db, ct);

        IReadOnlyCollection<string>? capabilities = target switch
        {
            StoredDocumentTarget.Property or StoredDocumentTarget.Unit or StoredDocumentTarget.Tenant =>
                [CapabilityKeys.RentalsManage],
            StoredDocumentTarget.LeaseAgreement or StoredDocumentTarget.LegalDocumentArtifact =>
                [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingAgreementsPrepare],
            StoredDocumentTarget.TenantAccount or StoredDocumentTarget.TenantLedgerEntry =>
                [CapabilityKeys.MoneyChargesManage, CapabilityKeys.MoneyPaymentsManage],
            StoredDocumentTarget.Expense => [CapabilityKeys.MoneyExpensesManage],
            StoredDocumentTarget.WorkOrder or StoredDocumentTarget.Appointment or StoredDocumentTarget.Inspection or
                StoredDocumentTarget.Vendor => [CapabilityKeys.WorkManage],
            StoredDocumentTarget.SecurityDepositAccount => [CapabilityKeys.MoneyDepositsManage],
            StoredDocumentTarget.OwnerEntity => [CapabilityKeys.RentalsManage],
            _ => null,
        };
        if (capabilities is null) return Task.FromResult(false);
        var assignments = AuthorizedAssignments(
            db, portfolioId, access, capabilities, businessAtUtc, securityAtUtc);

        return target switch
        {
            StoredDocumentTarget.Property => db.Set<Property>().AnyAsync(entity =>
                entity.Id == entityId && entity.PortfolioId == portfolioId && assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId && selected.PropertyId == entity.Id)), ct),
            StoredDocumentTarget.Unit => db.Set<Unit>().AnyAsync(
                entity => entity.Id == entityId
                    && entity.Property != null
                    && entity.Property.PortfolioId == portfolioId
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Tenant => db.Set<LeaseManagementParty>().AnyAsync(party =>
                party.TenantId == entityId && party.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                        selected.PropertyId == party.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.LeaseAgreement => db.Set<LeaseAgreement>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.LegalDocumentArtifact => db.Set<LegalDocumentArtifact>().AnyAsync(
                artifact => artifact.Id == entityId && artifact.PortfolioId == portfolioId &&
                    (db.Set<LeaseAgreement>().Any(agreement =>
                         agreement.PortfolioId == artifact.PortfolioId &&
                         (agreement.IssuedArtifactId == artifact.Id ||
                          agreement.ExecutedArtifactId == artifact.Id) &&
                         assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected =>
                                 selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == agreement.LeaseManagement!.PropertyId))) ||
                     db.Set<LeaseAddendum>().Any(addendum =>
                         addendum.PortfolioId == artifact.PortfolioId &&
                         (addendum.IssuedArtifactId == artifact.Id ||
                          addendum.ExecutedArtifactId == artifact.Id) &&
                         assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected =>
                                 selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == addendum.LeaseManagement!.PropertyId)))), ct),
            StoredDocumentTarget.TenantAccount => db.Set<TenantAccount>().AnyAsync(account =>
                account.Id == entityId && account.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId &&
                        selected.PropertyId == account.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.TenantLedgerEntry => db.Set<TenantLedgerEntry>().AnyAsync(entry =>
                entry.Id == entityId && entry.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId &&
                        selected.PropertyId == entry.TenantAccount!.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.Expense => db.Set<Expense>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Vendor => db.Set<Vendor>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    (entity.WorkOrders.Any(workOrder => workOrder.PortfolioId == portfolioId &&
                         assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == workOrder.PropertyId))) ||
                     entity.Expenses.Any(expense => expense.PortfolioId == portfolioId &&
                         expense.PropertyId != null && assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == expense.PropertyId)))), ct),
            StoredDocumentTarget.WorkOrder => db.Set<WorkOrder>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Appointment => db.Set<Appointment>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Inspection => db.Set<Inspection>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.SecurityDepositAccount => db.Set<SecurityDepositAccount>().AnyAsync(
                deposit => deposit.Id == entityId && deposit.PortfolioId == portfolioId &&
                    assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected =>
                            selected.PortfolioId == portfolioId &&
                            selected.PropertyId == deposit.TenantAccount!.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.OwnerEntity => db.Set<OwnerEntity>().AnyAsync(owner =>
                owner.Id == entityId && owner.PortfolioId == portfolioId &&
                db.Set<PropertyOwnership>().Any(ownership =>
                    ownership.PortfolioId == owner.PortfolioId
                    && ownership.OwnerEntityId == owner.Id
                    && ownership.EffectiveFromUtc <= businessAtUtc
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > businessAtUtc)
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected =>
                            selected.PortfolioId == portfolioId
                            && selected.PropertyId == ownership.PropertyId))), ct),
            _ => Task.FromResult(false),
        };
    }

    private static Task<bool> AssignedOrManagedWorkOrderExistsAsync(int portfolioId, long entityId,
        StoredDocumentManagementAccess access, DateTime businessAtUtc, DateTime securityAtUtc,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var capabilities = new[] { CapabilityKeys.WorkManage, CapabilityKeys.AssignedWorkUpdate };
        var assignments = AuthorizedAssignments(
            db, portfolioId, access, capabilities, businessAtUtc, securityAtUtc);
        return db.Set<WorkOrder>().AnyAsync(workOrder =>
            workOrder.Id == entityId && workOrder.PortfolioId == portfolioId && assignments.Any(assignment =>
                (assignment.RoleProfile!.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.WorkManage) &&
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                  assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                                               selected.PropertyId == workOrder.PropertyId))) ||
                (assignment.RoleProfile.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate) &&
                 assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                 db.Set<WorkOrderResponsibility>().Any(responsibility =>
                     responsibility.WorkOrderId == workOrder.Id && responsibility.PortfolioId == portfolioId &&
                     responsibility.WorkspaceMembershipId == assignment.WorkspaceMembershipId &&
                     responsibility.MembershipRoleAssignmentId == assignment.Id &&
                     responsibility.EffectiveFromUtc <= businessAtUtc &&
                     (responsibility.EffectiveToUtc == null ||
                      responsibility.EffectiveToUtc > businessAtUtc)))), ct);
    }

    private static Task<bool> ManagedWorkOrderExistsAsync(int portfolioId, long entityId,
        StoredDocumentManagementAccess access, DateTime businessAtUtc, DateTime securityAtUtc,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var assignments = AuthorizedAssignments(
            db, portfolioId, access, CapabilityKeys.WorkManage, businessAtUtc, securityAtUtc);
        return db.Set<WorkOrder>().AnyAsync(workOrder =>
            workOrder.Id == entityId && workOrder.PortfolioId == portfolioId && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                                             selected.PropertyId == workOrder.PropertyId)), ct);
    }

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        RentalCommandDbContext db,
        int portfolioId,
        StoredDocumentManagementAccess access,
        string capability,
        DateTime businessAtUtc,
        DateTime securityAtUtc) => AuthorizedAssignments(
            db, portfolioId, access, new[] { capability }, businessAtUtc, securityAtUtc);

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        RentalCommandDbContext db,
        int portfolioId,
        StoredDocumentManagementAccess access,
        IReadOnlyCollection<string> capabilities,
        DateTime businessAtUtc,
        DateTime securityAtUtc)
    {
        var keys = capabilities.Distinct(StringComparer.Ordinal).ToArray();
        return
        db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId &&
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= securityAtUtc &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityAtUtc) &&
            assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContextId == access.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == portfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= securityAtUtc &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > securityAtUtc) &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.UserId == access.UserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == access.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            db.Set<AuthSession>().Any(session =>
                session.Id == access.SessionId && session.UserId == access.UserId &&
                session.ActiveAccessContextId == access.AccessContextId &&
                session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > securityAtUtc) &&
            assignment.RoleProfile != null && assignment.RoleProfile.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition != null &&
                keys.Contains(profileCapability.CapabilityDefinition.Key) &&
                (profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property ||
                 profileCapability.CapabilityDefinition.Key == CapabilityKeys.AssignedWorkUpdate &&
                 profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.WorkOrder)));
    }
}
