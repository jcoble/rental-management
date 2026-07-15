using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Leasing;

/// <summary>Pure database implementation of the pre-possession relationship command.</summary>
public sealed class PrepareMoveInHandler
    : IAtomicCommandHandler<PrepareMoveInCommand, PrepareMoveInResult>,
      IAtomicReplayAuthorizer<PrepareMoveInCommand>
{
    public async Task<PrepareMoveInResult> HandleAsync(
        PrepareMoveInCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var hasInvalidPartyRole = command.Parties.Any(party => !Enum.IsDefined(party.Role));
        var hasInvalidTermType = !Enum.IsDefined(command.TermType);
        var customTemplateId = command.DocumentTemplateId.GetValueOrDefault();
        ValidateAuthorizationShape(command);

        // Every command taking both locks uses this order. The Unit id is explicit in the request
        // so no unlocked application read is needed to discover the first aggregate lock.
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        if (command.ApplicationId is { } applicationId)
        {
            await attempt.Locking.AcquireAsync(AtomicLockResource.RentalApplication, applicationId, ct);
        }

        // Query 1 is the database wall clock. Security eligibility never trusts command time or a
        // simulation clock. Query 2 combines the live access envelope and application facts.
        var wallClockUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(wallClockUtc);
        var target = command.ApplicationId.HasValue
            ? await AuthorizedApplications(command, attempt.Persistence, wallClockUtc)
                .Select(candidate => new PreparationTarget(
                    candidate,
                    candidate.PropertyId!.Value,
                    candidate.UnitId!.Value,
                    candidate.Portfolio!.Currency,
                    command.DocumentTemplateId == null
                    || attempt.Persistence.Query<DocumentTemplate>().Any(template =>
                        template.Id == customTemplateId
                        && template.PortfolioId == command.PortfolioId
                        && template.Kind == DocumentTemplateKind.Lease
                        && template.Status == DocumentTemplateStatus.Active
                        && template.ArchivedAtUtc == null
                        && (template.PropertyId == null || template.PropertyId == candidate.PropertyId)),
                    attempt.Persistence.Query<LeaseManagement>().Any(relationship =>
                        relationship.PortfolioId == command.PortfolioId
                        && relationship.PropertyId == candidate.PropertyId
                        && relationship.UnitId == command.UnitId
                        && relationship.CanceledAtUtc == null
                        && relationship.PossessionGivenAtUtc != null
                        && relationship.PossessionGivenAtUtc <= wallClockUtc
                        && (relationship.PossessionReturnedAtUtc == null
                            || relationship.PossessionReturnedAtUtc > wallClockUtc)
                        && (command.PlannedPossessionAtUtc == null
                            || command.PlannedPossessionAtUtc <= wallClockUtc)),
                    attempt.Persistence.Query<UnitOperationalPeriod>().Any(period =>
                        period.PortfolioId == command.PortfolioId
                        && period.PropertyId == candidate.PropertyId
                        && period.UnitId == command.UnitId
                        && period.StartedAtUtc <= wallClockUtc
                        && (period.EndedAtUtc == null || period.EndedAtUtc > wallClockUtc))))
                .SingleOrDefaultAsync(ct)
            : await AuthorizedUnits(command, attempt.Persistence, wallClockUtc)
                .Select(unit => new PreparationTarget(
                    null,
                    unit.PropertyId,
                    unit.Id,
                    unit.Property!.Portfolio!.Currency,
                command.DocumentTemplateId == null
                || attempt.Persistence.Query<DocumentTemplate>().Any(template =>
                    template.Id == customTemplateId
                    && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.ArchivedAtUtc == null
                    && (template.PropertyId == null || template.PropertyId == unit.PropertyId)),
                attempt.Persistence.Query<LeaseManagement>().Any(relationship =>
                    relationship.PortfolioId == command.PortfolioId
                    && relationship.PropertyId == unit.PropertyId
                    && relationship.UnitId == command.UnitId
                    && relationship.CanceledAtUtc == null
                    && relationship.PossessionGivenAtUtc != null
                    && relationship.PossessionGivenAtUtc <= wallClockUtc
                    && (relationship.PossessionReturnedAtUtc == null
                        || relationship.PossessionReturnedAtUtc > wallClockUtc)
                    && (command.PlannedPossessionAtUtc == null
                        || command.PlannedPossessionAtUtc <= wallClockUtc)),
                attempt.Persistence.Query<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.PropertyId == unit.PropertyId
                    && period.UnitId == command.UnitId
                    && period.StartedAtUtc <= wallClockUtc
                    && (period.EndedAtUtc == null || period.EndedAtUtc > wallClockUtc))))
                .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            // Cross-scope and mismatched-context references are authorization failures. Throwing
            // rolls back the pending receipt, so an unauthorized caller cannot reserve a key.
            throw new UnauthorizedAccessException(
                "The selected application or unit is not authorized in the current portfolio context.");
        }

        // Typed validation results are persisted only after the current caller has passed the live
        // access-envelope check, so malformed input cannot reserve a receipt across tenant scope.
        if (hasInvalidPartyRole)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "Every party role must be a supported lease relationship role.");
        }
        if (hasInvalidTermType)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidAgreementTerms,
                command,
                "The agreement term type is not supported.");
        }

        ValidateCommandShape(command);

        if (target.Application?.PreparedLeaseManagementId is { } preparedLeaseManagementId)
        {
            return Empty(
                PrepareMoveInOutcome.AlreadyPrepared,
                command,
                "This application has already been prepared for move-in.",
                preparedLeaseManagementId);
        }

        if (target.Application is { } application
            && (application.Status != ApplicationStatus.Approved
                || application.ApprovedTenantId is null))
        {
            return Empty(
                PrepareMoveInOutcome.ApplicationNotApproved,
                command,
                "Only an approved application with an approved tenant can be prepared for move-in.");
        }

        if (target.HasConflictingCurrentPossession || target.HasOpenOperationalPeriod)
        {
            return Empty(
                PrepareMoveInOutcome.UnitUnavailable,
                command,
                "The unit has conflicting current possession or an open operational period.");
        }

        if (!target.DocumentSourceIsValid)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidTemplate,
                command,
                "The selected lease template is not active for this property.");
        }

        var sourceVersion = command.DocumentTemplateId is { } documentTemplateId
            ? await attempt.Leasing.ResolveAuthoredDocumentSourceVersionAsync(
                command.PortfolioId, target.PropertyId, 0, documentTemplateId,
                command.CreatedByUserId, wallClockUtc, ct)
            : await attempt.Leasing.ResolveBuiltInDocumentSourceVersionAsync(
                command.PortfolioId, command.CreatedByUserId, wallClockUtc, ct);
        if (!sourceVersion.Resolved)
        {
            return Empty(PrepareMoveInOutcome.InvalidTemplate, command,
                command.DocumentTemplateId.HasValue
                    ? "The selected lease template could not be frozen as immutable source provenance."
                    : "The supplied lease source could not be frozen as immutable provenance.");
        }

        var requestedTenantIds = command.Parties
            .Where(party => party.TenantId.HasValue)
            .Select(party => party.TenantId!.Value)
            .Distinct()
            .ToArray();
        var existingSignerTenantIds = command.Parties
            .Where(party => party.IsAgreementSigner && party.TenantId.HasValue)
            .Select(party => party.TenantId!.Value)
            .ToArray();

        // Existence, signer-email presence, and normalized signer-email uniqueness are evaluated
        // by PostgreSQL in one aggregate query. The following row query supplies only the snapshot
        // values needed to construct the new legal signer records.
        var tenantValidation = await attempt.Persistence.Query<Tenant>()
            .Where(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && requestedTenantIds.Contains(candidate.Id))
            .GroupBy(_ => 1)
            .Select(group => new TenantSelectionValidation(
                group.Count(),
                group.Count(candidate => existingSignerTenantIds.Contains(candidate.Id)),
                group.Count(candidate => existingSignerTenantIds.Contains(candidate.Id)
                    && (candidate.Email == null || candidate.Email.Trim() == string.Empty)),
                group.Where(candidate => existingSignerTenantIds.Contains(candidate.Id)
                        && candidate.Email != null
                        && candidate.Email.Trim() != string.Empty)
                    .Select(candidate => candidate.Email!.Trim().ToLower())
                    .Distinct()
                    .Count()))
            .SingleOrDefaultAsync(ct);

        var tenants = await attempt.Persistence.Query<Tenant>()
            .Where(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && requestedTenantIds.Contains(candidate.Id))
            .Select(candidate => new TenantFacts(
                candidate.Id,
                candidate.FirstName,
                candidate.LastName,
                candidate.Email))
            .ToListAsync(ct);

        var existingSelectionIsValid = requestedTenantIds.Length == 0
            ? tenantValidation is null
            : tenantValidation is not null
                && tenantValidation.RequestedTenantCount == requestedTenantIds.Length
                && tenantValidation.SignerTenantCount == existingSignerTenantIds.Length
                && tenantValidation.MissingSignerEmailCount == 0
                && tenantValidation.DistinctNormalizedSignerEmailCount == existingSignerTenantIds.Length;
        if (!existingSelectionIsValid)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "Every selected existing person must be an active Tenant in this portfolio and every signer must have a unique email address.");
        }

        var primary = command.Parties.Single(party => party.Role == LeaseManagementPartyRole.PrimaryTenant);
        if (target.Application is { ApprovedTenantId: { } approvedTenantId }
            && (primary.TenantId != approvedTenantId || !requestedTenantIds.Contains(approvedTenantId)))
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "The application's approved Tenant must be the initial primary tenant.");
        }

        var tenantById = tenants.ToDictionary(tenant => tenant.Id);
        var suppliedNewTenantEmails = command.Parties
            .Where(party => party.NewTenant != null && !string.IsNullOrWhiteSpace(party.NewTenant.Email))
            .Select(party => party.NewTenant!.Email!.Trim().ToLower())
            .ToArray();
        var newTenantEmails = suppliedNewTenantEmails
            .Distinct()
            .ToArray();
        if (newTenantEmails.Length != suppliedNewTenantEmails.Length)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "Each new household member must use a unique email address.");
        }
        if (newTenantEmails.Length > 0 && await attempt.Persistence.Query<Tenant>()
                .AnyAsync(candidate => candidate.PortfolioId == command.PortfolioId
                    && candidate.Email != null
                    && newTenantEmails.Contains(candidate.Email.Trim().ToLower()), ct))
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "A new household member already exists in this portfolio; select the existing Tenant instead.");
        }

        var requestedSignerEmails = command.Parties
            .Where(party => party.IsAgreementSigner)
            .Select(party => party.TenantId is { } existingTenantId
                ? NormalizeSignerEmail(tenantById[existingTenantId].Email)
                : NormalizeSignerEmail(party.NewTenant!.Email))
            .ToArray();
        if (requestedSignerEmails.Any(email => email is null)
            || requestedSignerEmails.Distinct(StringComparer.Ordinal).Count()
                != requestedSignerEmails.Length)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "Every Agreement signer must have a unique email address.");
        }

        var newTenantRows = command.Parties
            .Where(party => party.NewTenant != null)
            .Select(party => new Tenant
            {
                PortfolioId = command.PortfolioId,
                FirstName = party.NewTenant!.FirstName.Trim(),
                LastName = party.NewTenant.LastName.Trim(),
                Email = NormalizeOptional(party.NewTenant.Email),
                Phone = NormalizeOptional(party.NewTenant.Phone),
                EmergencyContact = NormalizeOptional(party.NewTenant.EmergencyContact),
                CreatedAt = wallClockUtc,
                UpdatedAt = wallClockUtc,
            })
            .ToArray();
        foreach (var tenant in newTenantRows)
        {
            attempt.Persistence.Add(tenant);
            attempt.BindSemanticAudit(tenant, CreatedAudit(
                command, nameof(Tenant), "Created Tenant while preparing a lease relationship."));
        }
        if (newTenantRows.Length > 0)
        {
            await attempt.FlushBusinessAsync(ct);
        }

        var resolvedParties = new List<ResolvedParty>(command.Parties.Count);
        var newTenantIndex = 0;
        foreach (var party in command.Parties)
        {
            if (party.TenantId is { } existingTenantId)
            {
                resolvedParties.Add(new ResolvedParty(party, tenantById[existingTenantId]));
                continue;
            }

            var tenant = newTenantRows[newTenantIndex++];
            var facts = new TenantFacts(tenant.Id, tenant.FirstName, tenant.LastName, tenant.Email);
            tenantById.Add(tenant.Id, facts);
            resolvedParties.Add(new ResolvedParty(party, facts));
        }

        var relationshipPublicId = Guid.NewGuid();
        var relationshipToken = relationshipPublicId.ToString("N")[..12].ToUpperInvariant();

        var relationship = new LeaseManagement
        {
            PublicId = relationshipPublicId,
            PortfolioId = command.PortfolioId,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId,
            RelationshipNumber = $"LM-{relationshipToken}",
            PlannedPossessionAtUtc = command.PlannedPossessionAtUtc,
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
            UpdatedAtUtc = wallClockUtc,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = command.PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{relationshipToken}",
            Currency = target.Currency,
            OpenedAtUtc = wallClockUtc,
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = command.PortfolioId,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{relationshipToken}-V1",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = command.TermType,
            TermStartOn = command.TermStartOn,
            TermEndOn = command.TermEndOn,
            GoverningFromOn = command.TermStartOn,
            BaseRentAmount = command.BaseRentAmount,
            RentDueDay = command.RentDueDay,
            SecurityDepositObligation = command.SecurityDepositObligation,
            LateFeeAmount = command.LateFeeAmount,
            GracePeriodDays = command.GracePeriodDays,
            Currency = target.Currency,
            TermsSchemaVersion = command.TermsSchemaVersion,
            TermsPayload = command.TermsPayload,
            DocumentSourceVersionId = sourceVersion.DocumentSourceVersionId,
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
            UpdatedAtUtc = wallClockUtc,
            DraftRevision = 1,
        };

        var partyRows = resolvedParties.Select(resolved => new LeaseManagementParty
        {
            PortfolioId = command.PortfolioId,
            LeaseManagement = relationship,
            TenantId = resolved.Tenant.Id,
            Role = resolved.Input.Role,
            EffectiveFrom = command.PartyEffectiveFrom,
            GuarantorLegalNoticeEligible = resolved.Input.GuarantorLegalNoticeEligible,
            ChangeReason = resolved.Input.ChangeReason.Trim(),
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
        }).ToArray();
        var partyByTenantId = partyRows.ToDictionary(party => party.TenantId);
        var signerRows = resolvedParties
            .Where(party => party.Input.IsAgreementSigner)
            .OrderBy(party => party.Input.SigningOrder)
            .Select(party =>
            {
                var tenant = party.Tenant;
                return new LeaseAgreementSigner
                {
                    PortfolioId = command.PortfolioId,
                    LeaseAgreement = agreement,
                    LeaseManagementParty = partyByTenantId[tenant.Id],
                    TenantId = tenant.Id,
                    SignerRole = ToSignerRole(party.Input.Role),
                    NameSnapshot = $"{tenant.FirstName} {tenant.LastName}".Trim(),
                    EmailSnapshot = NormalizeSignerEmail(tenant.Email)!,
                    SigningOrder = party.Input.SigningOrder!.Value,
                    IsRequired = party.Input.IsRequiredSigner,
                };
            })
            .ToArray();

        attempt.Persistence.Add(relationship);
        attempt.Persistence.Add(account);
        attempt.Persistence.Add(agreement);
        attempt.Persistence.AddRange(partyRows);
        attempt.Persistence.AddRange(signerRows);
        SecurityDepositAccount? depositAccount = null;
        if (command.CreateSecurityDepositAccount)
        {
            depositAccount = new SecurityDepositAccount
            {
                PortfolioId = command.PortfolioId,
                TenantAccount = account,
                OriginatingAgreement = agreement,
                Currency = target.Currency,
                CreatedAtUtc = wallClockUtc,
                CreatedByUserId = command.CreatedByUserId,
            };
            attempt.Persistence.Add(depositAccount);
        }

        if (target.Application is { } trackedApplication)
        {
            trackedApplication.PreparedLeaseManagement = relationship;
            trackedApplication.UpdatedAt = wallClockUtc;
            attempt.BindSemanticAudit(trackedApplication, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(RentalApplication),
                trackedApplication.Id,
                AuditLogOperation.Updated,
                UserId: command.CreatedByUserId,
                ChangeReason: "Application prepared for move-in."));
        }

        attempt.BindSemanticAudit(relationship, CreatedAudit(
            command, nameof(LeaseManagement), "Prepared lease relationship and initial Agreement."));
        attempt.BindSemanticAudit(account, CreatedAudit(
            command, nameof(TenantAccount), "Opened tenant account during move-in preparation."));
        attempt.BindSemanticAudit(agreement, CreatedAudit(
            command, nameof(LeaseAgreement), "Created initial lease agreement draft."));
        await attempt.FlushBusinessAsync(ct);

        TenantLedgerEntry? openingBalance = null;
        if (command.OpeningBalanceAmount is { } signedOpening && signedOpening != 0m)
        {
            openingBalance = new TenantLedgerEntry
            {
                PortfolioId = command.PortfolioId,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.OpeningBalance,
                Direction = signedOpening > 0m
                    ? TenantLedgerDirection.Debit
                    : TenantLedgerDirection.Credit,
                Amount = Math.Abs(signedOpening),
                Currency = account.Currency,
                EffectiveOn = command.OpeningBalanceEffectiveOn!.Value,
                PostedAtUtc = wallClockUtc,
                Description = string.IsNullOrWhiteSpace(command.OpeningBalanceNote)
                    ? "Opening tenant-account balance"
                    : command.OpeningBalanceNote.Trim(),
                BusinessKey = $"opening:{agreement.PublicId}",
                LeaseAgreementId = agreement.Id,
                CreatedByUserId = command.CreatedByUserId,
            };
            attempt.Persistence.Add(openingBalance);
            await attempt.FlushBusinessAsync(ct);
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantAccount),
                account.Id,
                AuditLogOperation.Updated,
                UserId: command.CreatedByUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    openingBalance.Id,
                    openingBalance.Direction,
                    openingBalance.Amount,
                    openingBalance.EffectiveOn,
                    openingBalance.LeaseAgreementId,
                }),
                ChangeReason: "Posted opening balance while preparing the tenant account."),
                wallClockUtc);
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(TenantLedgerEntry),
                    entityId = openingBalance.Id,
                    data = new { TenantAccountId = account.Id, openingBalance.Direction },
                }),
                IdempotencyKey = OutboxIdempotency.Create(
                    "prepare-move-in-opening-balance",
                    command.DeliveryIdempotencyKey),
                CreatedAtUtc = wallClockUtc,
                NextAttemptAtUtc = wallClockUtc,
            });
        }

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(LeaseManagement),
                entityId = relationship.Id,
                data = new
                {
                    relationship.Id,
                    relationship.PublicId,
                    relationship.PropertyId,
                    relationship.UnitId,
                    TenantAccountId = account.Id,
                    LeaseAgreementId = agreement.Id,
                },
            }),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = wallClockUtc,
            NextAttemptAtUtc = wallClockUtc,
        });

        return new PrepareMoveInResult(
            PrepareMoveInOutcome.Prepared,
            command.ApplicationId,
            relationship.Id,
            account.Id,
            agreement.Id,
            openingBalance?.Id,
            depositAccount?.Id,
            resolvedParties.Select(party => party.Tenant.Id).ToArray(),
            partyRows.Select(party => party.Id).ToArray(),
            signerRows.Select(signer => signer.Id).ToArray(),
            null);
    }

    public async Task AuthorizeReplayAsync(
        PrepareMoveInCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        ValidateAuthorizationShape(command);
        var securityNowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        var authorized = command.ApplicationId.HasValue
            ? await AuthorizedApplications(command, persistence, securityNowUtc).AnyAsync(ct)
            : await AuthorizedUnits(command, persistence, securityNowUtc).AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The selected application or unit is not authorized in the current portfolio context.");
        }
    }

    private static IQueryable<RentalApplication> AuthorizedApplications(
        PrepareMoveInCommand command,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc)
    {
        var effectiveAssignments = persistence.Query<MembershipRoleAssignment>()
            .Where(assignment =>
                assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= securityNowUtc
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));

        return persistence.Query<RentalApplication>()
            .Where(candidate =>
                candidate.Id == command.ApplicationId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.PropertyId != null
                && candidate.UnitId == command.UnitId
                && candidate.Unit != null
                && candidate.Unit.PortfolioId == command.PortfolioId
                && candidate.Unit.PropertyId == candidate.PropertyId
                && candidate.Property != null
                && candidate.Property.PortfolioId == command.PortfolioId
                && persistence.Query<AuthSession>().Any(session =>
                    session.Id == command.AuthSessionId
                    && session.UserId == command.CreatedByUserId
                    && session.ActiveAccessContextId == command.AccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > securityNowUtc)
                && persistence.Query<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.AccessContextId
                    && context.UserId == command.CreatedByUserId
                    && context.PortfolioId == command.PortfolioId
                    && context.AccessRevision == command.ExpectedAccessRevision
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null
                    && context.RevokedAtUtc == null)
                && persistence.Query<WorkspaceMembership>().Any(membership =>
                    membership.AccessContextId == command.AccessContextId
                    && membership.PortfolioId == command.PortfolioId
                    && membership.Status == WorkspaceMembershipStatus.Active
                    && membership.SuspendedAtUtc == null
                    && membership.RevokedAtUtc == null
                    && membership.EffectiveFromUtc <= securityNowUtc
                    && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                    && effectiveAssignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == membership.Id
                        && assignment.PortfolioId == command.PortfolioId
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PropertyId == candidate.PropertyId
                                    && scope.PortfolioId == command.PortfolioId)))
                        && (assignment.RoleProfile!.Capabilities.Any(capability =>
                                capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                                && capability.CapabilityDefinition.AuthorizationTargetKind
                                    == CapabilityAuthorizationTargetKind.Property)
                            || assignment.RoleProfile.Capabilities.Any(capability =>
                                    capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingAgreementsPrepare
                                    && capability.CapabilityDefinition.AuthorizationTargetKind
                                        == CapabilityAuthorizationTargetKind.Property)))));
    }

    private static IQueryable<Unit> AuthorizedUnits(
        PrepareMoveInCommand command,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc)
    {
        var effectiveAssignments = persistence.Query<MembershipRoleAssignment>()
            .Where(assignment =>
                assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= securityNowUtc
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));

        return persistence.Query<Unit>()
            .Where(unit =>
                unit.Id == command.UnitId
                && unit.PortfolioId == command.PortfolioId
                && unit.Property != null
                && unit.Property.PortfolioId == command.PortfolioId
                && persistence.Query<AuthSession>().Any(session =>
                    session.Id == command.AuthSessionId
                    && session.UserId == command.CreatedByUserId
                    && session.ActiveAccessContextId == command.AccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > securityNowUtc)
                && persistence.Query<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.AccessContextId
                    && context.UserId == command.CreatedByUserId
                    && context.PortfolioId == command.PortfolioId
                    && context.AccessRevision == command.ExpectedAccessRevision
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null
                    && context.RevokedAtUtc == null)
                && persistence.Query<WorkspaceMembership>().Any(membership =>
                    membership.AccessContextId == command.AccessContextId
                    && membership.PortfolioId == command.PortfolioId
                    && membership.Status == WorkspaceMembershipStatus.Active
                    && membership.SuspendedAtUtc == null
                    && membership.RevokedAtUtc == null
                    && membership.EffectiveFromUtc <= securityNowUtc
                    && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                    && effectiveAssignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == membership.Id
                        && assignment.PortfolioId == command.PortfolioId
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PropertyId == unit.PropertyId
                                    && scope.PortfolioId == command.PortfolioId)))
                        && (assignment.RoleProfile!.Capabilities.Any(capability =>
                                capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                                && capability.CapabilityDefinition.AuthorizationTargetKind
                                    == CapabilityAuthorizationTargetKind.Property)
                            || assignment.RoleProfile.Capabilities.Any(capability =>
                                capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingAgreementsPrepare
                                && capability.CapabilityDefinition.AuthorizationTargetKind
                                    == CapabilityAuthorizationTargetKind.Property)))));
    }

    private static void ValidateAuthorizationShape(PrepareMoveInCommand command)
    {
        if (command.ApplicationId is <= 0 || command.UnitId <= 0 || command.PortfolioId <= 0
            || command.CreatedByUserId <= 0 || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0)
        {
            throw new ArgumentException("Portfolio, unit, and actor ids are required; ApplicationId must be positive when supplied.");
        }
    }

    internal static void ValidateCommandShape(PrepareMoveInCommand command)
    {
        ValidateAuthorizationShape(command);
        if (command.PartyEffectiveFrom == default || command.TermStartOn == default)
        {
            throw new ArgumentException("Party effective date and Agreement term start are required.");
        }
        if (command.Parties.Count == 0
            || command.Parties.Any(party => party.TenantId.HasValue == (party.NewTenant is not null))
            || command.Parties.Where(party => party.TenantId.HasValue)
                .Select(party => party.TenantId).Distinct().Count()
                != command.Parties.Count(party => party.TenantId.HasValue)
            || command.Parties.Count(party => party.Role == LeaseManagementPartyRole.PrimaryTenant) != 1
            || command.Parties.Any(party => party.TenantId is <= 0
                || party.NewTenant is { } newTenant
                    && (string.IsNullOrWhiteSpace(newTenant.FirstName)
                        || string.IsNullOrWhiteSpace(newTenant.LastName)
                        || newTenant.FirstName.Trim().Length > 100
                        || newTenant.LastName.Trim().Length > 100
                        || newTenant.Email?.Trim().Length > 320
                        || newTenant.Phone?.Trim().Length > 50
                        || newTenant.EmergencyContact?.Trim().Length > 500)
                || string.IsNullOrWhiteSpace(party.ChangeReason)
                || (party.Role != LeaseManagementPartyRole.Guarantor && party.GuarantorLegalNoticeEligible))
            || !command.Parties.Any(party => party.Role == LeaseManagementPartyRole.PrimaryTenant
                && party.IsAgreementSigner
                && party.IsRequiredSigner))
        {
            throw new ArgumentException(
                "Parties must be unique and include exactly one required primary-tenant signer.");
        }
        var signerOrders = command.Parties
            .Where(party => party.IsAgreementSigner)
            .Select(party => party.SigningOrder)
            .ToArray();
        if (signerOrders.Length == 0
            || signerOrders.Any(order => order is null or <= 0)
            || signerOrders.Distinct().Count() != signerOrders.Length
            || command.Parties.Any(party => !party.IsAgreementSigner && party.SigningOrder is not null)
            || command.Parties.Any(party => party.IsAgreementSigner && !party.IsRequiredSigner)
            || command.Parties.Any(party => party.IsAgreementSigner
                && party.Role == LeaseManagementPartyRole.Occupant))
        {
            throw new ArgumentException("Agreement signer order, required status, and party role are invalid.");
        }
        if (command.DocumentTemplateId is <= 0
            || command.TermsSchemaVersion <= 0 || string.IsNullOrWhiteSpace(command.TermsPayload)
            || command.BaseRentAmount < 0 || command.SecurityDepositObligation < 0
            || command.LateFeeAmount < 0 || command.RentDueDay is < 1 or > 31
            || command.GracePeriodDays is < 0 or > 31
            || (command.TermType == LeaseAgreementTermType.FixedTerm
                && (command.TermEndOn is null || command.TermEndOn < command.TermStartOn))
            || (command.TermType == LeaseAgreementTermType.MonthToMonth && command.TermEndOn is not null))
        {
            throw new ArgumentException("Initial agreement terms are invalid.");
        }
        if (command.OpeningBalanceAmount.HasValue != command.OpeningBalanceEffectiveOn.HasValue
            || command.OpeningBalanceAmount == 0m
            || (!string.IsNullOrWhiteSpace(command.OpeningBalanceNote)
                && !command.OpeningBalanceAmount.HasValue)
            || command.OpeningBalanceNote?.Trim().Length > 500)
        {
            throw new ArgumentException(
                "Opening balance amount and effective date must be supplied together; amount cannot be zero.");
        }
        using var payload = JsonDocument.Parse(command.TermsPayload);
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("TermsPayload must be a JSON object.");
        }
        if (command.DeliveryIdempotencyKey.Length is 0 or > 200)
        {
            throw new ArgumentException("Delivery idempotency key is invalid.");
        }
    }

    private static LeaseLegalSignerRole ToSignerRole(LeaseManagementPartyRole role) => role switch
    {
        LeaseManagementPartyRole.PrimaryTenant => LeaseLegalSignerRole.PrimaryTenant,
        LeaseManagementPartyRole.CoTenant => LeaseLegalSignerRole.CoTenant,
        LeaseManagementPartyRole.Guarantor => LeaseLegalSignerRole.Guarantor,
        _ => throw new ArgumentOutOfRangeException(nameof(role), "Occupants cannot be agreement signers."),
    };

    private static string? NormalizeSignerEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AtomicSemanticAudit CreatedAudit(
        PrepareMoveInCommand command,
        string entityType,
        string reason) => new(
            command.PortfolioId,
            entityType,
            0,
            AuditLogOperation.Created,
            UserId: command.CreatedByUserId,
            ChangeReason: reason);

    private static PrepareMoveInResult Empty(
        PrepareMoveInOutcome outcome,
        PrepareMoveInCommand command,
        string error,
        int leaseManagementId = 0) => new(
            outcome,
            command.ApplicationId,
            leaseManagementId,
            0,
            0,
            null,
            null,
            Array.Empty<int>(),
            Array.Empty<int>(),
            Array.Empty<int>(),
            error);

    private sealed record PreparationTarget(
        RentalApplication? Application,
        int PropertyId,
        int UnitId,
        string Currency,
        bool DocumentSourceIsValid,
        bool HasConflictingCurrentPossession,
        bool HasOpenOperationalPeriod);

    private sealed record TenantSelectionValidation(
        int RequestedTenantCount,
        int SignerTenantCount,
        int MissingSignerEmailCount,
        int DistinctNormalizedSignerEmailCount);

    private sealed record TenantFacts(int Id, string FirstName, string LastName, string? Email);
    private sealed record ResolvedParty(PrepareMoveInParty Input, TenantFacts Tenant);
}
