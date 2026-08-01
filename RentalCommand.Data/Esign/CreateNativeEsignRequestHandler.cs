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

/// <summary>Canonical Agreement issuance. No legacy Lease row is read or changed.</summary>
public sealed class IssueLeaseAgreementHandler
    : IAtomicCommandHandler<IssueLeaseAgreementCommand, IssueLeaseAgreementResult>
{
    private readonly RentalCommandDbContext _db;

    public IssueLeaseAgreementHandler(RentalCommandDbContext db) => _db = db;

    public async Task<IssueLeaseAgreementResult> HandleAsync(
        IssueLeaseAgreementCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var businessNowUtc = times.EffectiveNowUtc;
        var agreement = await AuthorizedAgreements(command, _db, times.WallClockUtc)
            .Include(item => item.Signers)
            .SingleOrDefaultAsync(item => item.Id == command.LeaseAgreementId, ct)
            ?? throw new UnauthorizedAccessException("Agreement issuance is outside the caller's current access scope.");

        if (agreement.IssuedAtUtc != null || agreement.DraftCanceledAtUtc != null
            || agreement.VoidedAtUtc != null || agreement.DraftRevision != command.ExpectedDraftRevision)
        {
            throw new DomainValidationException("Only the exact current revision of an open Agreement draft can be issued.");
        }
        if (agreement.Signers.Count == 0 || agreement.Signers.Any(signer => !signer.IsRequired))
        {
            throw new DomainValidationException("The Agreement must contain its complete required signer snapshot before issue.");
        }
        var predecessorId = agreement.ReplacesAgreementId ?? agreement.RenewsAgreementId;
        if (predecessorId.HasValue)
        {
            var predecessorFacts = await _db.Set<LeaseAgreement>()
                .Where(source => source.Id == predecessorId.Value
                    && source.PortfolioId == command.PortfolioId
                    && source.LeaseManagementId == command.LeaseManagementId)
                .Select(source => new
                {
                    InvalidEffectiveDate = agreement.GoverningFromOn <= source.GoverningFromOn,
                    SuccessorConflict = (source.SupersededByAgreementId != null
                            && source.SupersededByAgreementId != agreement.Id)
                        || source.CorrectionsAndRestatements.Any(candidate =>
                            candidate.Id != agreement.Id
                            && candidate.DraftCanceledAtUtc == null
                            && (candidate.VoidedAtUtc == null || candidate.FullyExecutedAtUtc != null))
                        || source.Renewals.Any(candidate =>
                            candidate.Id != agreement.Id
                            && candidate.DraftCanceledAtUtc == null
                            && (candidate.VoidedAtUtc == null || candidate.FullyExecutedAtUtc != null)),
                })
                .SingleOrDefaultAsync(ct)
                ?? throw new NativeEsignLegalTransitionConflictException(
                    AtomicLegalExecutionTransitionOutcome.TargetChanged);
            if (predecessorFacts.InvalidEffectiveDate)
            {
                throw new NativeEsignLegalTransitionConflictException(
                    AtomicLegalExecutionTransitionOutcome.InvalidEffectiveDate);
            }
            if (predecessorFacts.SuccessorConflict)
            {
                throw new NativeEsignLegalTransitionConflictException(
                    AtomicLegalExecutionTransitionOutcome.SuccessorConflict);
            }
        }
        var suppliedSignerIds = command.Signers.Select(item => item.AgreementSignerId).Order().ToArray();
        var frozenSignerIds = agreement.Signers.Select(item => item.Id).Order().ToArray();
        if (!suppliedSignerIds.SequenceEqual(frozenSignerIds))
        {
            throw new DomainValidationException("The signing packet must match every frozen Agreement signer exactly once.");
        }

        if (command.ExpectedDocumentSourceVersionId != agreement.DocumentSourceVersionId)
        {
            throw new DomainValidationException(
                "The issued PDF was rendered from a different Agreement source version.");
        }
        var issuanceFingerprint = LegalDocumentIssuanceBinding.Create(
            nameof(LeaseAgreement),
            command.PortfolioId,
            command.LeaseManagementId,
            agreement.Id,
            agreement.DraftRevision,
            agreement.DocumentSourceVersionId,
            agreement.TermsSchemaVersion,
            agreement.TermsPayload,
            command.ContentSha256,
            command.FileSize,
            command.FileName);
        if (!LegalDocumentIssuanceBinding.Matches(command.IssuanceFingerprint, issuanceFingerprint))
        {
            throw new DomainValidationException(
                "The issued PDF fingerprint does not match the Agreement source, terms, and artifact.");
        }

        var pending = await _db.Set<PendingFileUpload>()
            .SingleOrDefaultAsync(upload => upload.Id == command.PendingUploadId
                && upload.PortfolioId == command.PortfolioId
                && upload.ActorScopeId == command.ActorUserId
                && upload.Purpose == LegalDocumentIssuanceBinding.AgreementUploadPurpose
                && upload.State == PendingFileUploadState.Prepared
                && upload.CleanupClaimToken == null
                && upload.RequestFingerprint == issuanceFingerprint, ct)
            ?? throw new DomainValidationException("The issued PDF admission is missing or changed.");
        if (pending.StoragePath != command.StorageKey || pending.FileName != command.FileName
            || pending.ContentType != "application/pdf" || pending.SizeBytes != command.FileSize)
        {
            throw new DomainValidationException("The issued PDF does not match its admitted storage metadata.");
        }

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = nameof(LeaseAgreement),
            EntityId = agreement.Id,
            UploadedAt = businessNowUtc,
        };
        _db.Add(storedFile);
        await context.FlushBusinessAsync(ct);
        var artifact = new LegalDocumentArtifact
        {
            PortfolioId = command.PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = command.StorageKey,
            FileName = command.FileName,
            ContentType = "application/pdf",
            ByteLength = command.FileSize,
            ContentSha256 = command.ContentSha256,
            LegalIssuanceFingerprint = issuanceFingerprint,
            CreatedAtUtc = businessNowUtc,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(artifact);
        await context.FlushBusinessAsync(ct);

        agreement.IssuedArtifactId = artifact.Id;
        agreement.IssuedAtUtc = businessNowUtc;
        agreement.UpdatedAtUtc = businessNowUtc;
        context.BindSemanticAudit(agreement, new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseAgreement), agreement.Id, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { agreement.IssuedArtifactId, agreement.IssuedAtUtc }),
            ChangeReason: "Issued immutable Agreement artifact and froze the legal signer snapshot."));
        var packet = new SignatureRequest
        {
            PortfolioId = command.PortfolioId,
            LeaseAgreementId = agreement.Id,
            Provider = "native",
            PublicId = Guid.NewGuid(),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            Status = SignatureRequestStatus.AwaitingSignatures,
            Subject = command.Subject.Trim(),
            IssuedArtifactId = artifact.Id,
            PreparedAtUtc = businessNowUtc,
            ProviderAcceptedAtUtc = businessNowUtc,
            CreatedByUserId = command.ActorUserId,
        };
        var invitationTokens = new Dictionary<int, string>();
        foreach (var input in command.Signers)
        {
            var source = agreement.Signers.Single(item => item.Id == input.AgreementSignerId);
            var rawToken = GenerateRawToken();
            packet.Signers.Add(new SignatureSigner
            {
                PortfolioId = command.PortfolioId,
                AgreementSignerId = source.Id,
                NameSnapshot = source.NameSnapshot,
                EmailSnapshot = source.EmailSnapshot,
                SigningOrder = source.SigningOrder,
                IsRequired = source.IsRequired,
                TokenHash = HashToken(rawToken),
                TokenExpiresAtUtc = times.WallClockUtc.AddDays(14),
                Status = SignatureSignerStatus.Pending,
                CreatedAtUtc = businessNowUtc,
                UpdatedAtUtc = businessNowUtc,
            });
            invitationTokens[source.Id] = rawToken;
        }
        packet.AuditEvents.Add(new SignatureAuditEvent
        {
            PortfolioId = command.PortfolioId,
            Type = SignatureAuditEventType.Sent,
            OccurredAtUtc = businessNowUtc,
            Detail = $"Native packet admitted for {packet.Signers.Count} required signer(s).",
        });
        _db.Add(packet);
        await context.FlushBusinessAsync(ct);
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = storedFile.Id;
        pending.UpdatedAtUtc = businessNowUtc;

        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(SignatureRequest), packet.Id, AuditLogOperation.Created, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { packet.PublicId, packet.LeaseAgreementId, Status = packet.Status.ToString() }),
            ChangeReason: "Created canonical Agreement signature packet."), businessNowUtc);
        foreach (var signer in packet.Signers)
        {
            var link = $"{command.WebBaseUrl.TrimEnd('/')}/sign/{invitationTokens[signer.AgreementSignerId!.Value]}";
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = packet.Id,
                    leaseManagementId = command.LeaseManagementId,
                    leaseAgreementId = agreement.Id,
                    to = signer.EmailSnapshot,
                    subject = $"Please sign: {packet.Subject}",
                    body = $"Hi {signer.NameSnapshot},\n\nReview and sign {packet.Subject}:\n{link}\n\nThis secure link is unique to you.",
                }),
                IdempotencyKey = $"agreement-esign:{packet.Id}:signer:{signer.Id}:invite",
                CreatedAtUtc = businessNowUtc,
                NextAttemptAtUtc = businessNowUtc,
            });
        }
        return new(packet.PublicId, command.LeaseManagementId, agreement.Id, packet.Id, artifact.Id);
    }

    public async Task AuthorizeReplayAsync(IssueLeaseAgreementCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedAgreements(command, _db, now).AnyAsync(item => item.Id == command.LeaseAgreementId, ct))
            throw new UnauthorizedAccessException("Agreement issuance replay is outside the caller's current access scope.");
    }

    private static IQueryable<LeaseAgreement> AuthorizedAgreements(IssueLeaseAgreementCommand command,
        RentalCommandDbContext db, DateTime securityNowUtc) =>
        LeaseAgreementDraftCommandSupport.AuthorizedRelationships(command, db, securityNowUtc)
            .SelectMany(relationship => relationship.Agreements);

    private static void Validate(IssueLeaseAgreementCommand command)
    {
        if (command.ActorUserId <= 0 || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision < 1 || command.ExpectedDraftRevision < 1 || command.FileSize <= 0
            || command.Signers.Count == 0
            || !LegalDocumentIssuanceBinding.IsSha256(command.ContentSha256)
            || command.ExpectedDocumentSourceVersionId <= 0
            || !LegalDocumentIssuanceBinding.IsSha256(command.IssuanceFingerprint)
            || string.IsNullOrWhiteSpace(command.StorageKey) || string.IsNullOrWhiteSpace(command.FileName)
            || string.IsNullOrWhiteSpace(command.Subject) || string.IsNullOrWhiteSpace(command.WebBaseUrl)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey) || command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException("The Agreement issue command is incomplete.");
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string GenerateRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
