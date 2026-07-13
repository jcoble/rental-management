using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CreateLeaseAddendumDraftHandler
    : IAtomicCommandHandler<CreateLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>,
      IAtomicReplayAuthorizer<CreateLeaseAddendumDraftCommand>
{
    public async Task<LeaseAddendumDraftMutationResult> HandleAsync(
        CreateLeaseAddendumDraftCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        LeaseAddendumCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var relationship = await LeaseAddendumCommandSupport.AuthorizedRelationships(command, attempt.Persistence, times.WallClockUtc)
            .Select(item => new
            {
                Relationship = item,
                BaseAgreement = item.Agreements.FirstOrDefault(agreement => agreement.Id == command.BaseAgreementId
                    && agreement.FullyExecutedAtUtc != null && agreement.ExecutedArtifactId != null
                    && agreement.VoidedAtUtc == null && agreement.DraftCanceledAtUtc == null),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw LeaseAddendumCommandSupport.Unauthorized();
        if (relationship.BaseAgreement is null)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.BaseAgreementNotEligible,
                command, 0, Guid.Empty, 0, 0, null,
                "An Addendum must amend a fully executed, nonvoid Agreement in this relationship.");
        }
        if (!await LeaseAddendumCommandSupport.ValidateReferencesAsync(command, relationship.BaseAgreement.Currency,
                attempt.Persistence, ct))
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.InvalidSigners,
                command, 0, Guid.Empty, 0, 0, null,
                "The template, signer provenance, and financial-effect currency must belong to this relationship.");
        }
        var sourceVersion = await attempt.Leasing.ResolveAuthoredDocumentSourceVersionAsync(
            command.PortfolioId, relationship.Relationship.PropertyId, command.LeaseManagementId,
            command.DocumentTemplateId,
            command.ActorUserId, times.WallClockUtc, ct);
        if (!sourceVersion.Resolved)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.InvalidSigners,
                command, 0, Guid.Empty, 0, 0, null,
                "The selected template could not be frozen as immutable source provenance.");
        }

        var addendum = new LeaseAddendum
        {
            PublicId = Guid.NewGuid(),
            SeriesPublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            LeaseManagementId = command.LeaseManagementId,
            BaseAgreementId = command.BaseAgreementId,
            VersionNumber = 1,
            AddendumNumber = command.AddendumNumber.Trim(),
            Purpose = command.Purpose,
            EffectiveFromOn = command.EffectiveFromOn,
            EffectiveThroughOn = command.EffectiveThroughOn,
            TermsSchemaVersion = command.TermsSchemaVersion,
            TermsPayload = command.TermsPayload,
            DocumentSourceVersionId = sourceVersion.DocumentSourceVersionId,
            CreatedAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
            UpdatedAtUtc = times.WallClockUtc,
            DraftRevision = 1,
        };
        attempt.Persistence.Add(addendum);
        attempt.BindSemanticAudit(addendum, LeaseAddendumCommandSupport.Created(command,
            "Created a standalone Addendum draft attached to an exact executed Agreement."));
        await attempt.FlushBusinessAsync(ct);
        var (signerIds, effectIds) = await LeaseAddendumCommandSupport.InsertChildrenAsync(
            command, addendum.Id, attempt, times.WallClockUtc, ct);
        LeaseAddendumCommandSupport.StageOutbox(attempt, command, times.WallClockUtc, addendum.Id,
            "addendum-draft-created");
        return new(LeaseAddendumDraftMutationOutcome.Applied, command.LeaseManagementId, addendum.Id,
            addendum.SeriesPublicId, addendum.VersionNumber, addendum.DraftRevision, null,
            signerIds, effectIds, null);
    }

    public Task AuthorizeReplayAsync(CreateLeaseAddendumDraftCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        LeaseAddendumCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class EditLeaseAddendumDraftHandler
    : IAtomicCommandHandler<EditLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>,
      IAtomicReplayAuthorizer<EditLeaseAddendumDraftCommand>
{
    public async Task<LeaseAddendumDraftMutationResult> HandleAsync(
        EditLeaseAddendumDraftCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        LeaseAddendumCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var addendum = await LeaseAddendumCommandSupport.AuthorizedRelationships(command, attempt.Persistence, now)
            .SelectMany(item => item.Addenda)
            .Include(item => item.BaseAgreement)
            .Include(item => item.Signers)
            .Include(item => item.FinancialEffects)
            .SingleOrDefaultAsync(item => item.Id == command.LeaseAddendumId, ct)
            ?? throw LeaseAddendumCommandSupport.Unauthorized();
        if (addendum.IssuedAtUtc != null || addendum.IssuedArtifactId != null
            || addendum.FullyExecutedAtUtc != null || addendum.ExecutedArtifactId != null
            || addendum.VoidedAtUtc != null || addendum.DraftCanceledAtUtc != null)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.DraftNotEditable,
                command, addendum.Id, addendum.SeriesPublicId, addendum.VersionNumber, addendum.DraftRevision,
                addendum.ReplacesAddendumId, "Only an unissued, uncanceled Addendum draft can be edited.");
        }
        if (addendum.DraftRevision != command.ExpectedDraftRevision)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.StaleDraftRevision,
                command, addendum.Id, addendum.SeriesPublicId, addendum.VersionNumber, addendum.DraftRevision,
                addendum.ReplacesAddendumId, "DraftRevision is stale; reload before editing.");
        }
        if (addendum.BaseAgreement is null || !await LeaseAddendumCommandSupport.ValidateReferencesAsync(
                command, addendum.BaseAgreement.Currency, attempt.Persistence, ct))
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.InvalidSigners,
                command, addendum.Id, addendum.SeriesPublicId, addendum.VersionNumber, addendum.DraftRevision,
                addendum.ReplacesAddendumId, "The template, signer provenance, and effects are invalid.");
        }
        var sourceVersion = await attempt.Leasing.ResolveAuthoredDocumentSourceVersionAsync(
            command.PortfolioId, 0, command.LeaseManagementId, command.DocumentTemplateId,
            command.ActorUserId, now, ct);
        if (!sourceVersion.Resolved)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.InvalidSigners,
                command, addendum.Id, addendum.SeriesPublicId, addendum.VersionNumber,
                addendum.DraftRevision, addendum.ReplacesAddendumId,
                "The selected template could not be frozen as immutable source provenance.");
        }

        addendum.AddendumNumber = command.AddendumNumber.Trim();
        addendum.Purpose = command.Purpose;
        addendum.EffectiveFromOn = command.EffectiveFromOn;
        addendum.EffectiveThroughOn = command.EffectiveThroughOn;
        addendum.TermsSchemaVersion = command.TermsSchemaVersion;
        addendum.TermsPayload = command.TermsPayload;
        addendum.DocumentSourceVersionId = sourceVersion.DocumentSourceVersionId;
        addendum.UpdatedAtUtc = now;
        addendum.DraftRevision++;
        attempt.BindSemanticAudit(addendum, LeaseAddendumCommandSupport.Updated(command, addendum.Id,
            "Edited Addendum draft terms, signers, and financial effects."));
        foreach (var signer in addendum.Signers)
        {
            attempt.BindSemanticAudit(signer, new AtomicSemanticAudit(command.PortfolioId,
                nameof(LeaseAddendumSigner), signer.Id, AuditLogOperation.Deleted,
                UserId: command.ActorUserId, ChangeReason: "Replaced Addendum draft signer snapshot."));
            attempt.Persistence.Remove(signer);
        }
        foreach (var effect in addendum.FinancialEffects)
        {
            attempt.BindSemanticAudit(effect, new AtomicSemanticAudit(command.PortfolioId,
                nameof(LeaseAddendumFinancialEffect), effect.Id, AuditLogOperation.Deleted,
                UserId: command.ActorUserId, ChangeReason: "Replaced Addendum draft financial effect."));
            attempt.Persistence.Remove(effect);
        }
        await attempt.FlushBusinessAsync(ct);
        var (signerIds, effectIds) = await LeaseAddendumCommandSupport.InsertChildrenAsync(
            command, addendum.Id, attempt, now, ct);
        LeaseAddendumCommandSupport.StageOutbox(attempt, command, now, addendum.Id, "addendum-draft-edited");
        return new(LeaseAddendumDraftMutationOutcome.Applied, command.LeaseManagementId, addendum.Id,
            addendum.SeriesPublicId, addendum.VersionNumber, addendum.DraftRevision,
            addendum.ReplacesAddendumId, signerIds, effectIds, null);
    }

    public Task AuthorizeReplayAsync(EditLeaseAddendumDraftCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        LeaseAddendumCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class CorrectLeaseAddendumDraftHandler
    : IAtomicCommandHandler<CorrectLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>,
      IAtomicReplayAuthorizer<CorrectLeaseAddendumDraftCommand>
{
    public async Task<LeaseAddendumDraftMutationResult> HandleAsync(
        CorrectLeaseAddendumDraftCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        LeaseAddendumCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var source = await LeaseAddendumCommandSupport.AuthorizedRelationships(command, attempt.Persistence, now)
            .SelectMany(item => item.Addenda)
            .Where(item => item.Id == command.SourceAddendumId)
            .Select(item => new
            {
                item.Id,
                item.SeriesPublicId,
                item.VersionNumber,
                item.BaseAgreementId,
                item.AddendumNumber,
                item.Purpose,
                item.EffectiveThroughOn,
                item.TermsSchemaVersion,
                item.TermsPayload,
                item.DocumentSourceVersionId,
                IsEligible = item.FullyExecutedAtUtc != null
                    && item.ExecutedArtifactId != null
                    && item.VoidedAtUtc == null
                    && item.DraftCanceledAtUtc == null
                    && item.SupersededByAddendumId == null
                    && command.SupersessionEffectiveOn > item.EffectiveFromOn
                    && (!item.EffectiveThroughOn.HasValue
                        || command.SupersessionEffectiveOn <= item.EffectiveThroughOn.Value),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw LeaseAddendumCommandSupport.Unauthorized();
        if (!source.IsEligible)
        {
            return LeaseAddendumCommandSupport.Error(LeaseAddendumDraftMutationOutcome.SourceAddendumNotEligible,
                command, 0, source.SeriesPublicId, source.VersionNumber + 1, 0, source.Id,
                "Only the latest executed, nonvoid Addendum can be corrected within its effective range.");
        }
        var nextVersion = await attempt.Persistence.Query<LeaseAddendum>()
            .Where(item => item.PortfolioId == command.PortfolioId
                && item.LeaseManagementId == command.LeaseManagementId
                && item.SeriesPublicId == source.SeriesPublicId)
            .MaxAsync(item => item.VersionNumber, ct) + 1;
        var correction = new LeaseAddendum
        {
            PublicId = Guid.NewGuid(),
            SeriesPublicId = source.SeriesPublicId,
            PortfolioId = command.PortfolioId,
            LeaseManagementId = command.LeaseManagementId,
            BaseAgreementId = source.BaseAgreementId,
            VersionNumber = nextVersion,
            AddendumNumber = source.AddendumNumber,
            Purpose = source.Purpose,
            ReplacesAddendumId = source.Id,
            EffectiveFromOn = command.SupersessionEffectiveOn,
            EffectiveThroughOn = source.EffectiveThroughOn,
            TermsSchemaVersion = source.TermsSchemaVersion,
            TermsPayload = source.TermsPayload,
            DocumentSourceVersionId = source.DocumentSourceVersionId,
            CreatedAtUtc = now,
            CreatedByUserId = command.ActorUserId,
            UpdatedAtUtc = now,
            DraftRevision = 1,
        };
        attempt.Persistence.Add(correction);
        attempt.BindSemanticAudit(correction, LeaseAddendumCommandSupport.Created(command,
            "Created a new editable Addendum correction version without changing the issued source."));
        await attempt.FlushBusinessAsync(ct);
        var childCopy = await attempt.Leasing.CopyAddendumCorrectionChildrenAsync(
            command.PortfolioId,
            command.LeaseManagementId,
            source.Id,
            correction.Id,
            ct);
        if (!childCopy.Eligible)
        {
            throw new InvalidOperationException(
                "Source or correction Addendum changed before child snapshots were copied.");
        }
        foreach (var signerId in childCopy.CreatedSignerIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAddendumSigner), signerId, AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: "Copied signer snapshot into Addendum correction draft."), now);
        }
        foreach (var effectId in childCopy.CreatedFinancialEffectIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId, nameof(LeaseAddendumFinancialEffect), effectId, AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: "Copied financial effect into Addendum correction draft."), now);
        }
        LeaseAddendumCommandSupport.StageOutbox(attempt, command, now, correction.Id,
            "addendum-correction-draft-created");
        return new(LeaseAddendumDraftMutationOutcome.Applied, command.LeaseManagementId,
            correction.Id, correction.SeriesPublicId, correction.VersionNumber, correction.DraftRevision,
            source.Id, childCopy.CreatedSignerIds, childCopy.CreatedFinancialEffectIds, null);
    }

    public Task AuthorizeReplayAsync(CorrectLeaseAddendumDraftCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        LeaseAddendumCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

internal static class LeaseAddendumCommandSupport
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(
        ILeaseAddendumCommand command, IAtomicPersistenceSession persistence, DateTime now) =>
        LeaseAgreementDraftCommandSupport.AuthorizedRelationships(command, persistence, now);

    internal static async Task AuthorizeReplayAsync<T>(T command, IAtomicPersistenceSession persistence,
        CancellationToken ct) where T : ILeaseAddendumCommand
    {
        ValidateAuthorization(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedRelationships(command, persistence, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static void Validate(CreateLeaseAddendumDraftCommand command)
    {
        ValidateAuthorization(command);
        ValidateDraft(command.AddendumNumber, command.Purpose, command.EffectiveFromOn,
            command.EffectiveThroughOn, command.TermsSchemaVersion, command.TermsPayload,
            command.DocumentTemplateId,
            command.Signers, command.FinancialEffects);
        if (command.BaseAgreementId <= 0) throw new ArgumentException("BaseAgreementId is required.");
    }

    internal static void Validate(EditLeaseAddendumDraftCommand command)
    {
        ValidateAuthorization(command);
        ValidateDraft(command.AddendumNumber, command.Purpose, command.EffectiveFromOn,
            command.EffectiveThroughOn, command.TermsSchemaVersion, command.TermsPayload,
            command.DocumentTemplateId,
            command.Signers, command.FinancialEffects);
        if (command.LeaseAddendumId <= 0 || command.ExpectedDraftRevision <= 0)
            throw new ArgumentException("Addendum and DraftRevision are required.");
    }

    internal static void Validate(CorrectLeaseAddendumDraftCommand command)
    {
        ValidateAuthorization(command);
        if (command.SourceAddendumId <= 0 || command.SupersessionEffectiveOn == default)
            throw new ArgumentException("Source Addendum and supersession date are required.");
    }

    private static void ValidateAuthorization(ILeaseAddendumCommand command) =>
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);

    private static void ValidateDraft(string number, LeaseAddendumPurpose purpose,
        DateOnly from, DateOnly? through, int schema, string payload, int templateId,
        IReadOnlyList<LeaseAddendumDraftSignerInput> signers,
        IReadOnlyList<LeaseAddendumFinancialEffectInput> effects)
    {
        if (string.IsNullOrWhiteSpace(number) || number.Trim().Length > 100 || !Enum.IsDefined(purpose)
            || from == default || through < from || schema <= 0 || !IsJsonObject(payload)
            || templateId <= 0 || !ValidSigners(signers)
            || !effects.All(ValidEffect))
            throw new ArgumentException("Addendum draft terms, signers, or financial effects are invalid.");
    }

    private static bool ValidSigners(IReadOnlyList<LeaseAddendumDraftSignerInput> signers) =>
        signers.Count > 0
        && signers.All(item => Enum.IsDefined(item.SignerRole) && item.SigningOrder > 0
            && !string.IsNullOrWhiteSpace(item.NameSnapshot) && item.NameSnapshot.Trim().Length <= 200
            && !string.IsNullOrWhiteSpace(item.EmailSnapshot) && item.EmailSnapshot.Trim().Length <= 320
            && item.LeaseManagementPartyId.HasValue == item.TenantId.HasValue)
        && signers.Select(item => item.SigningOrder).Distinct().Count() == signers.Count
        && signers.Select(item => item.EmailSnapshot.Trim().ToLowerInvariant()).Distinct().Count() == signers.Count
        && signers.Any(item => item.IsRequired && item.SignerRole is
            LeaseLegalSignerRole.PrimaryTenant or LeaseLegalSignerRole.CoTenant);

    private static bool ValidEffect(LeaseAddendumFinancialEffectInput item) =>
        Enum.IsDefined(item.EffectType) && !string.IsNullOrWhiteSpace(item.Currency)
        && item.Currency.Trim().Length == 3
        && !string.IsNullOrWhiteSpace(item.ChargeCode) && item.ChargeCode.Trim().Length <= 50
        && !string.IsNullOrWhiteSpace(item.Description) && item.Description.Trim().Length <= 500
        && (!item.EffectiveThroughOn.HasValue
            || (item.EffectiveFromOn.HasValue
                && item.EffectiveThroughOn.Value >= item.EffectiveFromOn.Value))
        && item.EffectType switch
        {
            LeaseAddendumFinancialEffectType.RecurringRentDelta => item.Amount != 0
                && item.EffectiveFromOn.HasValue && item.DueOn == null,
            LeaseAddendumFinancialEffectType.OneTimeCharge => item.Amount > 0
                && item.DueOn.HasValue && item.EffectiveFromOn == null && item.EffectiveThroughOn == null,
            LeaseAddendumFinancialEffectType.DepositObligationDelta => item.Amount != 0
                && item.EffectiveFromOn.HasValue && item.DueOn == null,
            _ => false,
        };

    private static bool IsJsonObject(string payload)
    {
        try { using var document = JsonDocument.Parse(payload); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    internal static async Task<bool> ValidateReferencesAsync(CreateLeaseAddendumDraftCommand command,
        string currency, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        await ValidateReferencesAsync(command, command.Signers, command.FinancialEffects,
            command.DocumentTemplateId, currency, persistence, ct);

    internal static async Task<bool> ValidateReferencesAsync(EditLeaseAddendumDraftCommand command,
        string currency, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        await ValidateReferencesAsync(command, command.Signers, command.FinancialEffects,
            command.DocumentTemplateId, currency, persistence, ct);

    private static async Task<bool> ValidateReferencesAsync(ILeaseAddendumCommand command,
        IReadOnlyList<LeaseAddendumDraftSignerInput> signers,
        IReadOnlyList<LeaseAddendumFinancialEffectInput> effects, int templateId,
        string currency, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var partyIds = signers.Where(item => item.LeaseManagementPartyId.HasValue)
            .Select(item => item.LeaseManagementPartyId!.Value).Distinct().ToArray();
        var tenantIds = signers.Where(item => item.TenantId.HasValue)
            .Select(item => item.TenantId!.Value).Distinct().ToArray();
        var facts = await persistence.Query<LeaseManagement>()
            .Where(item => item.Id == command.LeaseManagementId && item.PortfolioId == command.PortfolioId)
            .Select(item => new
            {
                PartyCount = item.Parties.Count(party => partyIds.Contains(party.Id)
                    && tenantIds.Contains(party.TenantId)),
                TemplateValid = persistence.Query<DocumentTemplate>().Any(template =>
                    template.Id == templateId && template.PortfolioId == command.PortfolioId
                    && template.Kind == DocumentTemplateKind.Lease
                    && template.Status == DocumentTemplateStatus.Active
                    && template.ArchivedAtUtc == null
                    && (template.PropertyId == null || template.PropertyId == item.PropertyId)),
            }).SingleAsync(ct);
        return facts.PartyCount == partyIds.Length && facts.TemplateValid
            && effects.All(effect => string.Equals(effect.Currency.Trim(), currency, StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<(int[] SignerIds, int[] EffectIds)> InsertChildrenAsync(
        CreateLeaseAddendumDraftCommand command, int addendumId, IAtomicWriteAttempt attempt,
        DateTime now, CancellationToken ct) => await InsertChildrenAsync(command, addendumId,
            command.Signers, command.FinancialEffects, attempt, now, ct);

    internal static async Task<(int[] SignerIds, int[] EffectIds)> InsertChildrenAsync(
        EditLeaseAddendumDraftCommand command, int addendumId, IAtomicWriteAttempt attempt,
        DateTime now, CancellationToken ct) => await InsertChildrenAsync(command, addendumId,
            command.Signers, command.FinancialEffects, attempt, now, ct);

    private static async Task<(int[] SignerIds, int[] EffectIds)> InsertChildrenAsync(
        ILeaseAddendumCommand command, int addendumId, IReadOnlyList<LeaseAddendumDraftSignerInput> signerInputs,
        IReadOnlyList<LeaseAddendumFinancialEffectInput> effectInputs, IAtomicWriteAttempt attempt,
        DateTime now, CancellationToken ct)
    {
        var signers = signerInputs.Select(item => new LeaseAddendumSigner
        {
            PortfolioId = command.PortfolioId, LeaseAddendumId = addendumId,
            LeaseManagementPartyId = item.LeaseManagementPartyId, TenantId = item.TenantId,
            SignerRole = item.SignerRole, NameSnapshot = item.NameSnapshot.Trim(),
            EmailSnapshot = item.EmailSnapshot.Trim().ToLowerInvariant(), SigningOrder = item.SigningOrder,
            IsRequired = item.IsRequired,
        }).ToArray();
        var effects = effectInputs.Select(item => new LeaseAddendumFinancialEffect
        {
            PortfolioId = command.PortfolioId, LeaseAddendumId = addendumId,
            EffectType = item.EffectType, Amount = item.Amount,
            Currency = item.Currency.Trim().ToUpperInvariant(), ChargeCode = item.ChargeCode.Trim(),
            EffectiveFromOn = item.EffectiveFromOn, EffectiveThroughOn = item.EffectiveThroughOn,
            DueOn = item.DueOn, Description = item.Description.Trim(),
        }).ToArray();
        attempt.Persistence.AddRange(signers);
        attempt.Persistence.AddRange(effects);
        foreach (var signer in signers) attempt.BindSemanticAudit(signer, new AtomicSemanticAudit(
            command.PortfolioId, nameof(LeaseAddendumSigner), 0, AuditLogOperation.Created,
            UserId: command.ActorUserId, ChangeReason: "Created Addendum draft signer snapshot."));
        foreach (var effect in effects) attempt.BindSemanticAudit(effect, new AtomicSemanticAudit(
            command.PortfolioId, nameof(LeaseAddendumFinancialEffect), 0, AuditLogOperation.Created,
            UserId: command.ActorUserId, ChangeReason: "Created Addendum draft financial effect."));
        await attempt.FlushBusinessAsync(ct);
        return (signers.Select(item => item.Id).ToArray(), effects.Select(item => item.Id).ToArray());
    }

    internal static AtomicSemanticAudit Created(ILeaseAddendumCommand command, string reason) => new(
        command.PortfolioId, nameof(LeaseAddendum), 0, AuditLogOperation.Created,
        UserId: command.ActorUserId, ChangeReason: reason);
    internal static AtomicSemanticAudit Updated(ILeaseAddendumCommand command, int id, string reason) => new(
        command.PortfolioId, nameof(LeaseAddendum), id, AuditLogOperation.Updated,
        UserId: command.ActorUserId, ChangeReason: reason);
    internal static void StageOutbox(IAtomicWriteAttempt attempt, ILeaseAddendumCommand command,
        DateTime now, int id, string action) => attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId, MessageType = "entity-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(LeaseAddendum), entityId = id,
                leaseManagementId = command.LeaseManagementId, action }),
            IdempotencyKey = command.DeliveryIdempotencyKey, CreatedAtUtc = now, NextAttemptAtUtc = now,
        });
    internal static LeaseAddendumDraftMutationResult Error(LeaseAddendumDraftMutationOutcome outcome,
        ILeaseAddendumCommand command, int id, Guid series, int version, int revision, int? source, string error) =>
        new(outcome, command.LeaseManagementId, id, series, version, revision, source, [], [], error);
    internal static UnauthorizedAccessException Unauthorized() => new(
        "The Addendum is not authorized in the current access context and property scope.");
}
