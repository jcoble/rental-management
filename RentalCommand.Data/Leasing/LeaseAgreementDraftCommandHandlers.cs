using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class EditLeaseAgreementDraftHandler
    : IAtomicCommandHandler<EditLeaseAgreementDraftCommand, LeaseAgreementDraftMutationResult>,
      IAtomicReplayAuthorizer<EditLeaseAgreementDraftCommand>
{
    public async Task<LeaseAgreementDraftMutationResult> HandleAsync(
        EditLeaseAgreementDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        LeaseAgreementDraftCommandSupport.ValidateEditShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var agreement = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                command, attempt.Persistence, nowUtc)
            .SelectMany(relationship => relationship.Agreements)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == command.LeaseAgreementId
                && candidate.PortfolioId == command.PortfolioId,
                ct)
            ?? throw LeaseAgreementDraftCommandSupport.Unauthorized();

        if (agreement.IssuedAtUtc != null || agreement.IssuedArtifactId != null
            || agreement.FullyExecutedAtUtc != null || agreement.ExecutedArtifactId != null
            || agreement.VoidedAtUtc != null || agreement.DraftCanceledAtUtc != null)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.DraftNotEditable,
                command,
                command.LeaseAgreementId,
                agreement.VersionNumber,
                agreement.DraftRevision,
                "Only an unissued, uncanceled Agreement draft can be edited.");
        }
        if (agreement.DraftRevision != command.ExpectedDraftRevision)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.StaleDraftRevision,
                command,
                command.LeaseAgreementId,
                agreement.VersionNumber,
                agreement.DraftRevision,
                "DraftRevision is stale; reload the Agreement draft before editing.");
        }

        var signerInputs = LeaseAgreementDraftCommandSupport.ToAtomicSignerInputs(command.Signers);
        var draftReferencesValid = await LeaseAgreementDraftCommandSupport.ValidateDraftReferencesAsync(
                command, attempt.Persistence, ct)
            && await attempt.Leasing.ValidateAgreementDraftSignerScopeAsync(
                command.PortfolioId, command.LeaseManagementId, signerInputs, ct);
        if (!draftReferencesValid)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidSigners,
                command,
                command.LeaseAgreementId,
                agreement.VersionNumber,
                agreement.DraftRevision,
                "The template and every signer provenance reference must belong to this lease relationship and portfolio.");
        }

        var sourceVersion = await attempt.Leasing.ResolveAuthoredDocumentSourceVersionAsync(
            command.PortfolioId, 0, command.LeaseManagementId, command.DocumentTemplateId,
            command.ActorUserId, nowUtc, ct);
        if (!sourceVersion.Resolved)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidSigners, command, command.LeaseAgreementId,
                agreement.VersionNumber, agreement.DraftRevision,
                "The selected lease template could not be frozen as immutable source provenance.");
        }

        agreement.AgreementNumber = command.AgreementNumber.Trim();
        agreement.TermType = command.TermType;
        agreement.TermStartOn = command.TermStartOn;
        agreement.TermEndOn = command.TermEndOn;
        agreement.GoverningFromOn = command.GoverningFromOn;
        agreement.BaseRentAmount = command.BaseRentAmount;
        agreement.RentDueDay = command.RentDueDay;
        agreement.SecurityDepositObligation = command.SecurityDepositObligation;
        agreement.LateFeeAmount = command.LateFeeAmount;
        agreement.GracePeriodDays = command.GracePeriodDays;
        agreement.TermsSchemaVersion = command.TermsSchemaVersion;
        agreement.TermsPayload = command.TermsPayload;
        agreement.DocumentSourceVersionId = sourceVersion.DocumentSourceVersionId;
        agreement.UpdatedAtUtc = nowUtc;
        agreement.DraftRevision++;

        attempt.BindSemanticAudit(agreement, LeaseAgreementDraftCommandSupport.Updated(
            command, agreement.Id, "Edited Agreement draft terms and signer snapshot."));
        await attempt.FlushBusinessAsync(ct);

        var signerMutation = await attempt.Leasing.ReplaceAgreementDraftSignersAsync(
            command.PortfolioId,
            command.LeaseManagementId,
            agreement.Id,
            agreement.DraftRevision,
            signerInputs,
            ct);
        foreach (var signerId in signerMutation.DeletedSignerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAgreementSigner), signerId,
                AuditLogOperation.Deleted, UserId: command.ActorUserId,
                ChangeReason: "Replaced signer snapshot while editing Agreement draft."), nowUtc);
        }
        foreach (var signerId in signerMutation.CreatedSignerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAgreementSigner), signerId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Created replacement signer snapshot for Agreement draft."), nowUtc);
        }
        LeaseAgreementDraftCommandSupport.StageOutbox(
            attempt, command, nowUtc, agreement.Id, "agreement-draft-edited");

        return new(
            LeaseAgreementDraftMutationOutcome.Applied,
            command.LeaseManagementId,
            agreement.Id,
            agreement.VersionNumber,
            agreement.DraftRevision,
            null,
            signerMutation.CreatedSignerIds,
            Array.Empty<int>(),
            Array.Empty<int>(),
            null);
    }

    public Task AuthorizeReplayAsync(
        EditLeaseAgreementDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class CreateLeaseAgreementSuccessorDraftHandler
    : IAtomicCommandHandler<CreateLeaseAgreementSuccessorDraftCommand, LeaseAgreementDraftMutationResult>,
      IAtomicReplayAuthorizer<CreateLeaseAgreementSuccessorDraftCommand>
{
    public async Task<LeaseAgreementDraftMutationResult> HandleAsync(
        CreateLeaseAgreementSuccessorDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        LeaseAgreementDraftCommandSupport.ValidateSuccessorShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var source = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                command, attempt.Persistence, times.WallClockUtc)
            .SelectMany(relationship => relationship.Agreements)
            .Where(candidate =>
                candidate.Id == command.SourceAgreementId
                && candidate.PortfolioId == command.PortfolioId)
            .Select(candidate => new
            {
                Agreement = candidate,
                SourceKind = candidate.DocumentSourceVersion!.SourceKind,
                candidate.LeaseManagement!.PropertyId,
            })
            .SingleOrDefaultAsync(ct)
            ?? throw LeaseAgreementDraftCommandSupport.Unauthorized();

        var sourceAgreement = source.Agreement;

        var sourceIsCurrent = await attempt.Persistence.Query<LeaseAgreementStatusProjection>()
            .AnyAsync(status =>
                status.PortfolioId == command.PortfolioId
                && status.LeaseManagementId == command.LeaseManagementId
                && status.AgreementId == command.SourceAgreementId
                && status.IsGoverning,
                ct);
        if (!sourceIsCurrent || sourceAgreement.FullyExecutedAtUtc == null
            || sourceAgreement.ExecutedArtifactId == null || sourceAgreement.VoidedAtUtc != null
            || sourceAgreement.DraftCanceledAtUtc != null)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotCurrent,
                command,
                0,
                0,
                0,
                "The source must be the currently governing, fully executed, nonvoid Agreement.");
        }

        var existingSuccessor = await attempt.Persistence.Query<LeaseAgreement>()
            .AnyAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == command.LeaseManagementId
                && (candidate.ReplacesAgreementId == sourceAgreement.Id || candidate.RenewsAgreementId == sourceAgreement.Id)
                && candidate.DraftCanceledAtUtc == null
                && (candidate.VoidedAtUtc == null || candidate.FullyExecutedAtUtc != null),
                ct);
        if (existingSuccessor)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotCurrent,
                command,
                0,
                0,
                0,
                "The source already has a durable correction, restatement, renewal, or month-to-month successor.");
        }

        var isReplacement = command.ChangeType is LeaseAgreementChangeType.Correction
            or LeaseAgreementChangeType.Restatement;
        if (isReplacement && (command.TermStartOn != sourceAgreement.TermStartOn
                || command.TermEndOn != sourceAgreement.TermEndOn
                || command.GoverningFromOn <= sourceAgreement.GoverningFromOn))
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidTerms,
                command,
                0,
                0,
                0,
                "Correction and restatement drafts must copy the source term dates and begin governing after the source began governing; edit only the new draft afterward.");
        }
        var isRenewal = command.ChangeType is LeaseAgreementChangeType.Renewal
            or LeaseAgreementChangeType.MonthToMonth;
        if (isRenewal && (command.GoverningFromOn != command.TermStartOn
                || (sourceAgreement.TermEndOn.HasValue && command.GoverningFromOn <= sourceAgreement.TermEndOn.Value)
                || (!sourceAgreement.TermEndOn.HasValue && command.GoverningFromOn <= times.BusinessDate)))
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidTerms,
                command,
                0,
                0,
                0,
                "Renewal and month-to-month drafts must start governing on their term start after the source Agreement's current governing range.");
        }

        var nextVersion = await attempt.Persistence.Query<LeaseAgreement>()
            .Where(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == command.LeaseManagementId)
            .MaxAsync(candidate => candidate.VersionNumber, ct) + 1;

        if (!await LeaseAgreementDraftCommandSupport.ValidateRenewalDecisionsAsync(
                command, attempt.Persistence, times.BusinessDate, ct))
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidAddendumDecisions,
                command,
                0,
                nextVersion,
                0,
                "Renewal and month-to-month drafts require one valid decision for every currently effective Addendum series.");
        }

        var sourceVersionId = sourceAgreement.DocumentSourceVersionId;
        if (source.SourceKind == LegalDocumentSourceKind.ImportedExternalDocument)
        {
            if (command.DocumentTemplateId is not > 0)
            {
                return LeaseAgreementDraftCommandSupport.Error(
                    LeaseAgreementDraftMutationOutcome.InvalidTemplate,
                    command,
                    0,
                    nextVersion,
                    0,
                    "An active lease template is required to turn an imported Agreement into an editable successor draft.");
            }

            var authoredSource = await attempt.Leasing.ResolveAuthoredDocumentSourceVersionAsync(
                command.PortfolioId,
                source.PropertyId,
                command.LeaseManagementId,
                command.DocumentTemplateId.Value,
                command.ActorUserId,
                times.WallClockUtc,
                ct);
            if (!authoredSource.Resolved)
            {
                return LeaseAgreementDraftCommandSupport.Error(
                    LeaseAgreementDraftMutationOutcome.InvalidTemplate,
                    command,
                    0,
                    nextVersion,
                    0,
                    "The selected lease template is not active for this property.");
            }
            sourceVersionId = authoredSource.DocumentSourceVersionId;
        }
        else if (command.DocumentTemplateId.HasValue)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.InvalidTemplate,
                command,
                0,
                nextVersion,
                0,
                "A template selection is only accepted when the governing Agreement was imported.");
        }

        var successor = new LeaseAgreement
        {
            PortfolioId = command.PortfolioId,
            LeaseManagementId = command.LeaseManagementId,
            VersionNumber = nextVersion,
            AgreementNumber = $"AGR-{command.LeaseManagementId:D8}-V{nextVersion}",
            ChangeType = command.ChangeType,
            CorrectionReason = command.ChangeType == LeaseAgreementChangeType.Correction
                ? command.CorrectionReason!.Trim()
                : null,
            ReplacesAgreementId = isReplacement ? sourceAgreement.Id : null,
            RenewsAgreementId = isReplacement ? null : sourceAgreement.Id,
            TermType = command.ChangeType switch
            {
                LeaseAgreementChangeType.MonthToMonth => LeaseAgreementTermType.MonthToMonth,
                LeaseAgreementChangeType.Renewal => LeaseAgreementTermType.FixedTerm,
                _ => sourceAgreement.TermType,
            },
            TermStartOn = command.TermStartOn,
            TermEndOn = command.ChangeType == LeaseAgreementChangeType.MonthToMonth
                ? null
                : command.TermEndOn,
            GoverningFromOn = command.GoverningFromOn,
            BaseRentAmount = sourceAgreement.BaseRentAmount,
            RentDueDay = sourceAgreement.RentDueDay,
            SecurityDepositObligation = sourceAgreement.SecurityDepositObligation,
            LateFeeAmount = sourceAgreement.LateFeeAmount,
            GracePeriodDays = sourceAgreement.GracePeriodDays,
            Currency = sourceAgreement.Currency,
            TermsSchemaVersion = sourceAgreement.TermsSchemaVersion,
            TermsPayload = sourceAgreement.TermsPayload,
            DocumentSourceVersionId = sourceVersionId,
            CreatedAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
            UpdatedAtUtc = times.WallClockUtc,
            DraftRevision = 1,
        };
        attempt.Persistence.Add(successor);
        attempt.BindSemanticAudit(successor, LeaseAgreementDraftCommandSupport.Created(
            command, "Created a successor Agreement draft without changing the governing Agreement."));
        await attempt.FlushBusinessAsync(ct);
        var signerIds = await attempt.Leasing.CopyAgreementDraftSignersAsync(
            command.PortfolioId,
            command.LeaseManagementId,
            sourceAgreement.Id,
            successor.Id,
            ct);
        foreach (var signerId in signerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAgreementSigner), signerId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Copied signer snapshot into successor Agreement draft."),
                times.WallClockUtc);
        }
        var addendumResult = isRenewal
            ? await attempt.Leasing.CreateRenewalAddendumDraftsAsync(
                command.PortfolioId,
                command.LeaseManagementId,
                sourceAgreement.Id,
                successor.Id,
                successor.GoverningFromOn,
                command.AddendumDecisions.Select(decision => new AtomicRenewalAddendumDecisionInput(
                    decision.SourceAddendumSeriesPublicId, (int)decision.Decision)).ToArray(),
                command.ActorUserId,
                times.WallClockUtc,
                ct)
            : new AtomicRenewalAddendumDraftResult(true, [], [], [], []);
        if (!addendumResult.InputValid)
        {
            throw new InvalidOperationException(
                "Effective Addendum state changed after validation while the lease relationship lock was held.");
        }
        foreach (var decisionId in addendumResult.DecisionIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseRenewalAddendumDecision), decisionId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Recorded explicit effective Addendum disposition for successor Agreement."),
                times.WallClockUtc);
        }
        foreach (var addendumId in addendumResult.ReplacementAddendumIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAddendum), addendumId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Created editable successor-bound replacement Addendum draft."),
                times.WallClockUtc);
        }
        foreach (var signerId in addendumResult.ReplacementSignerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAddendumSigner), signerId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Copied signer snapshot into replacement Addendum draft."),
                times.WallClockUtc);
        }
        foreach (var effectId in addendumResult.ReplacementFinancialEffectIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAddendumFinancialEffect), effectId,
                AuditLogOperation.Created, UserId: command.ActorUserId,
                ChangeReason: "Copied financial effect into replacement Addendum draft."),
                times.WallClockUtc);
        }
        LeaseAgreementDraftCommandSupport.StageOutbox(
            attempt, command, times.WallClockUtc, successor.Id,
            "agreement-successor-draft-created", addendumResult.ReplacementAddendumIds);

        return new(
            LeaseAgreementDraftMutationOutcome.Applied,
            command.LeaseManagementId,
            successor.Id,
            successor.VersionNumber,
            successor.DraftRevision,
            sourceAgreement.Id,
            signerIds,
            addendumResult.DecisionIds,
            addendumResult.ReplacementAddendumIds,
            null);
    }

    public Task AuthorizeReplayAsync(
        CreateLeaseAgreementSuccessorDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ReplaceIssuedAgreementWithDraftHandler
    : IAtomicCommandHandler<ReplaceIssuedAgreementWithDraftCommand, LeaseAgreementDraftMutationResult>,
      IAtomicReplayAuthorizer<ReplaceIssuedAgreementWithDraftCommand>
{
    public async Task<LeaseAgreementDraftMutationResult> HandleAsync(
        ReplaceIssuedAgreementWithDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        LeaseAgreementDraftCommandSupport.ValidateIssuedReplacementShape(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);

        var nowUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                command, attempt.Persistence, nowUtc)
            .SelectMany(relationship => relationship.Agreements)
            .Select(agreement => new
            {
                Agreement = agreement,
                ActivePossessionDependsOnAgreement = agreement.LeaseManagement!.PossessionGivenAtUtc != null
                    && agreement.LeaseManagement.PossessionReturnedAtUtc == null
                    && attempt.Persistence.Query<LeaseAgreementStatusProjection>().Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.AgreementId == agreement.Id
                        && status.LeaseManagementId == command.LeaseManagementId
                        && status.IsGoverning),
                ActiveAddendaDependOnAgreement = agreement.Addenda.Any(addendum =>
                    addendum.FullyExecutedAtUtc != null && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null),
                HasUnreversedPostedMoney = attempt.Persistence.Query<TenantLedgerEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAgreementId == agreement.Id
                        && entry.ReversesEntryId == null && !entry.ReversalEntries.Any())
                    || attempt.Persistence.Query<SecurityDepositEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAgreementId == agreement.Id
                        && entry.ReversesEntryId == null && !entry.ReversalEntries.Any()),
            })
            .SingleOrDefaultAsync(candidate =>
                candidate.Agreement.Id == command.SourceAgreementId
                && candidate.Agreement.PortfolioId == command.PortfolioId, ct)
            ?? throw LeaseAgreementDraftCommandSupport.Unauthorized();
        var source = target.Agreement;

        if (source.IssuedAtUtc == null || source.IssuedArtifactId == null
            || source.FullyExecutedAtUtc != null || source.ExecutedArtifactId != null
            || source.DraftCanceledAtUtc != null)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable,
                command,
                0,
                0,
                0,
                "The source must be an issued, never-executed Agreement with preserved issuance evidence.");
        }

        if (source.VoidedAtUtc == null && target.ActivePossessionDependsOnAgreement)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable,
                command, 0, 0, 0,
                "Return possession or execute a governing replacement before voiding this Agreement.");
        }
        if (source.VoidedAtUtc == null && target.ActiveAddendaDependOnAgreement)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable,
                command, 0, 0, 0,
                "Every executed Addendum attached to this Agreement must be voided or superseded first.");
        }
        if (source.VoidedAtUtc == null && target.HasUnreversedPostedMoney)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable,
                command, 0, 0, 0,
                "Reverse every unresolved ledger/deposit effect before voiding this Agreement.");
        }

        var existingSuccessor = await attempt.Persistence.Query<LeaseAgreement>()
            .AnyAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == command.LeaseManagementId
                && candidate.ReissuesAgreementId == source.Id
                && candidate.DraftCanceledAtUtc == null
                && (candidate.VoidedAtUtc == null || candidate.FullyExecutedAtUtc != null),
                ct);
        if (existingSuccessor)
        {
            return LeaseAgreementDraftCommandSupport.Error(
                LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable,
                command,
                0,
                0,
                0,
                "The issued Agreement already has a successor version.");
        }

        if (source.VoidedAtUtc == null)
        {
            source.VoidedAtUtc = nowUtc;
            source.VoidReasonCode = "ISSUED_AGREEMENT_REPLACED";
            source.VoidNote = command.VoidNote?.Trim();
            source.UpdatedAtUtc = nowUtc;
            attempt.BindSemanticAudit(source, LeaseAgreementDraftCommandSupport.Updated(
                command, source.Id, "Voided issued Agreement while atomically creating its replacement draft."));
            await LegalArtifactCommandSupport.VoidOpenPacketAsync(
                command.PortfolioId, source.Id, null, command.ActorUserId, nowUtc, attempt, ct);
        }

        var nextVersion = await attempt.Persistence.Query<LeaseAgreement>()
            .Where(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.LeaseManagementId == command.LeaseManagementId)
            .MaxAsync(candidate => candidate.VersionNumber, ct) + 1;

        var replacement = new LeaseAgreement
        {
            PortfolioId = command.PortfolioId,
            LeaseManagementId = command.LeaseManagementId,
            VersionNumber = nextVersion,
            AgreementNumber = $"AGR-{command.LeaseManagementId:D8}-V{nextVersion}",
            ChangeType = source.ChangeType,
            CorrectionReason = source.CorrectionReason,
            TransferredFromAgreementId = source.TransferredFromAgreementId,
            ReplacesAgreementId = source.ReplacesAgreementId,
            RenewsAgreementId = source.RenewsAgreementId,
            ReissuesAgreementId = source.Id,
            ReissueReason = command.ReissueReason.Trim(),
            TermType = source.TermType,
            TermStartOn = source.TermStartOn,
            TermEndOn = source.TermEndOn,
            GoverningFromOn = source.GoverningFromOn,
            BaseRentAmount = source.BaseRentAmount,
            RentDueDay = source.RentDueDay,
            SecurityDepositObligation = source.SecurityDepositObligation,
            LateFeeAmount = source.LateFeeAmount,
            GracePeriodDays = source.GracePeriodDays,
            Currency = source.Currency,
            TermsSchemaVersion = source.TermsSchemaVersion,
            TermsPayload = source.TermsPayload,
            DocumentSourceVersionId = source.DocumentSourceVersionId,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = command.ActorUserId,
            UpdatedAtUtc = nowUtc,
            DraftRevision = 1,
        };
        attempt.Persistence.Add(replacement);
        attempt.BindSemanticAudit(replacement, LeaseAgreementDraftCommandSupport.Created(
            command,
            $"Created reissue draft from voided issued Agreement {source.Id}; preserved source artifact {source.IssuedArtifactId} as immutable history."));
        await attempt.FlushBusinessAsync(ct);

        var signerIds = await attempt.Leasing.CopyIssuedAgreementReplacementDraftSignersAsync(
            command.PortfolioId,
            command.LeaseManagementId,
            source.Id,
            replacement.Id,
            ct);
        foreach (var signerId in signerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LeaseAgreementSigner),
                signerId,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: "Copied the immutable issued signer snapshot into a recovery Agreement draft."),
                nowUtc);
        }
        LeaseAgreementDraftCommandSupport.StageOutbox(
            attempt,
            command,
            nowUtc,
            replacement.Id,
            "issued-agreement-replaced-with-draft");

        return new(
            LeaseAgreementDraftMutationOutcome.Applied,
            command.LeaseManagementId,
            replacement.Id,
            replacement.VersionNumber,
            replacement.DraftRevision,
            source.Id,
            signerIds,
            Array.Empty<int>(),
            Array.Empty<int>(),
            null);
    }

    public Task AuthorizeReplayAsync(
        ReplaceIssuedAgreementWithDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

internal static class LeaseAgreementDraftCommandSupport
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(
        ILeaseAgreementDraftCommand command,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));
        return persistence.Query<LeaseManagement>().Where(relationship =>
            relationship.Id == command.LeaseManagementId
            && relationship.PortfolioId == command.PortfolioId
            && relationship.Property != null && relationship.Property.PortfolioId == command.PortfolioId
            && relationship.Unit != null && relationship.Unit.PortfolioId == command.PortfolioId
            && relationship.Unit.PropertyId == relationship.PropertyId
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope =>
                                scope.PropertyId == relationship.PropertyId
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

    internal static async Task AuthorizeReplayAsync<TCommand>(
        TCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
        where TCommand : ILeaseAgreementDraftCommand
    {
        ValidateAuthorizationShape(command);
        var nowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedRelationships(command, persistence, nowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    internal static async Task<bool> ValidateDraftReferencesAsync(
        EditLeaseAgreementDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var partyIds = command.Signers.Where(s => s.LeaseManagementPartyId.HasValue)
            .Select(s => s.LeaseManagementPartyId!.Value).Distinct().ToArray();
        var tenantIds = command.Signers.Where(s => s.TenantId.HasValue)
            .Select(s => s.TenantId!.Value).Distinct().ToArray();
        var facts = await persistence.Query<LeaseManagement>()
            .Where(relationship => relationship.Id == command.LeaseManagementId
                && relationship.PortfolioId == command.PortfolioId)
            .Select(relationship => new
            {
                PartyCount = relationship.Parties.Count(party => partyIds.Contains(party.Id)),
                TenantCount = persistence.Query<Tenant>().Count(tenant =>
                    tenant.PortfolioId == command.PortfolioId && tenantIds.Contains(tenant.Id)),
                TemplateValid = persistence.Query<DocumentTemplate>().Any(template =>
                    template.Id == command.DocumentTemplateId
                    && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.ArchivedAtUtc == null
                    && (template.PropertyId == null || template.PropertyId == relationship.PropertyId)),
            })
            .SingleAsync(ct);
        return facts.PartyCount == partyIds.Length && facts.TenantCount == tenantIds.Length
            && facts.TemplateValid;
    }

    internal static async Task<bool> ValidateRenewalDecisionsAsync(
        CreateLeaseAgreementSuccessorDraftCommand command,
        IAtomicPersistenceSession persistence,
        DateOnly businessDate,
        CancellationToken ct)
    {
        var requiresDecisions = command.ChangeType is LeaseAgreementChangeType.Renewal
            or LeaseAgreementChangeType.MonthToMonth;
        if (!requiresDecisions)
        {
            return command.AddendumDecisions.Count == 0;
        }
        if (command.AddendumDecisions.Any(decision =>
                !Enum.IsDefined(decision.Decision)
                || decision.SourceAddendumSeriesPublicId == Guid.Empty
                )
            || command.AddendumDecisions.Select(d => d.SourceAddendumSeriesPublicId).Distinct().Count()
                != command.AddendumDecisions.Count)
        {
            return false;
        }
        var series = command.AddendumDecisions.Select(d => d.SourceAddendumSeriesPublicId).ToArray();
        var facts = await persistence.Query<LeaseManagement>()
            .Where(relationship => relationship.Id == command.LeaseManagementId
                && relationship.PortfolioId == command.PortfolioId)
            .Select(relationship => new
            {
                EffectiveCount = relationship.Addenda.Count(addendum =>
                    addendum.FullyExecutedAtUtc != null
                    && addendum.ExecutedArtifactId != null
                    && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null
                    && addendum.BaseAgreement != null
                    && addendum.BaseAgreement.FullyExecutedAtUtc != null
                    && addendum.BaseAgreement.ExecutedArtifactId != null
                    && addendum.BaseAgreement.VoidedAtUtc == null
                    && addendum.BaseAgreement.DraftCanceledAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber
                        && newer.FullyExecutedAtUtc != null
                        && newer.ExecutedArtifactId != null
                        && newer.VoidedAtUtc == null
                        && newer.DraftCanceledAtUtc == null
                        && newer.EffectiveFromOn <= businessDate
                        && (newer.EffectiveThroughOn == null
                            || newer.EffectiveThroughOn >= businessDate)
                        && (newer.SupersededEffectiveOn == null
                            || newer.SupersededEffectiveOn > businessDate)
                        && newer.BaseAgreement != null
                        && newer.BaseAgreement.FullyExecutedAtUtc != null
                        && newer.BaseAgreement.ExecutedArtifactId != null
                        && newer.BaseAgreement.VoidedAtUtc == null
                        && newer.BaseAgreement.DraftCanceledAtUtc == null)),
                MatchedCount = relationship.Addenda.Count(addendum =>
                    series.Contains(addendum.SeriesPublicId)
                    && addendum.FullyExecutedAtUtc != null
                    && addendum.ExecutedArtifactId != null
                    && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null
                    && addendum.BaseAgreement != null
                    && addendum.BaseAgreement.FullyExecutedAtUtc != null
                    && addendum.BaseAgreement.ExecutedArtifactId != null
                    && addendum.BaseAgreement.VoidedAtUtc == null
                    && addendum.BaseAgreement.DraftCanceledAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber
                        && newer.FullyExecutedAtUtc != null
                        && newer.ExecutedArtifactId != null
                        && newer.VoidedAtUtc == null
                        && newer.DraftCanceledAtUtc == null
                        && newer.EffectiveFromOn <= businessDate
                        && (newer.EffectiveThroughOn == null
                            || newer.EffectiveThroughOn >= businessDate)
                        && (newer.SupersededEffectiveOn == null
                            || newer.SupersededEffectiveOn > businessDate)
                        && newer.BaseAgreement != null
                        && newer.BaseAgreement.FullyExecutedAtUtc != null
                        && newer.BaseAgreement.ExecutedArtifactId != null
                        && newer.BaseAgreement.VoidedAtUtc == null
                        && newer.BaseAgreement.DraftCanceledAtUtc == null)),
            })
            .SingleAsync(ct);
        return facts.EffectiveCount == command.AddendumDecisions.Count
            && facts.MatchedCount == command.AddendumDecisions.Count;
    }

    internal static void ValidateAuthorizationShape(ILeaseAgreementDraftCommand command)
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

    internal static void ValidateEditShape(EditLeaseAgreementDraftCommand command)
    {
        if (command.LeaseAgreementId <= 0 || command.ExpectedDraftRevision <= 0
            || string.IsNullOrWhiteSpace(command.AgreementNumber) || command.AgreementNumber.Trim().Length > 100
            || !ValidTerms(command.TermType, command.TermStartOn, command.TermEndOn,
                command.GoverningFromOn, command.BaseRentAmount, command.RentDueDay,
                command.SecurityDepositObligation, command.LateFeeAmount, command.GracePeriodDays)
            || command.TermsSchemaVersion <= 0 || command.DocumentTemplateId <= 0
            || !IsJsonObject(command.TermsPayload)
            || !ValidSigners(command.Signers))
        {
            throw new ArgumentException("Agreement draft terms, revision, template, payload, or signers are invalid.");
        }
    }

    internal static void ValidateSuccessorShape(CreateLeaseAgreementSuccessorDraftCommand command)
    {
        if (command.SourceAgreementId <= 0
            || (command.DocumentTemplateId.HasValue && command.DocumentTemplateId <= 0)
            || command.ChangeType is not (LeaseAgreementChangeType.Correction
                or LeaseAgreementChangeType.Restatement or LeaseAgreementChangeType.Renewal
                or LeaseAgreementChangeType.MonthToMonth)
            || command.TermStartOn == default || command.GoverningFromOn == default
            || command.GoverningFromOn < command.TermStartOn
            || (command.ChangeType == LeaseAgreementChangeType.MonthToMonth && command.TermEndOn != null)
            || (command.ChangeType != LeaseAgreementChangeType.MonthToMonth
                && (command.TermEndOn == null || command.TermEndOn < command.TermStartOn
                    || command.GoverningFromOn > command.TermEndOn))
            || (command.ChangeType == LeaseAgreementChangeType.Correction
                && (string.IsNullOrWhiteSpace(command.CorrectionReason)
                    || command.CorrectionReason.Trim().Length > 1000))
            || (command.ChangeType != LeaseAgreementChangeType.Correction
                && !string.IsNullOrWhiteSpace(command.CorrectionReason)))
        {
            throw new ArgumentException(
                "Successor type, governing/term dates, or correction reason are invalid.");
        }
    }

    internal static void ValidateIssuedReplacementShape(
        ReplaceIssuedAgreementWithDraftCommand command)
    {
        if (command.SourceAgreementId <= 0
            || string.IsNullOrWhiteSpace(command.ReissueReason)
            || command.ReissueReason.Trim().Length > 1000
            || (command.VoidNote?.Trim().Length ?? 0) > 2000)
        {
            throw new ArgumentException(
                "Source Agreement, bounded void evidence, and a reissue reason of at most 1,000 characters are required.");
        }
    }

    private static bool ValidTerms(LeaseAgreementTermType termType, DateOnly start, DateOnly? end,
        DateOnly governing, decimal rent, short dueDay, decimal deposit, decimal lateFee, short grace) =>
        Enum.IsDefined(termType) && start != default && governing >= start
        && ((termType == LeaseAgreementTermType.FixedTerm && end >= start && governing <= end)
            || (termType == LeaseAgreementTermType.MonthToMonth && end == null))
        && rent >= 0 && deposit >= 0 && lateFee >= 0 && dueDay is >= 1 and <= 31
        && grace is >= 0 and <= 31;

    private static bool ValidSigners(IReadOnlyList<LeaseAgreementDraftSignerInput> signers) =>
        signers.Count > 0
        && signers.All(signer => signer.IsRequired && Enum.IsDefined(signer.SignerRole)
            && !string.IsNullOrWhiteSpace(signer.NameSnapshot) && signer.NameSnapshot.Trim().Length <= 200
            && !string.IsNullOrWhiteSpace(signer.EmailSnapshot) && signer.EmailSnapshot.Trim().Length <= 320
            && signer.SigningOrder > 0
            && signer.LeaseManagementPartyId.HasValue == signer.TenantId.HasValue)
        && signers.Select(signer => signer.SigningOrder).Distinct().Count() == signers.Count
        && signers.Select(signer => signer.EmailSnapshot.Trim().ToLowerInvariant()).Distinct().Count() == signers.Count
        && signers.Any(signer => signer.IsRequired && signer.SignerRole is
            LeaseLegalSignerRole.PrimaryTenant or LeaseLegalSignerRole.CoTenant);

    private static bool IsJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static AtomicAgreementDraftSignerInput[] ToAtomicSignerInputs(
        IReadOnlyList<LeaseAgreementDraftSignerInput> signers) => signers.Select(signer =>
        new AtomicAgreementDraftSignerInput(
            signer.LeaseManagementPartyId,
            signer.TenantId,
            (int)signer.SignerRole,
            signer.NameSnapshot.Trim(),
            signer.EmailSnapshot.Trim().ToLowerInvariant(),
            signer.SigningOrder,
            signer.IsRequired)).ToArray();

    internal static AtomicSemanticAudit Updated(
        ILeaseAgreementDraftCommand command, int agreementId, string reason) => new(
        command.PortfolioId, nameof(LeaseAgreement), agreementId, AuditLogOperation.Updated,
        UserId: command.ActorUserId, ChangeReason: reason);

    internal static AtomicSemanticAudit Created(
        ILeaseAgreementDraftCommand command, string reason) => new(
        command.PortfolioId, nameof(LeaseAgreement), 0, AuditLogOperation.Created,
        UserId: command.ActorUserId, ChangeReason: reason);

    internal static void StageOutbox(IAtomicWriteAttempt attempt, ILeaseAgreementDraftCommand command,
        DateTime nowUtc, int agreementId, string mutation,
        IReadOnlyList<int>? replacementAddendumIds = null) => attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(LeaseAgreement),
                entityId = agreementId,
                data = new
                {
                    mutation,
                    command.LeaseManagementId,
                    replacementAddendumIds = replacementAddendumIds ?? Array.Empty<int>(),
                },
            }),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        });

    internal static LeaseAgreementDraftMutationResult Error(
        LeaseAgreementDraftMutationOutcome outcome, ILeaseAgreementDraftCommand command,
        int agreementId, int version, int revision, string error) => new(
        outcome, command.LeaseManagementId, agreementId, version, revision,
        command switch
        {
            CreateLeaseAgreementSuccessorDraftCommand successor => successor.SourceAgreementId,
            ReplaceIssuedAgreementWithDraftCommand replacement => replacement.SourceAgreementId,
            _ => null,
        },
        Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), error);

    internal static UnauthorizedAccessException Unauthorized() => new(
        "The Agreement is not authorized in the current access context and property scope.");
}
