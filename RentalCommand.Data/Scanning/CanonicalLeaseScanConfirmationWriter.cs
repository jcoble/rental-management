using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Canonical lease-import writer. It never creates or reads a legacy Lease row. A reviewed scan
/// either becomes an immutable already-executed Agreement artifact or an unissued Agreement draft.
/// </summary>
internal static class CanonicalLeaseScanConfirmationWriter
{
    internal static async Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command, target);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, target.UnitId!.Value, ct);
        if (target.LeaseManagementId is > 0)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.LeaseManagement, target.LeaseManagementId.Value, ct);
        }

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var home = await AuthorizedHomes(command, target, attempt.Persistence, now)
            .Select(unit => new HomeFacts(
                unit.Id,
                unit.PropertyId,
                unit.Property!.Portfolio!.Currency,
                unit.LeaseManagements.Any(relationship =>
                    relationship.CanceledAtUtc == null
                    && relationship.PossessionReturnedAtUtc == null)))
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException(
                "Lease import is outside the caller's current property scope or capability.");

        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned)
        {
            var existing = await (
                from artifact in attempt.Persistence.Query<LegalDocumentArtifact>()
                join existingAgreement in attempt.Persistence.Query<LeaseAgreement>()
                    on new { ArtifactId = artifact.Id, artifact.PortfolioId }
                    equals new { ArtifactId = existingAgreement.ExecutedArtifactId!.Value, existingAgreement.PortfolioId }
                where artifact.PortfolioId == command.PortfolioId
                    && artifact.ContentSha256 == command.SourceContentSha256
                    && existingAgreement.LeaseManagement!.PropertyId == target.PropertyId
                    && existingAgreement.LeaseManagement.UnitId == target.UnitId
                select new ScanConfirmationTargetWriteResult(
                    existingAgreement.Id,
                    existingAgreement.LeaseManagement!.UnitId,
                    nameof(LeaseAgreement)))
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
                return existing;
        }

        var templateIsValid = (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned
                && target.DocumentTemplateId is null && target.DocumentTemplateVersion is null)
            || (target.DocumentTemplateId is > 0 && target.DocumentTemplateVersion is > 0
                && await attempt.Persistence.Query<DocumentTemplate>().AnyAsync(template =>
                    template.Id == target.DocumentTemplateId
                    && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.Version == target.DocumentTemplateVersion
                    && template.ArchivedAtUtc == null
                    && (template.PropertyId == null || template.PropertyId == target.PropertyId), ct));
        if (!templateIsValid)
        {
            throw new ScanConfirmationValidationException(
                "An active lease template is required when the uploaded agreement still needs signatures.");
        }

        var tenant = await ResolveTenantAsync(command, target, attempt, now, ct);
        var relationship = await ResolveRelationshipAsync(command, target, home, attempt, now, ct);
        var account = await ResolveAccountAsync(command, target, relationship, home.Currency, attempt, now, ct);

        var existingAgreementCount = await attempt.Persistence.Query<LeaseAgreement>()
            .CountAsync(agreement => agreement.PortfolioId == command.PortfolioId
                && agreement.LeaseManagementId == relationship.Id, ct);
        if (existingAgreementCount != 0)
        {
            throw new ScanConfirmationValidationException(
                "This rental relationship already has an agreement. Use the correction, restatement, or renewal flow instead of importing another initial lease.");
        }

        var party = await attempt.Persistence.Query<LeaseManagementParty>()
            .SingleOrDefaultAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == relationship.Id
                && candidate.TenantId == tenant.Id
                && candidate.EffectiveThrough == null, ct);
        if (party is null)
        {
            party = new LeaseManagementParty
            {
                PortfolioId = command.PortfolioId,
                LeaseManagementId = relationship.Id,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = DateOnly.FromDateTime(target.StartDate!.Value),
                ChangeReason = "Imported from reviewed lease scan.",
                CreatedAtUtc = now,
                CreatedByUserId = command.ConfirmedByUserId,
            };
            attempt.Persistence.Add(party);
            attempt.BindSemanticAudit(party, Created(command, nameof(LeaseManagementParty),
                "Added the primary tenant from reviewed lease scan."));
            await attempt.FlushBusinessAsync(ct);
        }

        var fixedTerm = target.EndDate.HasValue;
        var agreement = new LeaseAgreement
        {
            PortfolioId = command.PortfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = string.IsNullOrWhiteSpace(target.LeaseNumber)
                ? $"AGR-SCAN-{command.DraftId:D8}"
                : target.LeaseNumber.Trim(),
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = fixedTerm ? LeaseAgreementTermType.FixedTerm : LeaseAgreementTermType.MonthToMonth,
            TermStartOn = DateOnly.FromDateTime(target.StartDate!.Value),
            TermEndOn = target.EndDate.HasValue ? DateOnly.FromDateTime(target.EndDate.Value) : null,
            GoverningFromOn = DateOnly.FromDateTime(target.StartDate.Value),
            BaseRentAmount = target.MonthlyRent!.Value,
            RentDueDay = checked((short)target.RentDueDay!.Value),
            SecurityDepositObligation = target.SecurityDeposit ?? 0m,
            LateFeeAmount = target.LateFee ?? 0m,
            GracePeriodDays = target.GracePeriodDays,
            Currency = home.Currency,
            TermsSchemaVersion = target.TermsSchemaVersion,
            TermsPayload = NormalizeJsonObject(target.TermsPayload),
            DocumentTemplateId = target.DocumentTemplateId,
            DocumentTemplateVersion = target.DocumentTemplateVersion,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
            UpdatedAtUtc = now,
            DraftRevision = 1,
        };
        attempt.Persistence.Add(agreement);
        attempt.BindSemanticAudit(agreement, Created(command, nameof(LeaseAgreement),
            target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned
                ? $"Created Agreement for externally executed scan import{SourceSuffix(command.SourceLabel)}."
                : "Created Agreement draft from reviewed scan."));
        await attempt.FlushBusinessAsync(ct);

        if (string.IsNullOrWhiteSpace(tenant.Email)
            && target.ReviewDisposition == LeaseScanReviewDisposition.NeedsSignatures)
        {
            throw new ScanConfirmationValidationException(
                "The primary tenant needs an email address before this agreement can be sent for signatures.");
        }
        var signer = new LeaseAgreementSigner
        {
            PortfolioId = command.PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}".Trim(),
            // Externally executed originals do not need delivery; keep missing contact explicit
            // rather than inventing a usable address.
            EmailSnapshot = string.IsNullOrWhiteSpace(tenant.Email)
                ? "not-captured"
                : tenant.Email.Trim().ToLowerInvariant(),
            SigningOrder = 1,
            IsRequired = true,
        };
        attempt.Persistence.Add(signer);
        attempt.BindSemanticAudit(signer, Created(command, nameof(LeaseAgreementSigner),
            "Captured primary signer identity from reviewed lease scan."));

        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned)
        {
            var source = await attempt.Persistence.Query<StoredFile>()
                .SingleOrDefaultAsync(file =>
                    file.Id == command.SourceStoredFileId
                    && file.PortfolioId == command.PortfolioId
                    && file.DeletedAt == null, ct)
                ?? throw new ScanConfirmationValidationException(
                    "The exact uploaded lease source is no longer available.");
            var artifact = new LegalDocumentArtifact
            {
                PortfolioId = command.PortfolioId,
                StoredFileId = source.Id,
                ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
                StorageKey = source.FilePath,
                FileName = source.FileName,
                ContentType = source.ContentType,
                ByteLength = source.FileSize,
                ContentSha256 = command.SourceContentSha256!,
                CreatedAtUtc = now,
                CreatedByUserId = command.ConfirmedByUserId,
            };
            attempt.Persistence.Add(artifact);
            attempt.BindSemanticAudit(artifact, Created(command, nameof(LegalDocumentArtifact),
                $"Preserved exact externally executed Agreement bytes and SHA-256{SourceSuffix(command.SourceLabel)}."));
            await attempt.FlushBusinessAsync(ct);

            // An externally executed original is both the issued text and the executed evidence.
            // One immutable artifact preserves the exact bytes without manufacturing a second PDF.
            agreement.IssuedArtifactId = artifact.Id;
            agreement.IssuedAtUtc = now;
            agreement.ExecutedArtifactId = artifact.Id;
            agreement.FullyExecutedAtUtc = now;
            agreement.UpdatedAtUtc = now;
            attempt.BindSemanticAudit(agreement, new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAgreement), agreement.Id,
                AuditLogOperation.Updated, UserId: command.ConfirmedByUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    agreement.IssuedArtifactId,
                    agreement.IssuedAtUtc,
                    agreement.ExecutedArtifactId,
                    agreement.FullyExecutedAtUtc,
                }),
                ChangeReason: $"Marked imported external Agreement fully executed{SourceSuffix(command.SourceLabel)}."));
        }
        await attempt.FlushBusinessAsync(ct);

        return new ScanConfirmationTargetWriteResult(
            agreement.Id, home.UnitId, nameof(LeaseAgreement));
    }

    internal static async Task AuthorizeAsync(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (command.Target.LeaseAgreement is not { UnitId: > 0 } target)
            throw new UnauthorizedAccessException("Lease import has no authorized Unit scope.");
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedHomes(command, target, persistence, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("Lease import is outside the caller's current access scope.");
    }

    private static string SourceSuffix(string? sourceLabel) =>
        string.IsNullOrWhiteSpace(sourceLabel) ? string.Empty : $" from {sourceLabel.Trim()}";

    private static IQueryable<Unit> AuthorizedHomes(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return persistence.Query<Unit>().Where(unit =>
            unit.Id == target.UnitId && unit.PropertyId == target.PropertyId
            && unit.PortfolioId == command.PortfolioId
            && unit.Property != null && unit.Property.PortfolioId == command.PortfolioId
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ConfirmedByUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ConfirmedByUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == unit.PropertyId
                            && scope.PortfolioId == command.PortfolioId))
                    && (assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)
                        || assignment.RoleProfile.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingAgreementsPrepare)))));
    }

    private static async Task<Tenant> ResolveTenantAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        Tenant? tenant = null;
        if (target.TenantId is > 0)
        {
            tenant = await attempt.Persistence.Query<Tenant>().SingleOrDefaultAsync(candidate =>
                candidate.Id == target.TenantId && candidate.PortfolioId == command.PortfolioId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(target.TenantEmail))
        {
            var email = target.TenantEmail.Trim().ToLower();
            tenant = await attempt.Persistence.Query<Tenant>()
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(candidate => candidate.PortfolioId == command.PortfolioId
                    && candidate.Email != null && candidate.Email.Trim().ToLower() == email, ct);
        }
        if (tenant is not null) return tenant;

        var parts = (target.TenantName ?? string.Empty).Trim()
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ScanConfirmationValidationException("Select or name the primary tenant.");
        tenant = new Tenant
        {
            PortfolioId = command.PortfolioId,
            FirstName = parts[0],
            LastName = parts.Length == 2 ? parts[1] : string.Empty,
            Email = string.IsNullOrWhiteSpace(target.TenantEmail) ? null : target.TenantEmail.Trim(),
            Phone = target.TenantPhone,
            EmergencyContact = target.TenantEmergencyContact,
            CreatedAt = now,
            UpdatedAt = now,
        };
        attempt.Persistence.Add(tenant);
        attempt.BindSemanticAudit(tenant, Created(command, nameof(Tenant),
            "Created primary tenant from reviewed lease scan."));
        await attempt.FlushBusinessAsync(ct);
        return tenant;
    }

    private static async Task<LeaseManagement> ResolveRelationshipAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        HomeFacts home,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (target.LeaseManagementId is > 0)
        {
            return await attempt.Persistence.Query<LeaseManagement>().SingleOrDefaultAsync(relationship =>
                relationship.Id == target.LeaseManagementId
                && relationship.PortfolioId == command.PortfolioId
                && relationship.PropertyId == home.PropertyId
                && relationship.UnitId == home.UnitId, ct)
                ?? throw new ScanConfirmationValidationException(
                    "The selected rental relationship does not match this property and Unit.");
        }
        if (home.HasOpenRelationship)
            throw new ScanConfirmationValidationException(
                "This Unit already has an open rental relationship. Select it explicitly or resolve the occupancy first.");

        var relationship = new LeaseManagement
        {
            PortfolioId = command.PortfolioId,
            PropertyId = home.PropertyId,
            UnitId = home.UnitId,
            RelationshipNumber = $"LM-SCAN-{command.DraftId:D8}",
            PlannedPossessionAtUtc = target.StartDate,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        attempt.Persistence.Add(relationship);
        attempt.BindSemanticAudit(relationship, Created(command, nameof(LeaseManagement),
            "Created canonical rental relationship from reviewed lease scan."));
        await attempt.FlushBusinessAsync(ct);
        return relationship;
    }

    private static async Task<TenantAccount> ResolveAccountAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        LeaseManagement relationship,
        string currency,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var account = await attempt.Persistence.Query<TenantAccount>()
            .SingleOrDefaultAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == relationship.Id, ct);
        if (account is not null)
        {
            if (target.TenantAccountId is > 0 && target.TenantAccountId != account.Id)
                throw new ScanConfirmationValidationException(
                    "The selected tenant account does not belong to this rental relationship.");
            return account;
        }
        if (target.TenantAccountId is > 0)
            throw new ScanConfirmationValidationException(
                "The selected tenant account does not belong to this rental relationship.");

        account = new TenantAccount
        {
            PortfolioId = command.PortfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-SCAN-{command.DraftId:D8}",
            Currency = currency,
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
        };
        attempt.Persistence.Add(account);
        attempt.BindSemanticAudit(account, Created(command, nameof(TenantAccount),
            "Opened canonical tenant account from reviewed lease scan."));
        await attempt.FlushBusinessAsync(ct);
        return account;
    }

    private static void Validate(ConfirmScanDraftCommand command, ScanLeaseTargetData target)
    {
        if (target.ReviewDisposition is null || target.PropertyId <= 0 || target.UnitId is not > 0
            || target.StartDate is null || target.MonthlyRent is null or < 0
            || target.RentDueDay is not (>= 1 and <= 31)
            || target.SecurityDeposit is < 0 || target.LateFee is < 0
            || target.GracePeriodDays is < 0 or > 31 || target.TermsSchemaVersion <= 0)
            throw new ScanConfirmationValidationException(
                "Lease review requires its disposition, property, Unit, term start, rent, and rent due day.");
        if (target.EndDate.HasValue && target.EndDate.Value.Date < target.StartDate.Value.Date)
            throw new ScanConfirmationValidationException("Lease end date cannot precede its start date.");
        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned
            && (command.SourceStoredFileId is not > 0
                || string.IsNullOrWhiteSpace(command.SourceContentSha256)
                || command.SourceContentSha256.Length != 64))
            throw new ScanConfirmationValidationException(
                "An already-signed import requires the exact uploaded source and SHA-256.");
        if (command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || command.ConfirmedByUserId <= 0)
            throw new UnauthorizedAccessException("The current access envelope is incomplete.");
    }

    private static string NormalizeJsonObject(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "{}" : value;
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ScanConfirmationValidationException("Agreement terms must be a JSON object.");
        return document.RootElement.GetRawText();
    }

    private static AtomicSemanticAudit Created(
        ConfirmScanDraftCommand command,
        string entityType,
        string reason) => new(
            command.PortfolioId,
            entityType,
            0,
            AuditLogOperation.Created,
            UserId: command.ConfirmedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.DraftId,
                SourceSha256 = command.SourceContentSha256,
            }),
            ChangeReason: reason);

    private sealed record HomeFacts(int UnitId, int PropertyId, string Currency, bool HasOpenRelationship);
}
