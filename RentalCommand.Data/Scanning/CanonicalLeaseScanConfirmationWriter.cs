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
        // Different lease scans for the same empty portfolio must not create duplicate physical
        // inventory. This transaction-scoped lock serializes matching/creation before Unit locking.
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var home = await ResolveHomeAsync(command, target, attempt, now, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, home.UnitId, ct);
        if (target.LeaseManagementId is > 0)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.LeaseManagement, target.LeaseManagementId.Value, ct);
        }

        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned)
        {
            var existing = await (
                from artifact in attempt.Persistence.Query<LegalDocumentArtifact>()
                join existingAgreement in attempt.Persistence.Query<LeaseAgreement>()
                    on new { ArtifactId = artifact.Id, artifact.PortfolioId }
                    equals new { ArtifactId = existingAgreement.ExecutedArtifactId!.Value, existingAgreement.PortfolioId }
                where artifact.PortfolioId == command.PortfolioId
                    && artifact.ContentSha256 == command.SourceContentSha256
                    && existingAgreement.LeaseManagement!.PropertyId == home.PropertyId
                    && existingAgreement.LeaseManagement.UnitId == home.UnitId
                select new ScanConfirmationTargetWriteResult(
                    existingAgreement.Id,
                    existingAgreement.LeaseManagement!.UnitId,
                    nameof(LeaseAgreement),
                    null,
                    existingAgreement.LeaseManagementId))
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
            agreement.Id, home.UnitId, nameof(LeaseAgreement), null, relationship.Id);
    }

    internal static async Task AuthorizeAsync(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (command.Target.LeaseAgreement is not { } target)
            throw new UnauthorizedAccessException("Lease import has no authorized rental scope.");
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var authorized = target.PropertyId > 0 && target.UnitId is > 0
            ? await AuthorizedHomes(
                    command, target.PropertyId, target.UnitId.Value, persistence, now)
                .AnyAsync(ct)
            : target.PropertyId > 0
                ? await AuthorizedProperties(command, target.PropertyId, persistence, now)
                    .AnyAsync(ct)
            : await CanBootstrapHome(command, persistence, now).AnyAsync(ct);
        if (!authorized)
            throw new UnauthorizedAccessException("Lease import is outside the caller's current access scope.");
    }

    private static string SourceSuffix(string? sourceLabel) =>
        string.IsNullOrWhiteSpace(sourceLabel) ? string.Empty : $" from {sourceLabel.Trim()}";

    private static IQueryable<Unit> AuthorizedHomes(
        ConfirmScanDraftCommand command,
        int propertyId,
        int unitId,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return persistence.Query<Unit>().Where(unit =>
            unit.Id == unitId && unit.PropertyId == propertyId
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

    private static IQueryable<WorkspaceMembership> CanBootstrapHome(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return persistence.Query<WorkspaceMembership>().Where(membership =>
            membership.AccessContextId == command.AccessContextId
            && membership.PortfolioId == command.PortfolioId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= now
            && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
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
            && assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && assignment.PortfolioId == command.PortfolioId
                && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                && assignment.RoleProfile!.Capabilities.Any(capability =>
                    capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)));
    }

    private static IQueryable<Property> AuthorizedProperties(
        ConfirmScanDraftCommand command,
        int propertyId,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return persistence.Query<Property>().Where(property =>
            property.Id == propertyId && property.PortfolioId == command.PortfolioId
            && property.DeletedAt == null
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
                            scope.PropertyId == property.Id
                            && scope.PortfolioId == command.PortfolioId))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage))));
    }

    private static async Task<HomeFacts> ResolveHomeAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (target.UnitId is > 0)
        {
            if (target.PropertyId <= 0)
                throw new ScanConfirmationValidationException(
                    "A selected Unit must include its Property.");
            return await AuthorizedHomes(
                    command, target.PropertyId, target.UnitId.Value, attempt.Persistence, now)
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
        }

        Property property;
        if (target.PropertyId > 0)
        {
            property = await AuthorizedProperties(
                    command, target.PropertyId, attempt.Persistence, now)
                .SingleOrDefaultAsync(ct)
                ?? throw new UnauthorizedAccessException(
                    "Creating a Unit from this lease scan is outside the caller's Property scope.");
        }
        else
        {
            if (!await CanBootstrapHome(command, attempt.Persistence, now).AnyAsync(ct))
                throw new UnauthorizedAccessException(
                    "Creating a Property and Unit from a lease scan requires all-property rental-management authority.");

            var address = RequireHomeText(target.PropertyAddress, "Property street address");
            var city = RequireHomeText(target.PropertyCity, "Property city");
            var state = RequireHomeText(target.PropertyState, "Property state");
            var postalCode = RequireHomeText(target.PropertyPostalCode, "Property postal code");
            var addressKey = address.ToLowerInvariant();
            var cityKey = city.ToLowerInvariant();
            var stateKey = state.ToLowerInvariant();
            var postalKey = postalCode.ToLowerInvariant();
            var propertyMatch = await attempt.Persistence.Query<Property>()
                .Where(candidate => candidate.PortfolioId == command.PortfolioId
                    && candidate.DeletedAt == null
                    && candidate.AddressLine1.Trim().ToLower() == addressKey
                    && candidate.City.Trim().ToLower() == cityKey
                    && candidate.State.Trim().ToLower() == stateKey
                    && candidate.PostalCode.Trim().ToLower() == postalKey)
                .GroupBy(_ => 1)
                .Select(group => new { Count = group.Count(), Id = group.Min(candidate => candidate.Id) })
                .SingleOrDefaultAsync(ct);
            if (propertyMatch?.Count > 1)
                throw new ScanConfirmationValidationException(
                    "More than one Property matches the reviewed address. Select the intended Property explicitly.");
            if (propertyMatch is not null)
            {
                property = await attempt.Persistence.Query<Property>()
                    .SingleAsync(candidate => candidate.Id == propertyMatch.Id
                        && candidate.PortfolioId == command.PortfolioId, ct);
            }
            else
            {
                property = new Property
                {
                    PortfolioId = command.PortfolioId,
                    Name = string.IsNullOrWhiteSpace(target.PropertyName)
                        ? address
                        : target.PropertyName.Trim(),
                    PropertyType = ParsePropertyType(target.PropertyType, target.UnitNumber),
                    Status = PropertyStatus.Active,
                    AddressLine1 = address,
                    City = city,
                    State = state,
                    PostalCode = postalCode,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                attempt.Persistence.Add(property);
                attempt.BindSemanticAudit(property, Created(command, nameof(Property),
                    "Created physical Property from reviewed lease scan."));
                await attempt.FlushBusinessAsync(ct);
            }
        }

        var unitNumber = string.IsNullOrWhiteSpace(target.UnitNumber)
            ? "Property"
            : target.UnitNumber.Trim();
        var unitKey = unitNumber.ToLowerInvariant();
        var unitMatch = await attempt.Persistence.Query<Unit>()
            .Where(unit => unit.PortfolioId == command.PortfolioId
                && unit.PropertyId == property.Id && unit.DeletedAt == null
                && unit.UnitNumber.Trim().ToLower() == unitKey)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Id = group.Min(unit => unit.Id) })
            .SingleOrDefaultAsync(ct);
        if (unitMatch?.Count > 1)
            throw new ScanConfirmationValidationException(
                "More than one Unit matches the reviewed Unit number. Select the intended Unit explicitly.");

        Unit unit;
        if (unitMatch is not null)
        {
            unit = await attempt.Persistence.Query<Unit>()
                .SingleAsync(candidate => candidate.Id == unitMatch.Id
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.PropertyId == property.Id, ct);
        }
        else
        {
            unit = new Unit
            {
                PortfolioId = command.PortfolioId,
                PropertyId = property.Id,
                UnitNumber = unitNumber,
                Bedrooms = target.UnitBedrooms ?? 0m,
                Bathrooms = target.UnitBathrooms ?? 0m,
                SquareFeet = target.UnitSquareFeet,
                MarketRent = target.MonthlyRent ?? 0m,
                CreatedAt = now,
                UpdatedAt = now,
            };
            attempt.Persistence.Add(unit);
            await attempt.FlushBusinessAsync(ct);
            // Unit is portfolio-scoped but intentionally does not implement IAuditable, so it has
            // no tracked mutation for BindSemanticAudit to enrich. Record the exact generated Unit
            // identity as a semantic event after the insert flush instead of leaving an orphan bind.
            attempt.StageSemanticEvent(
                Created(command, nameof(Unit), "Created physical Unit from reviewed lease scan.")
                    with { EntityId = unit.Id },
                now);
        }

        var currency = await attempt.Persistence.Query<Portfolio>()
            .Where(portfolio => portfolio.Id == command.PortfolioId)
            .Select(portfolio => portfolio.Currency)
            .SingleAsync(ct);
        var hasOpenRelationship = await attempt.Persistence.Query<LeaseManagement>()
            .AnyAsync(relationship => relationship.PortfolioId == command.PortfolioId
                && relationship.UnitId == unit.Id
                && relationship.CanceledAtUtc == null
                && relationship.PossessionReturnedAtUtc == null, ct);
        return new HomeFacts(unit.Id, property.Id, currency, hasOpenRelationship);
    }

    private static string RequireHomeText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ScanConfirmationValidationException(
                $"{fieldName} is required before creating rental inventory from this scan.");
        return value.Trim();
    }

    private static PropertyType ParsePropertyType(string? value, string? unitNumber)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<PropertyType>(value.Replace(" ", string.Empty), true, out var parsed))
            return parsed;
        return string.IsNullOrWhiteSpace(unitNumber)
            ? PropertyType.SingleFamily
            : PropertyType.MultiFamily;
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
        if (target.ReviewDisposition is null || target.StartDate is null
            || target.MonthlyRent is null or < 0
            || target.RentDueDay is not (>= 1 and <= 31)
            || target.SecurityDeposit is < 0 || target.LateFee is < 0
            || target.GracePeriodDays is < 0 or > 31 || target.TermsSchemaVersion <= 0)
            throw new ScanConfirmationValidationException(
                "Lease review requires its disposition, term start, rent, and rent due day.");
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
