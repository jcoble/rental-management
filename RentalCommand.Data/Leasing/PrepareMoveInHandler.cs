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
        ValidateAuthorizationShape(command);

        // Every command taking both locks uses this order. The Unit id is explicit in the request
        // so no unlocked application read is needed to discover the first aggregate lock.
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.RentalApplication, command.ApplicationId, ct);

        // Query 1 is the database wall clock. Security eligibility never trusts command time or a
        // simulation clock. Query 2 combines the live access envelope and application facts.
        var wallClockUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(wallClockUtc);
        var application = await AuthorizedApplications(command, attempt.Persistence, wallClockUtc)
            .Select(candidate => new ApplicationPreparationTarget(
                candidate,
                candidate.PropertyId!.Value,
                candidate.UnitId!.Value,
                candidate.Portfolio!.Currency,
                attempt.Persistence.Query<DocumentTemplate>().Any(template =>
                    template.Id == command.DocumentTemplateId
                    && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.Version == command.DocumentTemplateVersion
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
            .SingleOrDefaultAsync(ct);

        if (application is null)
        {
            // Cross-scope and mismatched-context references are authorization failures. Throwing
            // rolls back the pending receipt, so an unauthorized caller cannot reserve a key.
            throw new UnauthorizedAccessException(
                "The application, property, and unit are not authorized in the current portfolio context.");
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

        if (application.Entity.PreparedLeaseManagementId.HasValue)
        {
            return Empty(
                PrepareMoveInOutcome.AlreadyPrepared,
                command,
                "This application has already been prepared for move-in.",
                application.Entity.PreparedLeaseManagementId.Value);
        }

        if (application.Entity.Status != ApplicationStatus.Approved
            || application.Entity.ApprovedTenantId is null)
        {
            return Empty(
                PrepareMoveInOutcome.ApplicationNotApproved,
                command,
                "Only an approved application with an approved tenant can be prepared for move-in.");
        }

        if (application.HasConflictingCurrentPossession || application.HasOpenOperationalPeriod)
        {
            return Empty(
                PrepareMoveInOutcome.UnitUnavailable,
                command,
                "The unit has conflicting current possession or an open operational period.");
        }

        if (!application.TemplateIsValid)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidTemplate,
                command,
                "The selected lease template is not active at the requested version for this property.");
        }

        var requestedTenantIds = command.Parties.Select(party => party.TenantId).Distinct().ToArray();
        var signerTenantIds = command.Parties
            .Where(party => party.IsAgreementSigner)
            .Select(party => party.TenantId)
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
                group.Count(candidate => signerTenantIds.Contains(candidate.Id)),
                group.Count(candidate => signerTenantIds.Contains(candidate.Id)
                    && (candidate.Email == null || candidate.Email.Trim() == string.Empty)),
                group.Where(candidate => signerTenantIds.Contains(candidate.Id)
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

        if (tenantValidation is null
            || tenantValidation.RequestedTenantCount != requestedTenantIds.Length
            || tenantValidation.SignerTenantCount != signerTenantIds.Length
            || tenantValidation.MissingSignerEmailCount != 0
            || tenantValidation.DistinctNormalizedSignerEmailCount != signerTenantIds.Length
            || !requestedTenantIds.Contains(application.Entity.ApprovedTenantId.Value))
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "Every chosen person must be an active Tenant in this portfolio, including the approved applicant.");
        }

        var primary = command.Parties.Single(party => party.Role == LeaseManagementPartyRole.PrimaryTenant);
        if (primary.TenantId != application.Entity.ApprovedTenantId.Value)
        {
            return Empty(
                PrepareMoveInOutcome.InvalidParties,
                command,
                "The application's approved Tenant must be the initial primary tenant.");
        }

        var tenantById = tenants.ToDictionary(tenant => tenant.Id);
        var normalizedSignerEmails = command.Parties
            .Where(party => party.IsAgreementSigner)
            .Select(party => new
            {
                party.TenantId,
                Email = NormalizeSignerEmail(tenantById[party.TenantId].Email),
            })
            .ToArray();
        var signerEmailByTenantId = normalizedSignerEmails.ToDictionary(
            signer => signer.TenantId,
            signer => signer.Email!);

        var relationship = new LeaseManagement
        {
            PortfolioId = command.PortfolioId,
            PropertyId = application.PropertyId,
            UnitId = application.UnitId,
            RelationshipNumber = $"LM-{application.Entity.Id:D8}",
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
            AccountNumber = $"TA-{application.Entity.Id:D8}",
            Currency = application.Currency,
            OpenedAtUtc = wallClockUtc,
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = command.PortfolioId,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{application.Entity.Id:D8}-V1",
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
            Currency = application.Currency,
            TermsSchemaVersion = command.TermsSchemaVersion,
            TermsPayload = command.TermsPayload,
            DocumentTemplateId = command.DocumentTemplateId,
            DocumentTemplateVersion = command.DocumentTemplateVersion,
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
            UpdatedAtUtc = wallClockUtc,
            DraftRevision = 1,
        };

        var partyRows = command.Parties.Select(party => new LeaseManagementParty
        {
            PortfolioId = command.PortfolioId,
            LeaseManagement = relationship,
            TenantId = party.TenantId,
            Role = party.Role,
            EffectiveFrom = command.PartyEffectiveFrom,
            GuarantorLegalNoticeEligible = party.GuarantorLegalNoticeEligible,
            ChangeReason = party.ChangeReason.Trim(),
            CreatedAtUtc = wallClockUtc,
            CreatedByUserId = command.CreatedByUserId,
        }).ToArray();
        var partyByTenantId = partyRows.ToDictionary(party => party.TenantId);
        var signerRows = command.Parties
            .Where(party => party.IsAgreementSigner)
            .OrderBy(party => party.SigningOrder)
            .Select(party =>
            {
                var tenant = tenantById[party.TenantId];
                return new LeaseAgreementSigner
                {
                    PortfolioId = command.PortfolioId,
                    LeaseAgreement = agreement,
                    LeaseManagementParty = partyByTenantId[party.TenantId],
                    TenantId = party.TenantId,
                    SignerRole = ToSignerRole(party.Role),
                    NameSnapshot = $"{tenant.FirstName} {tenant.LastName}".Trim(),
                    EmailSnapshot = signerEmailByTenantId[party.TenantId],
                    SigningOrder = party.SigningOrder!.Value,
                    IsRequired = party.IsRequiredSigner,
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
                Currency = application.Currency,
                CreatedAtUtc = wallClockUtc,
                CreatedByUserId = command.CreatedByUserId,
            };
            attempt.Persistence.Add(depositAccount);
        }

        var trackedApplication = application.Entity;
        trackedApplication.PreparedLeaseManagement = relationship;
        trackedApplication.UpdatedAt = wallClockUtc;

        attempt.BindSemanticAudit(relationship, CreatedAudit(
            command, nameof(LeaseManagement), "Prepared lease relationship from approved application."));
        attempt.BindSemanticAudit(account, CreatedAudit(
            command, nameof(TenantAccount), "Opened tenant account during move-in preparation."));
        attempt.BindSemanticAudit(agreement, CreatedAudit(
            command, nameof(LeaseAgreement), "Created initial lease agreement draft."));
        attempt.BindSemanticAudit(trackedApplication, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(RentalApplication),
            command.ApplicationId,
            AuditLogOperation.Updated,
            UserId: command.CreatedByUserId,
            ChangeReason: "Application prepared for move-in."));

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
        var authorized = await AuthorizedApplications(command, persistence, securityNowUtc)
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The application, property, and unit are not authorized in the current portfolio context.");
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
                                capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)
                            || (assignment.RoleProfile.Capabilities.Any(capability =>
                                    capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingApplicationsManage)
                                && assignment.RoleProfile.Capabilities.Any(capability =>
                                    capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingAgreementsPrepare)
                                && assignment.RoleProfile.Capabilities.Any(capability =>
                                    capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage))))));
    }

    private static void ValidateAuthorizationShape(PrepareMoveInCommand command)
    {
        if (command.ApplicationId <= 0 || command.UnitId <= 0 || command.PortfolioId <= 0
            || command.CreatedByUserId <= 0 || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0)
        {
            throw new ArgumentException("Portfolio, application, unit, and actor ids are required.");
        }
    }

    private static void ValidateCommandShape(PrepareMoveInCommand command)
    {
        ValidateAuthorizationShape(command);
        if (command.PartyEffectiveFrom == default || command.TermStartOn == default)
        {
            throw new ArgumentException("Portfolio, application, unit, and actor ids are required.");
        }
        if (command.Parties.Count == 0
            || command.Parties.Select(party => party.TenantId).Distinct().Count() != command.Parties.Count
            || command.Parties.Count(party => party.Role == LeaseManagementPartyRole.PrimaryTenant) != 1
            || command.Parties.Any(party => party.TenantId <= 0
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
            || command.Parties.Any(party => party.IsAgreementSigner
                && party.Role == LeaseManagementPartyRole.Occupant))
        {
            throw new ArgumentException("Agreement signer order and party role are invalid.");
        }
        if (command.DocumentTemplateId <= 0 || command.DocumentTemplateVersion <= 0
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
            error);

    private sealed record ApplicationPreparationTarget(
        RentalApplication Entity,
        int PropertyId,
        int UnitId,
        string Currency,
        bool TemplateIsValid,
        bool HasConflictingCurrentPossession,
        bool HasOpenOperationalPeriod);

    private sealed record TenantSelectionValidation(
        int RequestedTenantCount,
        int SignerTenantCount,
        int MissingSignerEmailCount,
        int DistinctNormalizedSignerEmailCount);

    private sealed record TenantFacts(int Id, string FirstName, string LastName, string? Email);
}
