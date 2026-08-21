using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Leasing;

public static class LeasePartyAccessWriteSupport
{
    public static string IdempotencyKey(ILeasePartyAccessCommand command, string digest) =>
        command switch
        {
            AddEffectivePartyCommand =>
                $"{command.PortfolioId}:{command.LeaseManagementId}:{digest}",
            EndEffectivePartyCommand value =>
                $"{value.PortfolioId}:{value.LeaseManagementId}:{value.PartyId}:{digest}",
            ChangeEffectivePartyRoleCommand value =>
                $"{value.PortfolioId}:{value.LeaseManagementId}:{value.PartyId}:{digest}",
            GrantTenantUserAccessCommand value =>
                $"{value.PortfolioId}:{value.LeaseManagementId}:{value.PartyId}:{digest}",
            RevokeTenantUserAccessCommand value =>
                $"{value.PortfolioId}:{value.LeaseManagementId}:{value.PartyId}:{value.TenantUserAccessId}:{digest}",
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    public static TransactionalWrite<TCommand, LeasePartyMutationResult> Write<TCommand>(
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<LeasePartyMutationResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, ILeasePartyAccessCommand
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var (operationName, resultContract, protocol) = command switch
        {
            AddEffectivePartyCommand =>
                ("lease-management.party.add", "lease-management.party.add.v1", WriteLockProtocol.LeaseParty),
            EndEffectivePartyCommand =>
                ("lease-management.party.end", "lease-management.party.end.v1", WriteLockProtocol.LeaseParty),
            ChangeEffectivePartyRoleCommand =>
                ("lease-management.party.change-role", "lease-management.party.change-role.v1", WriteLockProtocol.LeaseParty),
            GrantTenantUserAccessCommand =>
                ("lease-management.party.access.grant", "lease-management.party.access.grant.v1", WriteLockProtocol.LeasePartyAccessGrant),
            RevokeTenantUserAccessCommand =>
                ("lease-management.party.access.revoke", "lease-management.party.access.revoke.v1", WriteLockProtocol.LeasePartyAccessRevoke),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return new TransactionalWrite<TCommand, LeasePartyMutationResult>(
            operationName,
            WriteIdempotencyPolicy.Required,
            command,
            resultContract,
            new WriteLockPlan(protocol,
                WriteLock.For("LeaseManagement", command.LeaseManagementId)),
            executeAsync,
            authorizeReplayAsync);
    }

}

public sealed class AddEffectivePartyHandler
{
    public static async Task<LeasePartyMutationResult> ExecuteAsync(
        RentalCommandDbContext _db,
        AddEffectivePartyCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, _db, nowUtc)
            .Select(relationship => new AddPartyTarget(
                relationship,
                _db.Set<Tenant>().Any(tenant =>
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
        _db.Add(party);
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindCreated(context, party, command, "Added effective relationship party.");
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            context, target.Relationship, command, "Relationship party added.");
        await context.FlushBusinessAsync(ct);
        LeasePartyAccessCommandSupport.StageOutbox(context, command, nowUtc, party.Id, "party-added");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            party.Id,
            null,
            null,
            Array.Empty<int>(),
            null);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext _db,
        AddEffectivePartyCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private sealed record AddPartyTarget(
        LeaseManagement Relationship,
        bool TenantExists,
        bool HasOverlappingMembership,
        int PrimaryCount,
        bool LegalBasisValid);
}

public sealed class EndEffectivePartyHandler
{
    public static async Task<LeasePartyMutationResult> ExecuteAsync(
        RentalCommandDbContext _db,
        EndEffectivePartyCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;
        var successorStart = command.EffectiveThrough.AddDays(1);

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, _db, nowUtc)
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
        LeasePartyAccessCommandSupport.BindUpdated(context, target.Party, command, "Ended relationship party membership.");

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
            _db.Add(successorReplacement);
            LeasePartyAccessCommandSupport.BindUpdated(
                context, target.Successor, command, "Ended prior role for primary succession.");
            LeasePartyAccessCommandSupport.BindCreated(
                context, successorReplacement, command, "Promoted successor to primary tenant.");
        }
        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            context, target.Relationship, command, "Relationship party ended.");
        await context.FlushBusinessAsync(ct);
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
        var accessResult = await AtomicLeaseMutationPersistence.TransitionTenantAccessAsync(_db,
            context, command.PortfolioId, command.LeaseManagementId, accessTransitions,
            command.ActorUserId, nowUtc, command.ChangeReason, ct);
        LeasePartyAccessCommandSupport.StageAccessTransitionAudits(context, command, accessResult, nowUtc);
        LeasePartyAccessCommandSupport.StageOutbox(context, command, nowUtc, target.Party.Id, "party-ended");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            null,
            successorReplacement?.Id,
            accessResult.ActiveAccessIds.Concat(accessResult.CreatedAccessIds).ToArray(),
            null);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext _db,
        EndEffectivePartyCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private sealed record EndPartyTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        LeaseManagementParty? Successor,
        int CurrentPrimaryCount,
        bool LegalBasisValid);
}

public sealed class ChangeEffectivePartyRoleHandler
{
    public static async Task<LeasePartyMutationResult> ExecuteAsync(
        RentalCommandDbContext _db,
        ChangeEffectivePartyRoleCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var nowUtc = times.WallClockUtc;
        var currentDate = times.BusinessDate;

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, _db, nowUtc)
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
        _db.Add(replacement);
        LeasePartyAccessCommandSupport.BindUpdated(context, target.Party, command, "Ended prior relationship role.");
        LeasePartyAccessCommandSupport.BindCreated(context, replacement, command, "Started replacement relationship role.");

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
            _db.Add(companionReplacement);
            LeasePartyAccessCommandSupport.BindUpdated(context, target.Companion, command, "Ended companion role for primary swap.");
            LeasePartyAccessCommandSupport.BindCreated(context, companionReplacement, command, "Started companion role for primary swap.");
        }

        LeasePartyAccessCommandSupport.Touch(target.Relationship, nowUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            context, target.Relationship, command, "Relationship party role changed.");
        await context.FlushBusinessAsync(ct);
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
        var accessResult = await AtomicLeaseMutationPersistence.TransitionTenantAccessAsync(_db,
            context, command.PortfolioId, command.LeaseManagementId, accessTransitions,
            command.ActorUserId, nowUtc, command.ChangeReason, ct);
        LeasePartyAccessCommandSupport.StageAccessTransitionAudits(context, command, accessResult, nowUtc);
        LeasePartyAccessCommandSupport.StageOutbox(context, command, nowUtc, replacement.Id, "party-role-changed");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            replacement.Id,
            companionReplacement?.Id,
            accessResult.ActiveAccessIds.Concat(accessResult.CreatedAccessIds).ToArray(),
            null);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext _db,
        ChangeEffectivePartyRoleCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private sealed record ChangeRoleTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        LeaseManagementParty? Companion,
        int CurrentPrimaryCount,
        bool LegalBasisValid);
}

public sealed class GrantTenantUserAccessHandler
{
    public static async Task<LeasePartyMutationResult> ExecuteAsync(
        RentalCommandDbContext _db,
        GrantTenantUserAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var changedAtUtc = times.EffectiveNowUtc;
        var currentDate = times.BusinessDate;
        context.UseDatabaseWallClockForAudit(changedAtUtc);
        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, _db, securityNowUtc)
            .Select(relationship => new GrantAccessTarget(
                relationship,
                relationship.Parties.FirstOrDefault(party => party.Id == command.PartyId
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= currentDate)),
                relationship.Parties.Where(party => party.Id == command.PartyId)
                    .Select(party => party.Tenant!.Email).FirstOrDefault(),
                relationship.Parties.Where(party => party.Id == command.PartyId)
                    .Select(party => (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim())
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct)
            ?? throw LeasePartyAccessCommandSupport.Unauthorized();

        var email = target.Email?.Trim();
        if (target.Party is null || string.IsNullOrWhiteSpace(email)
            || !email.Contains('@', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command,
                "The active household member must have a valid email before login access can be granted.", command.PartyId);
        }

        // Match ASP.NET Identity's UpperInvariantLookupNormalizer without injecting a
        // framework service into the transaction-owned command handler.
        var normalizedEmail = email.Normalize().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command,
                "The active household member must have a valid email before login access can be granted.", command.PartyId);
        }

        // A relationship lock cannot serialize grants for the same person across two different
        // relationships. Use the Identity-normalized email as the global aggregate key before
        // reading or creating the ApplicationUser so the unique Identity row, workspace context,
        // relationship grant, audit, and invitation converge in one atomic context.
        await context.AcquireLockAsync(
            "TenantIdentityEmail",
            TenantIdentityEmailLockKey(normalizedEmail),
            ct);
        var tenantSecurityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);

        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
        var createdIdentity = false;
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                NormalizedUserName = normalizedEmail,
                Email = email,
                NormalizedEmail = normalizedEmail,
                EmailConfirmed = false,
                LockoutEnabled = true,
                DisplayName = string.IsNullOrWhiteSpace(target.DisplayName) ? email : target.DisplayName,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = changedAtUtc,
            };
            _db.Add(user);
            await context.FlushBusinessAsync(ct);
            createdIdentity = true;
        }

        var targetContextId = await _db.Set<WorkspaceAccessContext>()
            .Where(accessContext => accessContext.UserId == user.Id
                && accessContext.PortfolioId == command.PortfolioId)
            .Select(accessContext => (int?)accessContext.Id)
            .SingleOrDefaultAsync(ct);
        WorkspaceAccessContext tenantAccessContext;
        if (targetContextId is > 0)
        {
            await context.AcquireLockAsync(
                "WorkspaceAccessContext", targetContextId.Value, ct);
            tenantAccessContext = await _db.Set<WorkspaceAccessContext>()
                .Include(candidate => candidate.Membership)
                .SingleAsync(candidate => candidate.Id == targetContextId.Value, ct);
            if (tenantAccessContext.Status != WorkspaceAccessContextStatus.Active
                || tenantAccessContext.SuspendedAtUtc is not null
                || tenantAccessContext.RevokedAtUtc is not null)
            {
                return LeasePartyAccessCommandSupport.Error(
                    LeasePartyMutationOutcome.InvalidParty, command,
                    "The household member's existing workspace access is not active.", command.PartyId);
            }
            if (tenantAccessContext.Membership is not null &&
                tenantAccessContext.Membership.DefaultExperience != WorkspaceExperience.Tenant)
            {
                return LeasePartyAccessCommandSupport.Error(
                    LeasePartyMutationOutcome.InvalidParty, command,
                    "This email already belongs to a non-resident Team member in the workspace.", command.PartyId);
            }
        }
        else
        {
            tenantAccessContext = new WorkspaceAccessContext
            {
                User = user,
                UserId = user.Id,
                PortfolioId = command.PortfolioId,
                Status = WorkspaceAccessContextStatus.Active,
                LastAuthorizedExperience = WorkspaceExperience.Tenant,
                CreatedAtUtc = changedAtUtc,
                UpdatedAtUtc = changedAtUtc,
            };
            _db.Add(tenantAccessContext);
        }

        WorkspaceMembership tenantMembership = tenantAccessContext.Membership ?? new WorkspaceMembership
        {
            AccessContext = tenantAccessContext,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Tenant,
            EffectiveFromUtc = tenantSecurityNowUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        if (tenantAccessContext.Membership is null)
        {
            _db.Add(tenantMembership);
            await context.FlushBusinessAsync(ct);
        }
        else if (tenantMembership.Status != WorkspaceMembershipStatus.Active ||
                 tenantMembership.SuspendedAtUtc is not null ||
                 tenantMembership.RevokedAtUtc is not null)
        {
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.InvalidParty, command,
                "The household member's tenant portal membership is not active.", command.PartyId);
        }

        var tenantAssignmentCreated = await EnsureTenantPortalRoleAssignmentAsync(
            _db,
            context,
            tenantMembership,
            command.PortfolioId,
            tenantSecurityNowUtc,
            changedAtUtc,
            ct);
        if (tenantAssignmentCreated)
        {
            await context.FlushBusinessAsync(ct);
        }

        var hasActiveGrant = await _db.Set<TenantUserAccess>()
            .AnyAsync(access => access.PortfolioId == command.PortfolioId
                && access.ApplicationUserId == user.Id
                && access.LeaseManagementPartyId == command.PartyId
                && access.RevokedAtUtc == null, ct);
        if (hasActiveGrant)
        {
            if (tenantAssignmentCreated)
            {
                tenantAccessContext.AdvanceRevision(tenantAccessContext.AccessRevision);
                tenantAccessContext.UpdatedAtUtc = changedAtUtc;
                await context.FlushBusinessAsync(ct);
            }
            return LeasePartyAccessCommandSupport.Error(
                LeasePartyMutationOutcome.AlreadyActive, command, "This user already has active access through the party.", command.PartyId);
        }

        var pendingInvitation = await FindPendingInvitationAsync(
            _db, context, command.PortfolioId, tenantMembership, user.Id, changedAtUtc, ct);
        var access = LeasePartyAccessCommandSupport.NewAccess(
            command.PortfolioId, tenantAccessContext, target.Party,
            changedAtUtc,
            command.ActorUserId,
            command.Reason);
        _db.Add(access);
        if (targetContextId is > 0)
        {
            tenantAccessContext.AdvanceRevision(tenantAccessContext.AccessRevision);
        }
        tenantAccessContext.UpdatedAtUtc = changedAtUtc;
        LeasePartyAccessCommandSupport.Touch(target.Relationship, changedAtUtc);
        LeasePartyAccessCommandSupport.BindCreated(context, access, command, "Granted tenant portal access.");
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            context, target.Relationship, command, "Tenant portal access granted.");
        await context.FlushBusinessAsync(ct);
        if (string.IsNullOrEmpty(user.PasswordHash) && pendingInvitation is null)
        {
            var rawToken = CreateInvitationToken();
            var invitation = new WorkspaceInvitation
            {
                PortfolioId = command.PortfolioId,
                WorkspaceMembershipId = tenantMembership.Id,
                InvitedUserId = user.Id,
                InvitedByUserId = command.ActorUserId,
                TokenHash = CreateWorkspaceMembershipHandler.HashInvitationToken(rawToken),
                CreatedAtUtc = changedAtUtc,
                ExpiresAtUtc = changedAtUtc.AddDays(7),
            };
            _db.Add(invitation);
            context.StageOutbox(BuildPortalInvitation(
                command, tenantMembership, user, access.Id, rawToken, changedAtUtc));
            await context.FlushBusinessAsync(ct);
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(WorkspaceInvitation),
                checked((int)invitation.Id),
                AuditLogOperation.Created,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.LeaseManagementId,
                    command.PartyId,
                    TenantUserAccessId = access.Id,
                    WorkspaceMembershipId = tenantMembership.Id,
                    InvitedUserId = user.Id,
                    invitation.ExpiresAtUtc,
                }),
                ChangeReason: "Tenant portal invitation queued"), changedAtUtc);
        }
        if (createdIdentity)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(ApplicationUser),
                user.Id,
                AuditLogOperation.Created,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.LeaseManagementId,
                    command.PartyId,
                    AccessContextId = tenantAccessContext.Id,
                    RequiresAccountActivation = true,
                }),
                ChangeReason: "Tenant portal account invited"), changedAtUtc);
        }
        LeasePartyAccessCommandSupport.StageOutbox(context, command, changedAtUtc, access.Id, "tenant-access-granted");

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            target.Party.Id,
            null,
            null,
            new[] { access.Id },
            null);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext _db,
        GrantTenantUserAccessCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private static Guid TenantIdentityEmailLockKey(string normalizedEmail) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)).AsSpan(0, 16));

    private static async Task<bool> EnsureTenantPortalRoleAssignmentAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceMembership membership,
        int portfolioId,
        DateTime effectiveFromUtc,
        DateTime changedAtUtc,
        CancellationToken ct)
    {
        var roleProfileId = await db.Set<RoleProfile>()
            .AsNoTracking()
            .Where(role => role.Key == RoleProfileKeys.TenantPortal)
            .Select(role => (int?)role.Id)
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("The canonical Tenant Portal role profile is unavailable.");
        if (membership.Id > 0 && await db.Set<MembershipRoleAssignment>().AnyAsync(assignment =>
                assignment.WorkspaceMembershipId == membership.Id &&
                assignment.PortfolioId == portfolioId &&
                assignment.RoleProfileId == roleProfileId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= effectiveFromUtc &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > effectiveFromUtc),
                ct))
        {
            return false;
        }

        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = roleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = effectiveFromUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        });
        return true;
    }

    private static Task<PendingInvitation?> FindPendingInvitationAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        WorkspaceMembership membership,
        int userId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (membership.Id <= 0)
        {
            return Task.FromResult<PendingInvitation?>(null);
        }

        return db.Set<WorkspaceInvitation>()
            .AsNoTracking()
            .Where(invitation =>
                invitation.PortfolioId == portfolioId &&
                invitation.WorkspaceMembershipId == membership.Id &&
                invitation.InvitedUserId == userId &&
                invitation.AcceptedAtUtc == null &&
                invitation.RevokedAtUtc == null &&
                invitation.ExpiresAtUtc > nowUtc)
            .OrderByDescending(invitation => invitation.CreatedAtUtc)
            .Select(invitation => new PendingInvitation(invitation.Id))
            .FirstOrDefaultAsync(ct);
    }

    private static string CreateInvitationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static OutboxMessage BuildPortalInvitation(
        GrantTenantUserAccessCommand command,
        WorkspaceMembership membership,
        ApplicationUser user,
        int tenantUserAccessId,
        string rawToken,
        DateTime nowUtc)
    {
        var tokenHash = CreateWorkspaceMembershipHandler.HashInvitationToken(rawToken);
        var setupUrl = $"{command.WebBaseUrl.TrimEnd('/')}/activate-team?token={Uri.EscapeDataString(rawToken)}";
        var greeting = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email! : user.DisplayName;
        var subject = "Activate your Rental Command resident portal";
        var body = $"""
            Hi {greeting},

            You have been invited to access your rental in Rental Command. Set your password to activate your resident portal:

            {setupUrl}

            This secure link expires in 7 days. If you were not expecting this invitation, you can ignore this email.
            """;
        var htmlBody = $"""
            <p>Hi {WebUtility.HtmlEncode(greeting)},</p>
            <p>You have been invited to access your rental in Rental Command.</p>
            <p><a href="{WebUtility.HtmlEncode(setupUrl)}">Set your password and activate your resident portal</a>.</p>
            <p>This secure link expires in 7 days. If you were not expecting this invitation, you can ignore this email.</p>
            """;
        return new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new { to = user.Email, subject, body, htmlBody }),
            IdempotencyKey = $"tenant-portal-invitation:{membership.PortfolioId}:{tenantUserAccessId}:{tokenHash[..16]}",
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        };
    }

    private sealed record PendingInvitation(long Id);

    private sealed record GrantAccessTarget(
        LeaseManagement Relationship,
        LeaseManagementParty? Party,
        string? Email,
        string? DisplayName);
}

public sealed class RevokeTenantUserAccessHandler
{
    public static async Task<LeasePartyMutationResult> ExecuteAsync(
        RentalCommandDbContext _db,
        RevokeTenantUserAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        LeasePartyAccessCommandSupport.ValidateAuthorizationShape(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var changedAtUtc = times.EffectiveNowUtc;
        context.UseDatabaseWallClockForAudit(changedAtUtc);
        var targetContextId = await _db.Set<TenantUserAccess>()
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
        await context.AcquireLockAsync(
            "WorkspaceAccessContext", targetContextId, ct);

        var target = await LeasePartyAccessCommandSupport.AuthorizedRelationships(command, _db, securityNowUtc)
            .Select(relationship => new RevokeAccessTarget(
                relationship,
                _db.Set<TenantUserAccess>().FirstOrDefault(access =>
                    access.Id == command.TenantUserAccessId
                    && access.PortfolioId == command.PortfolioId
                    && access.LeaseManagementPartyId == command.PartyId
                    && access.LeaseManagementParty!.LeaseManagementId == relationship.Id),
                _db.Set<WorkspaceAccessContext>().FirstOrDefault(context =>
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

        target.Access.RevokedAtUtc = changedAtUtc;
        target.Access.RevokedByUserId = command.ActorUserId;
        target.Access.Reason = command.Reason.Trim();
        var pendingInvitations = await _db.Set<WorkspaceInvitation>()
            .Where(invitation =>
                invitation.PortfolioId == command.PortfolioId &&
                invitation.InvitedUserId == target.Access.ApplicationUserId &&
                invitation.WorkspaceMembership!.AccessContextId == target.Access.AccessContextId &&
                invitation.AcceptedAtUtc == null &&
                invitation.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var invitation in pendingInvitations)
        {
            invitation.RevokedAtUtc = changedAtUtc;
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(WorkspaceInvitation),
                checked((int)invitation.Id),
                AuditLogOperation.Updated,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.LeaseManagementId,
                    command.PartyId,
                    TenantUserAccessId = target.Access.Id,
                    invitation.WorkspaceMembershipId,
                    invitation.InvitedUserId,
                    invitation.RevokedAtUtc,
                }),
                ChangeReason: "Tenant portal invitation revoked"), changedAtUtc);
        }
        target.TargetContext.AdvanceRevision(target.TargetContext.AccessRevision);
        target.TargetContext.UpdatedAtUtc = changedAtUtc;
        LeasePartyAccessCommandSupport.BindUpdated(
            context, target.Access, command, "Revoked tenant portal access.");
        LeasePartyAccessCommandSupport.Touch(target.Relationship, changedAtUtc);
        LeasePartyAccessCommandSupport.BindRelationshipUpdate(
            context, target.Relationship, command, "Tenant portal access revoked.");
        LeasePartyAccessCommandSupport.StageOutbox(
            context, command, changedAtUtc, target.Access.Id, "tenant-access-revoked");
        await context.FlushBusinessAsync(ct);

        return new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied,
            command.LeaseManagementId,
            command.PartyId,
            null,
            null,
            new[] { target.Access.Id },
            null);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext _db,
        RevokeTenantUserAccessCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeasePartyAccessCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private sealed record RevokeAccessTarget(
        LeaseManagement Relationship,
        TenantUserAccess? Access,
        WorkspaceAccessContext? TargetContext);
}

internal static class LeasePartyAccessCommandSupport
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(
        ILeasePartyAccessCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var scope = new WorkspaceReadScope(
            command.PortfolioId,
            command.ActorUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision);
        var assignments = db.AuthorizedAssignmentsForScope(
            scope,
            [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage],
            CapabilityAuthorizationTargetKind.Property,
            securityNowUtc);

        return db.Set<LeaseManagement>()
            .Where(relationship =>
                relationship.Id == command.LeaseManagementId
                && relationship.PortfolioId == command.PortfolioId
                && relationship.Property != null
                && relationship.Property.PortfolioId == command.PortfolioId
                && relationship.Unit != null
                && relationship.Unit.PortfolioId == command.PortfolioId
                && relationship.Unit.PropertyId == relationship.PropertyId
                && assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PropertyId == relationship.PropertyId
                        && selected.PortfolioId == command.PortfolioId)));
    }

    internal static async Task AuthorizeReplayAsync<TCommand>(
        TCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
        where TCommand : ILeasePartyAccessCommand
    {
        ValidateAuthorizationShape(command);
        var nowUtc = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedRelationships(command, db, nowUtc).AnyAsync(ct))
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
        IAtomicCommandContext context,
        object entity,
        ILeasePartyAccessCommand command,
        string reason) =>
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId,
            entity.GetType().Name,
            0,
            AuditLogOperation.Created,
            UserId: command.ActorUserId,
            ChangeReason: reason));

    internal static void BindUpdated(
        IAtomicCommandContext context,
        object entity,
        ILeasePartyAccessCommand command,
        string reason) =>
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId,
            entity.GetType().Name,
            EntityId(entity),
            AuditLogOperation.Updated,
            UserId: command.ActorUserId,
            ChangeReason: reason));

    internal static void BindRelationshipUpdate(
        IAtomicCommandContext context,
        LeaseManagement relationship,
        ILeasePartyAccessCommand command,
        string reason) => BindUpdated(context, relationship, command, reason);

    internal static void StageAccessTransitionAudits(
        IAtomicCommandContext context,
        ILeasePartyAccessCommand command,
        AtomicTenantAccessTransitionResult result,
        DateTime occurredAtUtc)
    {
        foreach (var id in result.RevokedAccessIds)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantUserAccess),
                id,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                ChangeReason: "Revoked tenant portal access during party transition."), occurredAtUtc);
        }
        foreach (var id in result.CreatedAccessIds)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantUserAccess),
                id,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: "Continued tenant portal access on replacement membership."), occurredAtUtc);
        }
    }

    internal static void StageOutbox(
        IAtomicCommandContext context,
        ILeasePartyAccessCommand command,
        DateTime nowUtc,
        int entityId,
        string mutation) =>
        context.StageOutbox(new OutboxMessage
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
