using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Authorization;
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
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);

        var pendingUpload = await attempt.Persistence.Query<PendingFileUpload>()
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

        if (!await StoredDocumentAuthorization.TargetExistsAsync(command, attempt.Persistence, ct))
        {
            return NotFound(command);
        }

        var entityType = command.Target.ToString();
        var existing = await attempt.Persistence.Query<StoredFile>()
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
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "blob-delete",
                Payload = JsonSerializer.Serialize(new { pendingUploadId = pendingUpload.Id, storagePath = command.StoragePath }),
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

        pendingUpload.State = PendingFileUploadState.Finalized;
        pendingUpload.StoredFileId = row.Id;
        pendingUpload.UpdatedAtUtc = command.UploadedAtUtc;

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
            var target = await attempt.Persistence.Query<StoredFile>()
                .Where(file => file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId)
                .Select(file => new { file.EntityType, file.EntityId })
                .SingleOrDefaultAsync(ct);
            row = target is not null && target.EntityId.HasValue &&
                  Enum.TryParse<StoredDocumentTarget>(target.EntityType, true, out var parsed) &&
                  command.ManagementAccess is { } access &&
                  await StoredDocumentAuthorization.StaffTargetExistsAsync(
                      command.PortfolioId, parsed, target.EntityId.Value, access,
                      command.DeletedAtUtc, attempt.Persistence, ct)
                ? await attempt.Persistence.Query<StoredFile>().SingleAsync(file =>
                    file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId, ct)
                : null;
        }
        else if (command.TenantId.HasValue)
        {
            row = await (
                from file in attempt.Persistence.Query<StoredFile>()
                join workOrder in attempt.Persistence.Query<WorkOrder>()
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
        var flush = await attempt.FlushBusinessAsync(ct);
        var mutation = flush.Mutations.Single(candidate => ReferenceEquals(candidate.EntityReference, row));
        attempt.EnrichMutation(mutation, new AtomicSemanticAudit(
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
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Unit),
                checked((int)entityId),
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

        return command.ManagementAccess is not { } access
            ? Task.FromResult(false)
            : StaffTargetExistsAsync(
                command.PortfolioId,
                command.Target,
                command.EntityId,
                access,
                command.UploadedAtUtc,
                persistence,
                ct,
                allowAssignedWork: true);
    }

    internal static Task<bool> StaffTargetExistsAsync(
        int portfolioId,
        StoredDocumentTarget target,
        long entityId,
        StoredDocumentManagementAccess access,
        DateTime utcNow,
        IAtomicPersistenceSession persistence,
        CancellationToken ct,
        bool allowAssignedWork = false)
    {
        if (target == StoredDocumentTarget.WorkOrder)
            return allowAssignedWork
                ? AssignedOrManagedWorkOrderExistsAsync(
                    portfolioId, entityId, access, utcNow, persistence, ct)
                : ManagedWorkOrderExistsAsync(portfolioId, entityId, access, utcNow, persistence, ct);

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
        var assignments = AuthorizedAssignments(persistence, portfolioId, access, capabilities, utcNow);

        return target switch
        {
            StoredDocumentTarget.Property => persistence.Query<Property>().AnyAsync(entity =>
                entity.Id == entityId && entity.PortfolioId == portfolioId && assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId && selected.PropertyId == entity.Id)), ct),
            StoredDocumentTarget.Unit => persistence.Query<Unit>().AnyAsync(
                entity => entity.Id == entityId
                    && entity.Property != null
                    && entity.Property.PortfolioId == portfolioId
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Tenant => persistence.Query<LeaseManagementParty>().AnyAsync(party =>
                party.TenantId == entityId && party.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                        selected.PropertyId == party.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.LeaseAgreement => persistence.Query<LeaseAgreement>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.LegalDocumentArtifact => persistence.Query<LegalDocumentArtifact>().AnyAsync(
                artifact => artifact.Id == entityId && artifact.PortfolioId == portfolioId &&
                    (persistence.Query<LeaseAgreement>().Any(agreement =>
                         agreement.PortfolioId == artifact.PortfolioId &&
                         (agreement.IssuedArtifactId == artifact.Id ||
                          agreement.ExecutedArtifactId == artifact.Id) &&
                         assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected =>
                                 selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == agreement.LeaseManagement!.PropertyId))) ||
                     persistence.Query<LeaseAddendum>().Any(addendum =>
                         addendum.PortfolioId == artifact.PortfolioId &&
                         (addendum.IssuedArtifactId == artifact.Id ||
                          addendum.ExecutedArtifactId == artifact.Id) &&
                         assignments.Any(assignment =>
                             assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                             assignment.SelectedProperties.Any(selected =>
                                 selected.PortfolioId == portfolioId &&
                                 selected.PropertyId == addendum.LeaseManagement!.PropertyId)))), ct),
            StoredDocumentTarget.TenantAccount => persistence.Query<TenantAccount>().AnyAsync(account =>
                account.Id == entityId && account.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId &&
                        selected.PropertyId == account.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.TenantLedgerEntry => persistence.Query<TenantLedgerEntry>().AnyAsync(entry =>
                entry.Id == entityId && entry.PortfolioId == portfolioId &&
                assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId &&
                        selected.PropertyId == entry.TenantAccount!.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.Expense => persistence.Query<Expense>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Vendor => persistence.Query<Vendor>().AnyAsync(
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
            StoredDocumentTarget.WorkOrder => persistence.Query<WorkOrder>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Appointment => persistence.Query<Appointment>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.Inspection => persistence.Query<Inspection>().AnyAsync(
                entity => entity.Id == entityId && entity.PortfolioId == portfolioId &&
                    assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                            selected.PropertyId == entity.PropertyId)), ct),
            StoredDocumentTarget.SecurityDepositAccount => persistence.Query<SecurityDepositAccount>().AnyAsync(
                deposit => deposit.Id == entityId && deposit.PortfolioId == portfolioId &&
                    assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected =>
                            selected.PortfolioId == portfolioId &&
                            selected.PropertyId == deposit.TenantAccount!.LeaseManagement!.PropertyId)), ct),
            StoredDocumentTarget.OwnerEntity => persistence.Query<OwnerEntity>().AnyAsync(owner =>
                owner.Id == entityId && owner.PortfolioId == portfolioId &&
                persistence.Query<PropertyOwnership>().Any(ownership =>
                    ownership.PortfolioId == owner.PortfolioId
                    && ownership.OwnerEntityId == owner.Id
                    && ownership.EffectiveFromUtc <= utcNow
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > utcNow)
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                        assignment.SelectedProperties.Any(selected =>
                            selected.PortfolioId == portfolioId
                            && selected.PropertyId == ownership.PropertyId))), ct),
            _ => Task.FromResult(false),
        };
    }

    private static Task<bool> AssignedOrManagedWorkOrderExistsAsync(int portfolioId, long entityId,
        StoredDocumentManagementAccess access, DateTime utcNow, IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var capabilities = new[] { CapabilityKeys.WorkManage, CapabilityKeys.AssignedWorkUpdate };
        var assignments = AuthorizedAssignments(persistence, portfolioId, access, capabilities, utcNow);
        return persistence.Query<WorkOrder>().AnyAsync(workOrder =>
            workOrder.Id == entityId && workOrder.PortfolioId == portfolioId && assignments.Any(assignment =>
                (assignment.RoleProfile!.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.WorkManage) &&
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                  assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                                               selected.PropertyId == workOrder.PropertyId))) ||
                (assignment.RoleProfile.Capabilities.Any(item =>
                     item.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate) &&
                 assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                 persistence.Query<WorkOrderResponsibility>().Any(responsibility =>
                     responsibility.WorkOrderId == workOrder.Id && responsibility.PortfolioId == portfolioId &&
                     responsibility.WorkspaceMembershipId == assignment.WorkspaceMembershipId &&
                     responsibility.MembershipRoleAssignmentId == assignment.Id &&
                     responsibility.EffectiveFromUtc <= utcNow &&
                     (responsibility.EffectiveToUtc == null || responsibility.EffectiveToUtc > utcNow)))), ct);
    }

    private static Task<bool> ManagedWorkOrderExistsAsync(int portfolioId, long entityId,
        StoredDocumentManagementAccess access, DateTime utcNow, IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var assignments = AuthorizedAssignments(
            persistence, portfolioId, access, CapabilityKeys.WorkManage, utcNow);
        return persistence.Query<WorkOrder>().AnyAsync(workOrder =>
            workOrder.Id == entityId && workOrder.PortfolioId == portfolioId && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId &&
                                                             selected.PropertyId == workOrder.PropertyId)), ct);
    }

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        StoredDocumentManagementAccess access,
        string capability,
        DateTime utcNow) => AuthorizedAssignments(
            persistence, portfolioId, access, new[] { capability }, utcNow);

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        StoredDocumentManagementAccess access,
        IReadOnlyCollection<string> capabilities,
        DateTime utcNow)
    {
        var keys = capabilities.Distinct(StringComparer.Ordinal).ToArray();
        return
        persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId &&
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= utcNow &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
            assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContextId == access.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == portfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.UserId == access.UserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == access.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            persistence.Query<AuthSession>().Any(session =>
                session.Id == access.SessionId && session.UserId == access.UserId &&
                session.ActiveAccessContextId == access.AccessContextId &&
                session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow) &&
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
