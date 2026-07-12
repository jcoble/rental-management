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
        var addendum = await LeaseAddendumCommandSupport.AuthorizedRelationships(command, attempt.Persistence, times.WallClockUtc)
            .SelectMany(item => item.Addenda)
            .Include(item => item.BaseAgreement)
            .Include(item => item.Signers)
            .SingleOrDefaultAsync(item => item.Id == command.LeaseAddendumId, ct)
            ?? throw LeaseAddendumCommandSupport.Unauthorized();
        if (addendum.IssuedAtUtc != null || addendum.DraftCanceledAtUtc != null
            || addendum.VoidedAtUtc != null || addendum.DraftRevision != command.ExpectedDraftRevision)
            throw new DomainValidationException("Only the exact current revision of an open Addendum draft can be issued.");
        if (addendum.BaseAgreement?.FullyExecutedAtUtc == null || addendum.BaseAgreement.VoidedAtUtc != null)
            throw new DomainValidationException("The Addendum's exact base Agreement must remain executed and nonvoid at issue.");
        if (addendum.Signers.Count == 0 || addendum.Signers.Any(item => !item.IsRequired))
            throw new DomainValidationException("The Addendum must contain its complete required signer snapshot before issue.");
        var supplied = command.Signers.Select(item => item.AddendumSignerId).Order().ToArray();
        var frozen = addendum.Signers.Select(item => item.Id).Order().ToArray();
        if (!supplied.SequenceEqual(frozen))
            throw new DomainValidationException("The signing packet must match every frozen Addendum signer exactly once.");

        var pending = await attempt.Persistence.Query<PendingFileUpload>()
            .SingleOrDefaultAsync(upload => upload.Id == command.PendingUploadId
                && upload.PortfolioId == command.PortfolioId
                && upload.State == PendingFileUploadState.Prepared && upload.CleanupClaimToken == null
                && upload.RequestFingerprint == command.RequestFingerprint, ct)
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
            ContentSha256 = command.ContentSha256, CreatedAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(artifact);
        await attempt.FlushBusinessAsync(ct);

        addendum.IssuedArtifactId = artifact.Id;
        addendum.IssuedAtUtc = times.WallClockUtc;
        addendum.UpdatedAtUtc = times.WallClockUtc;
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
            var source = addendum.Signers.Single(item => item.Id == input.AddendumSignerId);
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
        attempt.BindSemanticAudit(addendum, new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseAddendum), addendum.Id, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { addendum.IssuedArtifactId, addendum.IssuedAtUtc }),
            ChangeReason: "Issued immutable Addendum artifact and froze its signer/effect snapshot."));
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
            || command.Signers.Count == 0 || command.ContentSha256.Length != 64
            || command.ContentSha256.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(command.RequestFingerprint) || string.IsNullOrWhiteSpace(command.StorageKey)
            || string.IsNullOrWhiteSpace(command.FileName) || string.IsNullOrWhiteSpace(command.Subject)
            || string.IsNullOrWhiteSpace(command.WebBaseUrl))
            throw new ArgumentException("The Addendum issue command is incomplete.");
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string GenerateRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
