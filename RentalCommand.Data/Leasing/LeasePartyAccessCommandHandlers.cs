using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class AddEffectivePartyHandler
    : IAtomicCommandHandler<AddEffectivePartyCommand, LeasePartyMutationResult>,
      IAtomicReplayAuthorizer<AddEffectivePartyCommand>
{
    public async Task<LeasePartyMutationResult> HandleAsync(
        AddEffectivePartyCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, attempt.Persistence, nowUtc)
            .Select(relationship => new AddPartyTarget(
                relationship,
                attempt.Persistence.Query<Tenant>().Any(tenant =>
                    tenant.Id == command.TenantId && tenant.PortfolioId == command.PortfolioId),
                relationship.Parties.Any(party =>
                    party.TenantId == command.TenantId
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= command.EffectiveFrom)),
                relationship.Parties.Count(party =>
                    party.Role == LeaseManagementPartyRole.PrimaryTenant
                    && party.EffectiveFrom <= command.EffectiveFrom
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= command.EffectiveFrom)),
                LeasePartyAccessCommandSupport.IsResponsible(command.Role)
                    && command.LegalBasisAddendumId == null
                    && command.LegalBasisAgreementId != null
                    && relationship.Agreements.Any(agreement =>
                                agreement.Id == command.LegalBasisAgreementId
                                && (agreement.ChangeType == LeaseAgreementChangeType.Correction
                                    || agreement.ChangeType == LeaseAgreementChangeType.Restatement)
                                && agreement.ReplacesAgreementId != null
                                && agreement.GoverningFromOn == command.EffectiveFrom
                                && (agreement.SupersededEffectiveOn == null
                                    || agreement.SupersededEffectiveOn > command.EffectiveFrom)
                                && agreement.FullyExecutedAtUtc != null
                                && agreement.VoidedAtUtc == null
                                && agreement.Signers.Any(signer =>
                                    signer.IsRequired && signer.TenantId == command.TenantId))))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        if (!LeasePartyAccessCommandSupport.IsMutable(target.Relationship)
            || target.Relationship.PossessionReturnedAtUtc != null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The relationship is no longer mutable.");
        }
        if (!Enum.IsDefined(command.Role)
            || (command.Role != LeaseManagementPartyRole.Guarantor
                && command.GuarantorLegalNoticeEligible)
            || command.EffectiveFrom < currentDate
            || string.IsNullOrWhiteSpace(command.ChangeReason)
            || command.ChangeReason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The party details are invalid.");
        }
        if (!target.TenantExists || target.HasOverlappingMembership)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty,
                command,
                "The Tenant is unavailable or already has an overlapping relationship membership.");
        }
        if ((command.Role == LeaseManagementPartyRole.PrimaryTenant && target.PrimaryCount != 0)
            || (command.Role != LeaseManagementPartyRole.PrimaryTenant && target.PrimaryCount != 1))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidPrimaryTransition,
                command,
                "The mutation must preserve exactly one primary tenant on the requested effective date.");
        }
        if (LeasePartyAccessCommandSupport.IsResponsible(command.Role)
            && (!command.SameRelationshipConfirmed
                || command.LegalBasisAgreementId is not > 0
                || command.LegalBasisAddendumId != null
                || !target.LegalBasisValid))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.LegalBasisRequired,
                command,
                "A qualifying executed replacement or restated Agreement effective on this date and signed by this Tenant is required.");
        }

        var party = LeasePartyAccessCommandSupport.NewParty(
            command.PortfolioId,
            target.Relationship,
            command.TenantId,
            command.Role,
            command.EffectiveFrom,
            command.GuarantorLegalNoticeEligible,
            command.ChangeReason,
            nowUtc,
            command.ActorUserId);
        attempt.Persistence.Add(party);
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindCreated(attempt, party, command, "Added effective relationship party.");
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            attempt, target.Relationship, command, "Relationship party added.");
        await attempt.FlushBusinessAsync(ct);
        LeasePartyAccessCommandSupport.StageOutbox(attempt, command, nowUtc, party.Id, "party-added");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            party.Id,
            null,
            null,
            Array.Empty<int>(),
            null);
    }

    public Task AuthorizeReplayAsync(
        AddEffectivePartyCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);

    private sealed record AddPartyTarget(
        LeaseManagement Relationship,
        bool TenantExists,
        bool HasOverlappingMembership,
        int PrimaryCount,
        bool LegalBasisValid);
}

public sealed class EndEffectivePartyHandler
    : IAtomicCommandHandler<EndEffectivePartyCommand, LeasePartyMutationResult>,
      IAtomicReplayAuthorizer<EndEffectivePartyCommand>
{
    public async Task<LeasePartyMutationResult> HandleAsync(
        EndEffectivePartyCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;
        var successorStart = command.EffectiveThrough.AddDays(1);

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, attempt.Persistence, nowUtc)
            .Select(relationship => new EndPartyTarget(
                relationship,
                relationship.Parties.FirstOrDefault(party => party.Id == command.PartyId),
                command.PrimarySuccessorPartyId == null
                    ? null
                    : relationship.Parties.FirstOrDefault(party =>
                        party.Id == command.PrimarySuccessorPartyId),
                relationship.Parties.Count(party =>
                    party.Role == LeaseManagementPartyRole.PrimaryTenant
                    && party.EffectiveFrom <= currentDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= currentDate)),
                relationship.Parties.Where(party => party.Id == command.PartyId)
                    .Select(party => party.Role != LeaseManagementPartyRole.Occupant
                        && command.LegalBasisAddendumId == null
                        && command.LegalBasisAgreementId != null
                        && relationship.Agreements.Any(agreement =>
                                    agreement.Id == command.LegalBasisAgreementId
                                    && (agreement.ChangeType == LeaseAgreementChangeType.Correction
                                        || agreement.ChangeType == LeaseAgreementChangeType.Restatement)
                                    && agreement.ReplacesAgreementId != null
                                    && agreement.GoverningFromOn == successorStart
                                    && (agreement.SupersededEffectiveOn == null
                                        || agreement.SupersededEffectiveOn > successorStart)
                                    && agreement.FullyExecutedAtUtc != null
                                    && agreement.VoidedAtUtc == null
                                    && agreement.Signers.Any(signer =>
                                        signer.IsRequired && signer.TenantId == party.TenantId)
                                    && (command.PrimarySuccessorPartyId == null
                                        || agreement.Signers.Any(signer => signer.TenantId ==
                                            relationship.Parties
                                                .Where(successor => successor.Id == command.PrimarySuccessorPartyId)
                                                .Select(successor => successor.TenantId)
                                                .FirstOrDefault()))))
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        if (!LeasePartyAccessCommandSupport.IsMutable(target.Relationship))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The relationship is no longer mutable.", command.PartyId);
        }
        if (!Enum.IsDefined(command.AccessDisposition)
            || command.EffectiveThrough != currentDate.AddDays(-1)
            || string.IsNullOrWhiteSpace(command.ChangeReason)
            || command.ChangeReason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidEffectiveDate, command, "The party end details are invalid.", command.PartyId);
        }
        if (target.Party is null
            || target.Party.EffectiveThrough != null
            || target.Party.EffectiveFrom > command.EffectiveThrough)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The party is not an open membership.", command.PartyId);
        }
        if (command.AccessDisposition == TenantAccessDisposition.ContinueOnReplacementMembership)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.AccessTransitionInvalid,
                command,
                "Ending a membership has no same-person replacement; choose revoke or retained history.",
                command.PartyId);
        }
        if (target.CurrentPrimaryCount != 1)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidPrimaryTransition,
                command,
                "The mutation requires exactly one current primary tenant.",
                command.PartyId);
        }

        LeaseManagementParty? successorReplacement = null;
        if (target.Party.Role == LeaseManagementPartyRole.PrimaryTenant)
        {
            if (target.Successor is null
                || target.Successor.Id == target.Party.Id
                || target.Successor.Role == LeaseManagementPartyRole.PrimaryTenant
                || target.Successor.EffectiveFrom > command.EffectiveThrough
                || (target.Successor.EffectiveThrough != null
                    && target.Successor.EffectiveThrough < successorStart))
            {
                return LeasePartyAccessCommandSupport.Error(
                    LeasePartyMutationOutcome.InvalidPrimaryTransition,
                    command,
                    "Ending a primary tenant requires an active non-primary successor.",
                    command.PartyId);
            }
        }
        else if (command.PrimarySuccessorPartyId != null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidPrimaryTransition,
                command,
                "A primary successor is valid only when ending the current primary tenant.",
                command.PartyId);
        }

        if (LeasePartyAccessCommandSupport.IsResponsible(target.Party.Role)
            && (!command.SameRelationshipConfirmed
                || command.LegalBasisAgreementId is not > 0
                || command.LegalBasisAddendumId != null
                || !target.LegalBasisValid))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.LegalBasisRequired,
                command,
                "A qualifying executed replacement or restated Agreement effective on this transition and signed by every affected responsible Tenant is required.",
                command.PartyId);
        }

        target.Party.EffectiveThrough = command.EffectiveThrough;
        target.Party.ChangeReason = command.ChangeReason.Trim();
        LeasePartyAccessCommandSupport.BindUpdated(attempt, target.Party, command, "Ended relationship party membership.");

        if (target.Successor is not null)
        {
            target.Successor.EffectiveThrough = command.EffectiveThrough;
            target.Successor.ChangeReason = command.ChangeReason.Trim();
            successorReplacement = LeasePartyAccessCommandSupport.NewParty(
                command.PortfolioId,
                target.Relationship,
                target.Successor.TenantId,
                LeaseManagementPartyRole.PrimaryTenant,
                successorStart,
                false,
                command.ChangeReason,
                nowUtc,
                command.ActorUserId);
            attempt.Persistence.Add(successorReplacement);
            LeasePartyAccessCommandSupport.BindUpdated(
                attempt, target.Successor, command, "Ended prior role for primary succession.");
            LeasePartyAccessCommandSupport.BindCreated(
                attempt, successorReplacement, command, "Promoted successor to primary tenant.");
        }
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            attempt, target.Relationship, command, "Relationship party ended.");
        await attempt.FlushBusinessAsync(ct);
        var accessTransitions = new List<AtomicTenantAccessTransition>
        {
            new(target.Party.Id, null,
                command.AccessDisposition == TenantAccessDisposition.RevokeImmediately
                    ? AtomicTenantAccessTransitionKind.Revoke
                    : AtomicTenantAccessTransitionKind.Retain),
        };
        if (target.Successor is not null && successorReplacement is not null)
        {
            accessTransitions.Add(new(target.Successor.Id, successorReplacement.Id,
                AtomicTenantAccessTransitionKind.ContinueOnReplacement));
        }
        var accessResult = await attempt.Leasing.TransitionTenantAccessAsync(
            command.PortfolioId, command.LeaseManagementId, accessTransitions,
            command.ActorUserId, nowUtc, command.ChangeReason, ct);
        LeasePartyAccessCommandSupport.StageAccessTransitionAudits(attempt, command, accessResult, nowUtc);
        LeasePartyAccessCommandSupport.StageOutbox(attempt, command, nowUtc, target.Party.Id, "party-ended");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            null,
            successorReplacement?.Id,
            accessResult.ActiveAccessIds.Concat(accessResult.CreatedAccessIds).ToArray(),
            null);
    }

    public Task AuthorizeReplayAsync(
        EndEffectivePartyCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);

    private sealed record EndPartyTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        LeaseManagementParty? Successor,
        int CurrentPrimaryCount,
        bool LegalBasisValid);
}

public sealed class ChangeEffectivePartyRoleHandler
    : IAtomicCommandHandler<ChangeEffectivePartyRoleCommand, LeasePartyMutationResult>,
      IAtomicReplayAuthorizer<ChangeEffectivePartyRoleCommand>
{
    public async Task<LeasePartyMutationResult> HandleAsync(
        ChangeEffectivePartyRoleCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, attempt.Persistence, nowUtc)
            .Select(relationship => new ChangeRoleTarget(
                relationship,
                relationship.Parties.FirstOrDefault(party => party.Id == command.PartyId),
                command.CompanionPrimaryPartyId == null
                    ? null
                    : relationship.Parties.FirstOrDefault(party => party.Id == command.CompanionPrimaryPartyId),
                relationship.Parties.Count(party =>
                    party.Role == LeaseManagementPartyRole.PrimaryTenant
                    && party.EffectiveFrom <= currentDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= currentDate)),
                relationship.Parties.Where(party => party.Id == command.PartyId)
                    .Select(party =>
                        command.LegalBasisAddendumId == null
                        && command.LegalBasisAgreementId != null
                        && relationship.Agreements.Any(agreement =>
                                    agreement.Id == command.LegalBasisAgreementId
                                    && (agreement.ChangeType == LeaseAgreementChangeType.Correction
                                        || agreement.ChangeType == LeaseAgreementChangeType.Restatement)
                                    && agreement.ReplacesAgreementId != null
                                    && agreement.GoverningFromOn == command.EffectiveOn
                                    && (agreement.SupersededEffectiveOn == null
                                        || agreement.SupersededEffectiveOn > command.EffectiveOn)
                                    && agreement.FullyExecutedAtUtc != null
                                    && agreement.VoidedAtUtc == null
                                    && agreement.Signers.Any(signer =>
                                        signer.IsRequired && signer.TenantId == party.TenantId)
                                    && (command.CompanionPrimaryPartyId == null
                                        || agreement.Signers.Any(signer => signer.TenantId ==
                                            relationship.Parties
                                                .Where(companion => companion.Id == command.CompanionPrimaryPartyId)
                                                .Select(companion => companion.TenantId)
                                                .FirstOrDefault()))))
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        if (!LeasePartyAccessCommandSupport.IsMutable(target.Relationship)
            || target.Relationship.PossessionReturnedAtUtc != null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The relationship is no longer mutable.", command.PartyId);
        }
        if (!Enum.IsDefined(command.NewRole)
            || !Enum.IsDefined(command.AccessDisposition)
            || (command.NewRole != LeaseManagementPartyRole.Guarantor
                && command.GuarantorLegalNoticeEligible)
            || command.EffectiveOn != currentDate
            || string.IsNullOrWhiteSpace(command.ChangeReason)
            || command.ChangeReason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidEffectiveDate, command, "The role transition details are invalid.", command.PartyId);
        }
        if (target.Party is null
            || target.Party.EffectiveThrough != null
            || target.Party.EffectiveFrom >= command.EffectiveOn
            || target.Party.Role == command.NewRole)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The target is not an open membership with a different role.", command.PartyId);
        }
        if (target.CurrentPrimaryCount != 1)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidPrimaryTransition,
                command,
                "The mutation requires exactly one current primary tenant.",
                command.PartyId);
        }

        var swapsPrimary = target.Party.Role == LeaseManagementPartyRole.PrimaryTenant
            || command.NewRole == LeaseManagementPartyRole.PrimaryTenant;
        if (swapsPrimary)
        {
            var companionIsValid = target.Companion is not null
                && target.Companion.Id != target.Party.Id
                && target.Companion.EffectiveThrough == null
                && target.Companion.EffectiveFrom < command.EffectiveOn
                && command.CompanionNewRole is not null
                && Enum.IsDefined(command.CompanionNewRole.Value)
                && (command.CompanionNewRole == LeaseManagementPartyRole.Guarantor
                    || !command.CompanionGuarantorLegalNoticeEligible)
                && ((target.Party.Role == LeaseManagementPartyRole.PrimaryTenant
                        && target.Companion.Role != LeaseManagementPartyRole.PrimaryTenant
                        && command.CompanionNewRole == LeaseManagementPartyRole.PrimaryTenant)
                    || (command.NewRole == LeaseManagementPartyRole.PrimaryTenant
                        && target.Companion.Role == LeaseManagementPartyRole.PrimaryTenant
                        && command.CompanionNewRole != LeaseManagementPartyRole.PrimaryTenant));
            if (!companionIsValid)
            {
                return LeasePartyAccessCommandSupport.Error(
                    LeasePartyMutationOutcome.InvalidPrimaryTransition,
                    command,
                    "A primary swap requires the other active party and its explicit replacement role.",
                    command.PartyId);
            }
        }
        else if (command.CompanionPrimaryPartyId != null || command.CompanionNewRole != null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidPrimaryTransition,
                command,
                "A companion transition is valid only for a primary swap.",
                command.PartyId);
        }

        var responsibleChange = LeasePartyAccessCommandSupport.IsResponsible(target.Party.Role)
            || LeasePartyAccessCommandSupport.IsResponsible(command.NewRole)
            || target.Companion is not null;
        if (responsibleChange
            && (!command.SameRelationshipConfirmed
                || command.LegalBasisAgreementId is not > 0
                || command.LegalBasisAddendumId != null
                || !target.LegalBasisValid))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.LegalBasisRequired,
                command,
                "A qualifying executed replacement or restated Agreement effective on this transition and signed by every affected responsible Tenant is required.",
                command.PartyId);
        }

        var priorDay = command.EffectiveOn.AddDays(-1);
        target.Party.EffectiveThrough = priorDay;
        target.Party.ChangeReason = command.ChangeReason.Trim();
        var replacement = LeasePartyAccessCommandSupport.NewParty(
            command.PortfolioId,
            target.Relationship,
            target.Party.TenantId,
            command.NewRole,
            command.EffectiveOn,
            command.GuarantorLegalNoticeEligible,
            command.ChangeReason,
            nowUtc,
            command.ActorUserId);
        attempt.Persistence.Add(replacement);
        LeasePartyAccessCommandSupport.BindUpdated(attempt, target.Party, command, "Ended prior relationship role.");
        LeasePartyAccessCommandSupport.BindCreated(attempt, replacement, command, "Started replacement relationship role.");

        LeaseManagementParty? companionReplacement = null;
        if (target.Companion is not null)
        {
            target.Companion.EffectiveThrough = priorDay;
            target.Companion.ChangeReason = command.ChangeReason.Trim();
            companionReplacement = LeasePartyAccessCommandSupport.NewParty(
                command.PortfolioId,
                target.Relationship,
                target.Companion.TenantId,
                command.CompanionNewRole!.Value,
                command.EffectiveOn,
                command.CompanionGuarantorLegalNoticeEligible,
                command.ChangeReason,
                nowUtc,
                command.ActorUserId);
            attempt.Persistence.Add(companionReplacement);
            LeasePartyAccessCommandSupport.BindUpdated(attempt, target.Companion, command, "Ended companion role for primary swap.");
            LeasePartyAccessCommandSupport.BindCreated(attempt, companionReplacement, command, "Started companion role for primary swap.");
        }

        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            attempt, target.Relationship, command, "Relationship party role changed.");
        await attempt.FlushBusinessAsync(ct);
        var accessTransitions = new List<AtomicTenantAccessTransition>
        {
            new(target.Party.Id,
                command.AccessDisposition == TenantAccessDisposition.ContinueOnReplacementMembership
                    ? replacement.Id
                    : null,
                command.AccessDisposition switch
                {
                    TenantAccessDisposition.RevokeImmediately => AtomicTenantAccessTransitionKind.Revoke,
                    TenantAccessDisposition.RetainHistoricalReadOnly => AtomicTenantAccessTransitionKind.Retain,
                    TenantAccessDisposition.ContinueOnReplacementMembership =>
                        AtomicTenantAccessTransitionKind.ContinueOnReplacement,
                    _ => throw new ArgumentOutOfRangeException(nameof(command.AccessDisposition)),
                }),
        };
        if (target.Companion is not null && companionReplacement is not null)
        {
            accessTransitions.Add(new(target.Companion.Id, companionReplacement.Id,
                AtomicTenantAccessTransitionKind.ContinueOnReplacement));
        }
        var accessResult = await attempt.Leasing.TransitionTenantAccessAsync(
            command.PortfolioId, command.LeaseManagementId, accessTransitions,
            command.ActorUserId, nowUtc, command.ChangeReason, ct);
        LeasePartyAccessCommandSupport.StageAccessTransitionAudits(attempt, command, accessResult, nowUtc);
        LeasePartyAccessCommandSupport.StageOutbox(attempt, command, nowUtc, replacement.Id, "party-role-changed");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            replacement.Id,
            companionReplacement?.Id,
            accessResult.ActiveAccessIds.Concat(accessResult.CreatedAccessIds).ToArray(),
            null);
    }

    public Task AuthorizeReplayAsync(
        ChangeEffectivePartyRoleCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);

    private sealed record ChangeRoleTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        LeaseManagementParty? Companion,
        int CurrentPrimaryCount,
        bool LegalBasisValid);
}

public sealed class GrantTenantUserAccessHandler
    : IAtomicCommandHandler<GrantTenantUserAccessCommand, LeasePartyMutationResult>,
      IAtomicReplayAuthorizer<GrantTenantUserAccessCommand>
{
    public async Task<LeasePartyMutationResult> HandleAsync(
        GrantTenantUserAccessCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;
        var targetContextId = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .Where(context => context.UserId == command.ApplicationUserId
                && context.PortfolioId == command.PortfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            .Select(context => context.Id)
            .SingleOrDefaultAsync(ct);
        if (targetContextId <= 0)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command,
                "The user has no active access context in this workspace.", command.PartyId);
        }
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, targetContextId, ct);

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, attempt.Persistence, nowUtc)
            .Select(relationship => new GrantAccessTarget(
                relationship,
                relationship.Parties.FirstOrDefault(party => party.Id == command.PartyId
                    && party.EffectiveFrom <= currentDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= currentDate)),
                attempt.Persistence.Query<WorkspaceAccessContext>().FirstOrDefault(context =>
                    context.Id == targetContextId
                    && context.UserId == command.ApplicationUserId
                    && context.PortfolioId == command.PortfolioId
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null
                    && context.RevokedAtUtc == null),
                attempt.Persistence.Query<TenantUserAccess>().Any(access =>
                    access.PortfolioId == command.PortfolioId
                    && access.AccessContext!.UserId == command.ApplicationUserId
                    && access.LeaseManagementPartyId == command.PartyId
                    && access.RevokedAtUtc == null)))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        if (target.Party is null || target.TargetContext is null
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The existing user and active party do not match.", command.PartyId);
        }
        if (target.HasActiveGrant)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.AlreadyActive, command, "This user already has active access through the party.", command.PartyId);
        }

        var access = LeasePartyAccessCommandSupport.NewAccess(
            command.PortfolioId,
            target.TargetContext,
            target.Party,
            nowUtc,
            command.ActorUserId,
            command.Reason);
        attempt.Persistence.Add(access);
        target.TargetContext.AdvanceRevision(target.TargetContext.AccessRevision);
        target.TargetContext.UpdatedAtUtc = nowUtc;
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindCreated(attempt, access, command, "Granted tenant portal access.");
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            attempt, target.Relationship, command, "Tenant portal access granted.");
        await attempt.FlushBusinessAsync(ct);
        LeasePartyAccessCommandSupport.StageOutbox(attempt, command, nowUtc, access.Id, "tenant-access-granted");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            null,
            null,
            new[] { access.Id },
            null);
    }

    public Task AuthorizeReplayAsync(
        GrantTenantUserAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);

    private sealed record GrantAccessTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        WorkspaceAccessContext? TargetContext,
        bool HasActiveGrant);
}

public sealed class RevokeTenantUserAccessHandler
    : IAtomicCommandHandler<RevokeTenantUserAccessCommand, LeasePartyMutationResult>,
      IAtomicReplayAuthorizer<RevokeTenantUserAccessCommand>
{
    public async Task<LeasePartyMutationResult> HandleAsync(
        RevokeTenantUserAccessCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var targetContextId = await attempt.Persistence.Query<TenantUserAccess>()
            .Where(access => access.Id == command.TenantUserAccessId
                && access.PortfolioId == command.PortfolioId)
            .Select(access => access.AccessContextId)
            .SingleOrDefaultAsync(ct);
        if (targetContextId <= 0)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command,
                "The tenant access grant does not exist in this workspace.", command.PartyId);
        }
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, targetContextId, ct);

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, attempt.Persistence, nowUtc)
            .Select(relationship => new RevokeAccessTarget(
                relationship,
                attempt.Persistence.Query<TenantUserAccess>().FirstOrDefault(access =>
                    access.Id == command.TenantUserAccessId
                    && access.PortfolioId == command.PortfolioId
                    && access.LeaseManagementPartyId == command.PartyId
                    && access.LeaseManagementParty!.LeaseManagementId == relationship.Id),
                attempt.Persistence.Query<WorkspaceAccessContext>().FirstOrDefault(context =>
                    context.Id == targetContextId
                    && context.PortfolioId == command.PortfolioId)))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        if (target.Access is null || target.TargetContext is null || string.IsNullOrWhiteSpace(command.Reason)
            || command.Reason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command, "The access grant does not belong to this relationship party.", command.PartyId);
        }
        if (target.Access.RevokedAtUtc != null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.AlreadyRevoked, command, "The access grant is already revoked.", command.PartyId,
                new[] { target.Access.Id });
        }

        target.Access.RevokedAtUtc = nowUtc;
        target.Access.RevokedByUserId = command.ActorUserId;
        target.Access.Reason = command.Reason.Trim();
        target.TargetContext.AdvanceRevision(target.TargetContext.AccessRevision);
        target.TargetContext.UpdatedAtUtc = nowUtc;
        LeasePartyAccessCommandSupport.BindUpdated(
            attempt, target.Access, command, "Revoked tenant portal access.");
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            attempt, target.Relationship, command, "Tenant portal access revoked.");
        LeasePartyAccessCommandSupport.StageOutbox(
            attempt, command, nowUtc, target.Access.Id, "tenant-access-revoked");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            command.PartyId,
            null,
            null,
            new[] { target.Access.Id },
            null);
    }

    public Task AuthorizeReplayAsync(
        RevokeTenantUserAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);

    private sealed record RevokeAccessTarget(
        LeaseManagement Relationship,
        TenantUserAccess? Access,
        WorkspaceAccessContext? TargetContext);
}

internal static class LeasePartyAccessCommandSupport
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(
        ILeasePartyAccessCommand command,
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

        return persistence.Query<LeaseManagement>()
            .Where(relationship =>
                relationship.Id == command.LeaseManagementId
                && relationship.PortfolioId == command.PortfolioId
                && relationship.Property != null
                && relationship.Property.PortfolioId == command.PortfolioId
                && relationship.Unit != null
                && relationship.Unit.PortfolioId == command.PortfolioId
                && relationship.Unit.PropertyId == relationship.PropertyId
                && persistence.Query<AuthSession>().Any(session =>
                    session.Id == command.AuthSessionId
                    && session.UserId == command.ActorUserId
                    && session.ActiveAccessContextId == command.AccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > securityNowUtc)
                && persistence.Query<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.AccessContextId
                    && context.UserId == command.ActorUserId
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
                                    scope.PropertyId == relationship.PropertyId
                                    && scope.PortfolioId == command.PortfolioId)))
                        && (assignment.RoleProfile!.Capabilities.Any(capability =>
                                capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage)
                            || assignment.RoleProfile.Capabilities.Any(capability =>
                                capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage)))));
    }

    internal static async Task AuthorizeReplayAsync<TCommand>(
        TCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
        where TCommand : ILeasePartyAccessCommand
    {
        ValidateAuthorizationShape(command);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedRelationships(command, persistence, nowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    internal static void ValidateAuthorizationShape(ILeasePartyAccessCommand command)
    {
        if (command.PortfolioId <= 0 || command.LeaseManagementId <= 0
            || command.ActorUserId <= 0 || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200)
        {
            throw new ArgumentException("Portfolio, relationship, actor, access context, and delivery key are required.");
        }
    }

    internal static bool IsResponsible(LeaseManagementPartyRole role) =>
        role is LeaseManagementPartyRole.PrimaryTenant
            or LeaseManagementPartyRole.CoTenant
            or LeaseManagementPartyRole.Guarantor;

    internal static bool IsMutable(LeaseManagement relationship) =>
        relationship.CanceledAtUtc == null && relationship.AccountClosedAtUtc == null;

    internal static bool HasExactlyOneLegalBasis(int? agreementId, int? addendumId) =>
        (agreementId is > 0) ^ (addendumId is > 0);

    internal static LeaseManagementParty NewParty(
        int portfolioId,
        LeaseManagement relationship,
        int tenantId,
        LeaseManagementPartyRole role,
        DateOnly effectiveFrom,
        bool guarantorLegalNoticeEligible,
        string reason,
        DateTime nowUtc,
        int actorUserId) => new()
        {
            PortfolioId = portfolioId,
            LeaseManagement = relationship,
            TenantId = tenantId,
            Role = role,
            EffectiveFrom = effectiveFrom,
            GuarantorLegalNoticeEligible = guarantorLegalNoticeEligible,
            ChangeReason = reason.Trim(),
            CreatedAtUtc = nowUtc,
            CreatedByUserId = actorUserId,
        };

    internal static TenantUserAccess NewAccess(
        int portfolioId,
        WorkspaceAccessContext accessContext,
        LeaseManagementParty party,
        DateTime nowUtc,
        int actorUserId,
        string reason) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            AccessContext = accessContext,
            ApplicationUserId = accessContext.UserId,
            LeaseManagementParty = party,
            GrantedAtUtc = nowUtc,
            GrantedByUserId = actorUserId,
            Reason = reason.Trim(),
        };

    internal static void Touch(LeaseManagement relationship, DateTime nowUtc)
    {
        relationship.UpdatedAtUtc = nowUtc;
        relationship.RowVersion = Guid.NewGuid();
    }

    internal static void BindCreated(
        IAtomicWriteAttempt attempt,
        object entity,
        ILeasePartyAccessCommand command,
        string reason) =>
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId,
            entity.GetType().Name,
            0,
            AuditLogOperation.Created,
            UserId: command.ActorUserId,
            ChangeReason: reason));

    internal static void BindUpdated(
        IAtomicWriteAttempt attempt,
        object entity,
        ILeasePartyAccessCommand command,
        string reason) =>
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId,
            entity.GetType().Name,
            EntityId(entity),
            AuditLogOperation.Updated,
            UserId: command.ActorUserId,
            ChangeReason: reason));

    internal static void BindRelationshipUpdate(
        IAtomicWriteAttempt attempt,
        LeaseManagement relationship,
        ILeasePartyAccessCommand command,
        string reason) => BindUpdated(attempt, relationship, command, reason);

    internal static void StageAccessTransitionAudits(
        IAtomicWriteAttempt attempt,
        ILeasePartyAccessCommand command,
        AtomicTenantAccessTransitionResult result,
        DateTime occurredAtUtc)
    {
        foreach (var id in result.RevokedAccessIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantUserAccess),
                id,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                ChangeReason: "Revoked tenant portal access during party transition."), occurredAtUtc);
        }
        foreach (var id in result.CreatedAccessIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantUserAccess),
                id,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: "Continued tenant portal access on replacement membership."), occurredAtUtc);
        }
    }

    internal static void StageOutbox(
        IAtomicWriteAttempt attempt,
        ILeasePartyAccessCommand command,
        DateTime nowUtc,
        int entityId,
        string mutation) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(LeaseManagement),
                entityId = command.LeaseManagementId,
                data = new { mutation, entityId },
            }),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        });

    internal static LeasePartyMutationResult Error(
        LeasePartyMutationOutcome outcome,
        ILeasePartyAccessCommand command,
        string error,
        int partyId = 0,
        IReadOnlyList<int>? accessIds = null) => new(
            outcome,
            command.LeaseManagementId,
            partyId,
            null,
            null,
            accessIds ?? Array.Empty<int>(),
            error);

    internal static UnauthorizedAccessException Unauthorized() => new(
        "The relationship is not authorized in the current access context and property scope.");

    private static int EntityId(object entity) => entity switch
    {
        LeaseManagement relationship => relationship.Id,
        LeaseManagementParty party => party.Id,
        TenantUserAccess access => access.Id,
        _ => 0,
    };
}
