using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Policies;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Scanning;

internal enum CanonicalLeaseScanDocumentSourceKind
{
    ImportedExternalDocument,
    BuiltInRenderer,
    AuthoredTemplateSnapshot,
}

/// <summary>
/// Canonical lease-import writer. It never creates or reads a legacy Lease row. A reviewed scan
/// either becomes an immutable already-executed Agreement artifact or an unissued Agreement draft.
/// </summary>
internal static class CanonicalLeaseScanConfirmationWriter
{
    internal static async Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command, target);
        var documentSourceKind = SelectDocumentSource(target);
        // Different lease scans for the same empty portfolio must not create duplicate physical
        // inventory. This transaction-scoped lock serializes matching/creation before Unit locking.
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(db, command.PortfolioId, ct);
        var now = times.WallClockUtc;
        var rentTrackingStartOn = RentTrackingStartPolicy.Resolve(
            DateOnly.FromDateTime(target.StartDate!.Value),
            target.RentTrackingStartMode,
            target.RentTrackingStartOn,
            times.BusinessDate);
        var home = await ResolveHomeAsync(command, target, db, context, now, ct);
        await context.AcquireLockAsync("Unit", home.UnitId, ct);
        if (target.LeaseManagementId is > 0)
        {
            await context.AcquireLockAsync(
                "LeaseManagement", target.LeaseManagementId.Value, ct);
        }

        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned)
        {
            var existing = await (
                from artifact in db.Set<LegalDocumentArtifact>()
                join existingAgreement in db.Set<LeaseAgreement>()
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

        var templateIsValid = documentSourceKind != CanonicalLeaseScanDocumentSourceKind.AuthoredTemplateSnapshot
            || (target.DocumentTemplateId is > 0
                && await db.Set<DocumentTemplate>().AnyAsync(template =>
                    template.Id == target.DocumentTemplateId
                    && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.ArchivedAtUtc == null
                    && (template.PropertyId == null || template.PropertyId == target.PropertyId), ct));
        if (!templateIsValid)
        {
            throw new ScanConfirmationValidationException(
                "An active lease template is required when the uploaded agreement still needs signatures.");
        }

        var tenant = await ResolveTenantAsync(command, target, db, context, now, ct);
        var relationship = await ResolveRelationshipAsync(command, target, home, db, context, now, ct);
        var account = await ResolveAccountAsync(
            command, target, relationship, home.Currency, rentTrackingStartOn, db, context, now, ct);

        if (target.LeaseAgreementId is > 0)
        {
            var agreementContextIsValid = await db.Set<LeaseAgreement>()
                .AnyAsync(agreement => agreement.Id == target.LeaseAgreementId.Value
                    && agreement.PortfolioId == command.PortfolioId
                    && agreement.LeaseManagementId == relationship.Id, ct);
            if (!agreementContextIsValid)
            {
                throw new ScanConfirmationValidationException(
                    "The selected agreement does not belong to this rental relationship.");
            }
        }

        var existingAgreementCount = await db.Set<LeaseAgreement>()
            .CountAsync(agreement => agreement.PortfolioId == command.PortfolioId
                && agreement.LeaseManagementId == relationship.Id, ct);
        if (existingAgreementCount != 0)
        {
            throw new ScanConfirmationValidationException(
                "This rental relationship already has an agreement. Use the correction, restatement, or renewal flow instead of importing another initial lease.");
        }

        var party = await db.Set<LeaseManagementParty>()
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
            db.Add(party);
            context.BindSemanticAudit(party, Created(command, nameof(LeaseManagementParty),
                "Added the primary tenant from reviewed lease scan."));
            await context.FlushBusinessAsync(ct);
        }

        var fixedTerm = target.EndDate.HasValue;
        LegalDocumentArtifact? importedArtifact = null;
        AtomicLegalDocumentSourceVersionResult sourceVersion;
        if (documentSourceKind == CanonicalLeaseScanDocumentSourceKind.ImportedExternalDocument)
        {
            var source = await db.Set<StoredFile>()
                .SingleOrDefaultAsync(file => file.Id == command.SourceStoredFileId
                    && file.PortfolioId == command.PortfolioId && file.DeletedAt == null, ct)
                ?? throw new ScanConfirmationValidationException(
                    "The exact uploaded lease source is no longer available.");
            importedArtifact = new LegalDocumentArtifact
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
            db.Add(importedArtifact);
            context.BindSemanticAudit(importedArtifact, Created(command, nameof(LegalDocumentArtifact),
                $"Preserved exact externally executed Agreement bytes and SHA-256{SourceSuffix(command.SourceLabel)}."));
            await context.FlushBusinessAsync(ct);
            sourceVersion = await AtomicLeaseMutationPersistence.ResolveImportedDocumentSourceVersionAsync(db,
                context, command.PortfolioId, source.Id, importedArtifact.Id, command.SourceContentSha256!,
                command.SourceLabel, command.ConfirmedByUserId, now, ct);
        }
        else if (documentSourceKind == CanonicalLeaseScanDocumentSourceKind.AuthoredTemplateSnapshot)
        {
            sourceVersion = await AtomicLeaseMutationPersistence.ResolveAuthoredDocumentSourceVersionAsync(db,
                context, command.PortfolioId, home.PropertyId, relationship.Id, target.DocumentTemplateId!.Value,
                command.ConfirmedByUserId, now, ct);
        }
        else
        {
            sourceVersion = await AtomicLeaseMutationPersistence.ResolveBuiltInDocumentSourceVersionAsync(db,
                context, command.PortfolioId, command.ConfirmedByUserId, now, ct);
        }
        if (!sourceVersion.Resolved)
        {
            throw new ScanConfirmationValidationException(
                "The exact legal-document source could not be frozen as immutable provenance.");
        }

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
            DocumentSourceVersionId = sourceVersion.DocumentSourceVersionId,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
            UpdatedAtUtc = now,
            DraftRevision = 1,
        };
        db.Add(agreement);
        context.BindSemanticAudit(agreement, Created(command, nameof(LeaseAgreement),
            target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned
                ? $"Created Agreement for externally executed scan import{SourceSuffix(command.SourceLabel)}."
                : "Created Agreement draft from reviewed scan."));
        await context.FlushBusinessAsync(ct);

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
        db.Add(signer);
        context.BindSemanticAudit(signer, Created(command, nameof(LeaseAgreementSigner),
            "Captured primary signer identity from reviewed lease scan."));

        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned)
        {
            // An externally executed original is both the issued text and the executed evidence.
            // One immutable artifact preserves the exact bytes without manufacturing a second PDF.
            agreement.IssuedArtifactId = importedArtifact!.Id;
            agreement.IssuedAtUtc = now;
            agreement.ExecutedArtifactId = importedArtifact.Id;
            agreement.FullyExecutedAtUtc = now;
            agreement.UpdatedAtUtc = now;
            context.BindSemanticAudit(agreement, new AtomicSemanticAudit(
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
        await context.FlushBusinessAsync(ct);

        return new ScanConfirmationTargetWriteResult(
            agreement.Id, home.UnitId, nameof(LeaseAgreement), null, relationship.Id);
    }

    internal static async Task AuthorizeAsync(
        ConfirmScanDraftCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (command.Target.LeaseAgreement is not { } target)
            throw new UnauthorizedAccessException("Lease import has no authorized rental scope.");
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var authorized = target.PropertyId > 0 && target.UnitId is > 0
            ? await AuthorizedHomes(
                    command, target.PropertyId, target.UnitId.Value, db, now)
                .AnyAsync(ct)
            : target.PropertyId > 0
                ? await AuthorizedProperties(command, target.PropertyId, db, now)
                    .AnyAsync(ct)
            : await CanBootstrapHome(command, db, now).AnyAsync(ct);
        if (!authorized)
            throw new UnauthorizedAccessException("Lease import is outside the caller's current access scope.");
    }

    private static string SourceSuffix(string? sourceLabel) =>
        string.IsNullOrWhiteSpace(sourceLabel) ? string.Empty : $" from {sourceLabel.Trim()}";

    private static IQueryable<Unit> AuthorizedHomes(
        ConfirmScanDraftCommand command,
        int propertyId,
        int unitId,
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return db.Set<Unit>().Where(unit =>
            unit.Id == unitId && unit.PropertyId == propertyId
            && unit.PortfolioId == command.PortfolioId
            && unit.Property != null && unit.Property.PortfolioId == command.PortfolioId
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ConfirmedByUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ConfirmedByUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
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
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return db.Set<WorkspaceMembership>().Where(membership =>
            membership.AccessContextId == command.AccessContextId
            && membership.PortfolioId == command.PortfolioId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= now
            && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ConfirmedByUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
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
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        return db.Set<Property>().Where(property =>
            property.Id == propertyId && property.PortfolioId == command.PortfolioId
            && property.DeletedAt == null
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ConfirmedByUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ConfirmedByUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
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
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        DateTime now,
        CancellationToken ct)
    {
        if (target.UnitId is > 0)
        {
            if (target.PropertyId <= 0)
                throw new ScanConfirmationValidationException(
                    "A selected Unit must include its Property.");
            return await AuthorizedHomes(
                    command, target.PropertyId, target.UnitId.Value, db, now)
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
                    command, target.PropertyId, db, now)
                .SingleOrDefaultAsync(ct)
                ?? throw new UnauthorizedAccessException(
                    "Creating a Unit from this lease scan is outside the caller's Property scope.");
        }
        else
        {
            if (!await CanBootstrapHome(command, db, now).AnyAsync(ct))
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
            var propertyMatch = await db.Set<Property>()
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
                property = await db.Set<Property>()
                    .SingleAsync(candidate => candidate.Id == propertyMatch.Id
                        && candidate.PortfolioId == command.PortfolioId, ct);
            }
            else
            {
                var rentalStructure = target.RentalStructure
                    ?? throw new ScanConfirmationValidationException(
                        "Choose SingleRental or MultiRental before creating rental inventory from this scan.");
                property = new Property
                {
                    PortfolioId = command.PortfolioId,
                    Name = string.IsNullOrWhiteSpace(target.PropertyName)
                        ? address
                        : target.PropertyName.Trim(),
                    PropertyType = ParsePropertyType(target.PropertyType, target.UnitNumber),
                    RentalStructure = rentalStructure,
                    Status = PropertyStatus.Active,
                    AddressLine1 = address,
                    City = city,
                    State = state,
                    PostalCode = postalCode,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Add(property);
                context.BindSemanticAudit(property, Created(command, nameof(Property),
                    "Created physical Property from reviewed lease scan."));
                await context.FlushBusinessAsync(ct);
            }
        }

        var unitNumber = string.IsNullOrWhiteSpace(target.UnitNumber)
            ? "Property"
            : target.UnitNumber.Trim();
        var unitKey = unitNumber.ToLowerInvariant();
        var unitMatch = await db.Set<Unit>()
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
            unit = await db.Set<Unit>()
                .SingleAsync(candidate => candidate.Id == unitMatch.Id
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.PropertyId == property.Id, ct);
        }
        else
        {
            ValidateNewUnitDetails(
                property.PropertyType, target.UnitBedrooms, target.UnitBathrooms);
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
            db.Add(unit);
            await context.FlushBusinessAsync(ct);
            // Unit is portfolio-scoped but intentionally does not implement IAuditable, so it has
            // no tracked mutation for BindSemanticAudit to enrich. Record the exact generated Unit
            // identity as a semantic event after the insert flush instead of leaving an orphan bind.
            context.StageSemanticEvent(
                Created(command, nameof(Unit), "Created physical Unit from reviewed lease scan.")
                    with { EntityId = unit.Id },
                now);
        }

        var currency = await db.Set<Portfolio>()
            .Where(portfolio => portfolio.Id == command.PortfolioId)
            .Select(portfolio => portfolio.Currency)
            .SingleAsync(ct);
        var hasOpenRelationship = await db.Set<LeaseManagement>()
            .AnyAsync(relationship => relationship.PortfolioId == command.PortfolioId
                && relationship.UnitId == unit.Id
                && relationship.CanceledAtUtc == null
                && relationship.PossessionReturnedAtUtc == null, ct);
        return new HomeFacts(unit.Id, property.Id, currency, hasOpenRelationship);
    }

    private static void ValidateNewUnitDetails(
        PropertyType propertyType,
        decimal? bedrooms,
        decimal? bathrooms)
    {
        if (ResidentialUnitPolicy.IsInvalidForCreate(propertyType, bedrooms, bathrooms))
        {
            throw new ScanConfirmationValidationException(
                ResidentialUnitPolicy.RequiredDetailsMessage);
        }
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
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        DateTime now,
        CancellationToken ct)
    {
        Tenant? tenant = null;
        if (target.TenantId is > 0)
        {
            tenant = await db.Set<Tenant>().SingleOrDefaultAsync(candidate =>
                candidate.Id == target.TenantId && candidate.PortfolioId == command.PortfolioId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(target.TenantEmail))
        {
            var email = target.TenantEmail.Trim().ToLower();
            tenant = await db.Set<Tenant>()
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
        db.Add(tenant);
        context.BindSemanticAudit(tenant, Created(command, nameof(Tenant),
            "Created primary tenant from reviewed lease scan."));
        await context.FlushBusinessAsync(ct);
        return tenant;
    }

    private static async Task<LeaseManagement> ResolveRelationshipAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        HomeFacts home,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        DateTime now,
        CancellationToken ct)
    {
        if (target.LeaseManagementId is > 0)
        {
            return await db.Set<LeaseManagement>().SingleOrDefaultAsync(relationship =>
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
            PossessionGivenAtUtc = target.PossessionGivenAtUtc,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        db.Add(relationship);
        context.BindSemanticAudit(relationship, Created(command, nameof(LeaseManagement),
            "Created canonical rental relationship from reviewed lease scan."));
        await context.FlushBusinessAsync(ct);
        return relationship;
    }

    private static async Task<TenantAccount> ResolveAccountAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseTargetData target,
        LeaseManagement relationship,
        string currency,
        DateOnly? rentTrackingStartOn,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        DateTime now,
        CancellationToken ct)
    {
        var account = await db.Set<TenantAccount>()
            .SingleOrDefaultAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == relationship.Id, ct);
        if (account is not null)
        {
            if (target.TenantAccountId is > 0 && target.TenantAccountId != account.Id)
                throw new ScanConfirmationValidationException(
                    "The selected tenant account does not belong to this rental relationship.");
            if (account.RentTrackingStartOn != rentTrackingStartOn)
            {
                account.RentTrackingStartOn = rentTrackingStartOn;
                context.BindSemanticAudit(account, new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(TenantAccount),
                    account.Id,
                    AuditLogOperation.Updated,
                    UserId: command.ConfirmedByUserId,
                    ChangeReason: "Applied the reviewed rent tracking start choice from the lease scan."));
            }
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
            RentTrackingStartOn = rentTrackingStartOn,
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = command.ConfirmedByUserId,
        };
        db.Add(account);
        context.BindSemanticAudit(account, Created(command, nameof(TenantAccount),
            "Opened canonical tenant account from reviewed lease scan."));
        await context.FlushBusinessAsync(ct);
        return account;
    }

    private static void Validate(ConfirmScanDraftCommand command, ScanLeaseTargetData target)
    {
        if (target.ReviewDisposition is null || target.StartDate is null
            || !Enum.IsDefined(target.RentTrackingStartMode)
            || (target.RentTrackingStartMode == RentTrackingStartMode.CustomCutoffDate
                && target.RentTrackingStartOn is null)
            || (target.RentTrackingStartMode != RentTrackingStartMode.CustomCutoffDate
                && target.RentTrackingStartOn is not null)
            || target.MonthlyRent is null or < 0
            || target.RentDueDay is not (>= 1 and <= 31)
            || target.SecurityDeposit is < 0 || target.LateFee is < 0
            || target.GracePeriodDays is < 0 or > 31 || target.TermsSchemaVersion <= 0)
            throw new ScanConfirmationValidationException(
                "Lease review requires its disposition, term start, rent, and rent due day.");
        if (target.EndDate.HasValue && target.EndDate.Value.Date < target.StartDate.Value.Date)
            throw new ScanConfirmationValidationException("Lease end date cannot precede its start date.");
        if (target.PossessionGivenAtUtc > command.ConfirmedAtUtc)
            throw new ScanConfirmationValidationException(
                "Possession date cannot be later than the confirmation date.");
        if (target.ReviewDisposition == LeaseScanReviewDisposition.AlreadyFullySigned
            && target.LeaseManagementId is not > 0
            && target.StartDate.Value.Date <= command.ConfirmedAtUtc.Date
            && (!target.EndDate.HasValue || target.EndDate.Value.Date >= command.ConfirmedAtUtc.Date)
            && target.PossessionGivenAtUtc is null)
        {
            throw new ScanConfirmationValidationException(
                "A fully signed lease whose term has started requires the reviewed possession date.");
        }
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

    internal static CanonicalLeaseScanDocumentSourceKind SelectDocumentSource(
        ScanLeaseTargetData target) => target.ReviewDisposition switch
    {
        LeaseScanReviewDisposition.AlreadyFullySigned =>
            CanonicalLeaseScanDocumentSourceKind.ImportedExternalDocument,
        LeaseScanReviewDisposition.NeedsSignatures when target.DocumentTemplateId is null =>
            CanonicalLeaseScanDocumentSourceKind.BuiltInRenderer,
        LeaseScanReviewDisposition.NeedsSignatures when target.DocumentTemplateId > 0 =>
            CanonicalLeaseScanDocumentSourceKind.AuthoredTemplateSnapshot,
        _ => throw new ScanConfirmationValidationException(
            "Choose a valid custom lease template or use the supplied lease source."),
    };

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
