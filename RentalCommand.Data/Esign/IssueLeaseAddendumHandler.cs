using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Esign;

/// <summary>Canonical Addendum issuance through the shared immutable native e-sign workflow.</summary>
public sealed class IssueLeaseAddendumHandler
    : IAtomicCommandHandler<IssueLeaseAddendumCommand, IssueLeaseAddendumResult>,
      IAtomicReplayAuthorizer<IssueLeaseAddendumCommand>
{
    public async Task<IssueLeaseAddendumResult> HandleAsync(
        IssueLeaseAddendumCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, command.LeaseManagementId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var candidate = await LeaseAddendumCommandSupport.AuthorizedRelationships(
                command, attempt.Persistence, times.WallClockUtc)
            .SelectMany(item => item.Addenda)
            .Where(item => item.Id == command.LeaseAddendumId)
            .Select(item => new
            {
                Addendum = item,
                Signers = item.Signers.OrderBy(signer => signer.Id).ToList(),
                FinancialEffects = item.FinancialEffects
                    .OrderBy(effect => effect.EffectType)
                    .ThenBy(effect => effect.EffectiveFromOn)
                    .ThenBy(effect => effect.EffectiveThroughOn)
                    .ThenBy(effect => effect.DueOn)
                    .ThenBy(effect => effect.ChargeCode)
                    .ThenBy(effect => effect.Currency)
                    .ThenBy(effect => effect.Amount)
                    .ThenBy(effect => effect.Description)
                    .ThenBy(effect => effect.Id)
                    .Select(effect => new LegalDocumentIssuanceFinancialEffect(
                        effect.Id,
                        effect.EffectType,
                        effect.Amount,
                        effect.Currency,
                        effect.ChargeCode,
                        effect.EffectiveFromOn,
                        effect.EffectiveThroughOn,
                        effect.DueOn,
                        effect.Description))
                    .ToList(),
                HasCompleteRequiredSignerSnapshot = item.Signers.Any()
                    && item.Signers.All(signer => signer.IsRequired),
                BaseAgreementEligible = item.BaseAgreement != null
                    && item.BaseAgreement.VoidedAtUtc == null
                    && item.BaseAgreement.DraftCanceledAtUtc == null
                    && ((item.BaseAgreement.FullyExecutedAtUtc != null
                            && item.BaseAgreement.ExecutedArtifactId != null)
                        || (item.BaseAgreement.FullyExecutedAtUtc == null
                            && item.BaseAgreement.ExecutedArtifactId == null
                            && item.BaseAgreement.IssuedAtUtc != null
                            && item.BaseAgreement.IssuedArtifactId != null
                            && (item.BaseAgreement.ChangeType == LeaseAgreementChangeType.Renewal
                                || item.BaseAgreement.ChangeType == LeaseAgreementChangeType.MonthToMonth)
                            && item.ReplacesAddendum != null
                            && item.ReplacesAddendum.FullyExecutedAtUtc != null
                            && item.ReplacesAddendum.ExecutedArtifactId != null
                            && item.ReplacesAddendum.VoidedAtUtc == null
                            && item.ReplacesAddendum.DraftCanceledAtUtc == null
                            && item.ReplacesAddendum.SupersededEffectiveOn == null
                            && item.ReplacesAddendum.SupersededByAddendumId == null
                            && item.ReplacesAddendum.SeriesPublicId == item.SeriesPublicId
                            && item.ReplacesAddendum.BaseAgreementId == item.BaseAgreement.RenewsAgreementId
                            && item.ReplacesAddendum.EffectiveFromOn < item.BaseAgreement.GoverningFromOn
                            && (item.ReplacesAddendum.EffectiveThroughOn == null
                                || item.ReplacesAddendum.EffectiveThroughOn >= item.BaseAgreement.GoverningFromOn)
                            && item.EffectiveFromOn == item.BaseAgreement.GoverningFromOn
                            && item.BaseAgreement.RenewalAddendumDecisions.Any(decision =>
                                decision.Decision == LeaseRenewalAddendumDecisionType.ReissueAsAddendum
                                && decision.ReplacementAddendumId == item.Id
                                && decision.SourceAddendumSeriesPublicId == item.SeriesPublicId))),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw LeaseAddendumCommandSupport.Unauthorized();
        var addendum = candidate.Addendum;
        var signers = candidate.Signers;
        if (addendum.IssuedAtUtc != null || addendum.DraftCanceledAtUtc != null
            || addendum.VoidedAtUtc != null || addendum.DraftRevision != command.ExpectedDraftRevision)
            throw new DomainValidationException("Only the exact current revision of an open Addendum draft can be issued.");
        if (!candidate.BaseAgreementEligible)
            throw new DomainValidationException(
                "The Addendum's exact base Agreement must be executed and nonvoid, or be its issued renewal with an exact ReissueAsAddendum decision.");
        if (!candidate.HasCompleteRequiredSignerSnapshot)
            throw new DomainValidationException("The Addendum must contain its complete required signer snapshot before issue.");
        var supplied = command.Signers.Select(item => item.AddendumSignerId).Order().ToArray();
        var frozen = signers.Select(item => item.Id).ToArray();
        if (!supplied.SequenceEqual(frozen))
            throw new DomainValidationException("The signing packet must match every frozen Addendum signer exactly once.");
        var signersById = signers.ToDictionary(item => item.Id);

        if (command.ExpectedDocumentSourceVersionId != addendum.DocumentSourceVersionId)
            throw new DomainValidationException(
                "The issued PDF was rendered from a different Addendum source version.");
        var issuanceFingerprint = LegalDocumentIssuanceBinding.CreateAddendum(
            command.PortfolioId,
            command.LeaseManagementId,
            addendum.Id,
            addendum.DraftRevision,
            addendum.DocumentSourceVersionId,
            addendum.TermsSchemaVersion,
            addendum.TermsPayload,
            candidate.FinancialEffects,
            command.ContentSha256,
            command.FileSize,
            command.FileName);
        if (!LegalDocumentIssuanceBinding.Matches(command.IssuanceFingerprint, issuanceFingerprint))
            throw new DomainValidationException(
                "The issued PDF fingerprint does not match the Addendum source, terms, and artifact.");

        var pending = await attempt.Persistence.Query<PendingFileUpload>()
            .SingleOrDefaultAsync(upload => upload.Id == command.PendingUploadId
                && upload.PortfolioId == command.PortfolioId
                && upload.ActorScopeId == command.ActorUserId
                && upload.Purpose == LegalDocumentIssuanceBinding.AddendumUploadPurpose
                && upload.State == PendingFileUploadState.Prepared && upload.CleanupClaimToken == null
                && upload.RequestFingerprint == issuanceFingerprint, ct)
            ?? throw new DomainValidationException("The issued PDF admission is missing or changed.");
        if (pending.StoragePath != command.StorageKey || pending.FileName != command.FileName
            || pending.ContentType != "application/pdf" || pending.SizeBytes != command.FileSize)
            throw new DomainValidationException("The issued PDF does not match its admitted storage metadata.");

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId, FileName = command.FileName, FilePath = command.StorageKey,
            ContentType = "application/pdf", FileSize = command.FileSize, EntityType = nameof(LeaseAddendum),
            EntityId = addendum.Id, UploadedAt = times.WallClockUtc,
        };
        attempt.Persistence.Add(storedFile);
        await attempt.FlushBusinessAsync(ct);
        var artifact = new LegalDocumentArtifact
        {
            PortfolioId = command.PortfolioId, StoredFileId = storedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAddendum, StorageKey = command.StorageKey,
            FileName = command.FileName, ContentType = "application/pdf", ByteLength = command.FileSize,
            ContentSha256 = command.ContentSha256, LegalIssuanceFingerprint = issuanceFingerprint,
            CreatedAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(artifact);
        await attempt.FlushBusinessAsync(ct);

        addendum.IssuedArtifactId = artifact.Id;
        addendum.IssuedAtUtc = times.WallClockUtc;
        addendum.UpdatedAtUtc = times.WallClockUtc;
        attempt.BindSemanticAudit(addendum, new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseAddendum), addendum.Id, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { addendum.IssuedArtifactId, addendum.IssuedAtUtc }),
            ChangeReason: "Issued immutable Addendum artifact and froze its signer/effect snapshot."));
        var packet = new SignatureRequest
        {
            PortfolioId = command.PortfolioId, LeaseAddendumId = addendum.Id, Provider = "native",
            PublicId = Guid.NewGuid(), IdempotencyKey = command.DeliveryIdempotencyKey,
            Status = SignatureRequestStatus.AwaitingSignatures, Subject = command.Subject.Trim(),
            IssuedArtifactId = artifact.Id, PreparedAtUtc = times.WallClockUtc,
            ProviderAcceptedAtUtc = times.WallClockUtc, CreatedByUserId = command.ActorUserId,
        };
        var tokens = new Dictionary<int, string>();
        foreach (var input in command.Signers)
        {
            var source = signersById[input.AddendumSignerId];
            var token = GenerateRawToken();
            packet.Signers.Add(new SignatureSigner
            {
                PortfolioId = command.PortfolioId, AddendumSignerId = source.Id,
                NameSnapshot = source.NameSnapshot, EmailSnapshot = source.EmailSnapshot,
                SigningOrder = source.SigningOrder, IsRequired = source.IsRequired,
                TokenHash = HashToken(token), TokenExpiresAtUtc = times.WallClockUtc.AddDays(14),
                Status = SignatureSignerStatus.Pending, CreatedAtUtc = times.WallClockUtc,
                UpdatedAtUtc = times.WallClockUtc,
            });
            tokens[source.Id] = token;
        }
        packet.AuditEvents.Add(new SignatureAuditEvent
        {
            PortfolioId = command.PortfolioId, Type = SignatureAuditEventType.Sent,
            OccurredAtUtc = times.WallClockUtc,
            Detail = $"Native Addendum packet admitted for {packet.Signers.Count} required signer(s).",
        });
        attempt.Persistence.Add(packet);
        await attempt.FlushBusinessAsync(ct);
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = storedFile.Id;
        pending.UpdatedAtUtc = times.WallClockUtc;
        attempt.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(SignatureRequest),
            packet.Id, AuditLogOperation.Created, UserId: command.ActorUserId,
            ChangeReason: "Created canonical Addendum signature packet."), times.WallClockUtc);
        foreach (var signer in packet.Signers)
        {
            var link = $"{command.WebBaseUrl.TrimEnd('/')}/sign/{tokens[signer.AddendumSignerId!.Value]}";
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId, MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = packet.Id, leaseManagementId = command.LeaseManagementId,
                    leaseAddendumId = addendum.Id, to = signer.EmailSnapshot,
                    subject = $"Please sign: {packet.Subject}",
                    body = $"Hi {signer.NameSnapshot},\n\nReview and sign {packet.Subject}:\n{link}\n\nThis secure link is unique to you.",
                }),
                IdempotencyKey = $"addendum-esign:{packet.Id}:signer:{signer.Id}:invite",
                CreatedAtUtc = times.WallClockUtc, NextAttemptAtUtc = times.WallClockUtc,
            });
        }
        return new(packet.PublicId, command.LeaseManagementId, addendum.Id, packet.Id, artifact.Id);
    }

    public async Task AuthorizeReplayAsync(IssueLeaseAddendumCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await LeaseAddendumCommandSupport.AuthorizedRelationships(command, persistence, now)
                .SelectMany(item => item.Addenda).AnyAsync(item => item.Id == command.LeaseAddendumId, ct))
            throw LeaseAddendumCommandSupport.Unauthorized();
    }

    private static void Validate(IssueLeaseAddendumCommand command)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        if (command.LeaseAddendumId <= 0 || command.ExpectedDraftRevision <= 0 || command.FileSize <= 0
            || command.Signers.Count == 0
            || !LegalDocumentIssuanceBinding.IsSha256(command.ContentSha256)
            || command.ExpectedDocumentSourceVersionId <= 0
            || !LegalDocumentIssuanceBinding.IsSha256(command.IssuanceFingerprint)
            || string.IsNullOrWhiteSpace(command.StorageKey)
            || string.IsNullOrWhiteSpace(command.FileName) || string.IsNullOrWhiteSpace(command.Subject)
            || string.IsNullOrWhiteSpace(command.WebBaseUrl))
            throw new ArgumentException("The Addendum issue command is incomplete.");
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string GenerateRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
