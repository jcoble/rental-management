using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

public sealed class RecordNativeSignatureHandler
    : IAtomicCommandHandler<RecordNativeSignatureCommand, NativeSignerActionResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordNativeSignatureHandler(RentalCommandDbContext db) => _db = db;

    public async Task<NativeSignerActionResult> HandleAsync(
        RecordNativeSignatureCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task<NativeSignerActionResult> ExecuteAsync(
        RecordNativeSignatureCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var requestId = await _db.Set<SignatureSigner>()
            .Where(signer => signer.TokenHash == command.TokenHash)
            .Select(signer => (int?)signer.SignatureRequestId)
            .SingleOrDefaultAsync(ct);
        if (requestId is null)
        {
            return Missing();
        }

        await context.AcquireLockAsync("SignatureRequest", requestId.Value, ct);
        var signer = await _db.Set<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(candidate => candidate.TokenHash == command.TokenHash, ct);
        var request = signer.SignatureRequest!;
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var occurredAtUtc = times.EffectiveNowUtc;

        if (signer.TokenExpiresAtUtc <= securityNowUtc
            || signer.Status is SignatureSignerStatus.Signed or SignatureSignerStatus.Declined
            || request.Status is SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided)
        {
            return Unavailable(signer, request);
        }

        signer.Status = SignatureSignerStatus.Signed;
        signer.SignatureType = command.SignatureType;
        signer.TypedName = command.SignatureType == SignatureSignatureType.Typed ? command.TypedName : null;
        if (command.SignatureType == SignatureSignatureType.Drawn)
        {
            if (!command.DrawnSignaturePendingUploadId.HasValue
                || string.IsNullOrWhiteSpace(command.DrawnSignatureRequestFingerprint)
                || string.IsNullOrWhiteSpace(command.DrawnSignatureStorageKey)
                || command.DrawnSignatureFileSize is null or <= 0)
                throw new DomainValidationException("Drawn signature evidence admission is required.");
            var pending = await _db.Set<PendingFileUpload>()
                .SingleOrDefaultAsync(upload => upload.Id == command.DrawnSignaturePendingUploadId.Value
                    && upload.PortfolioId == request.PortfolioId
                    && upload.State == PendingFileUploadState.Prepared
                    && upload.CleanupClaimToken == null
                    && upload.RequestFingerprint == command.DrawnSignatureRequestFingerprint, ct)
                ?? throw new DomainValidationException("Drawn signature evidence admission is missing or changed.");
            if (pending.StoragePath != command.DrawnSignatureStorageKey
                || pending.ContentType != "image/png" || pending.SizeBytes != command.DrawnSignatureFileSize.Value)
                throw new DomainValidationException("Drawn signature evidence does not match its admission.");
            var evidence = new StoredFile
            {
                PortfolioId = request.PortfolioId,
                FileName = pending.FileName,
                FilePath = pending.StoragePath,
                ContentType = pending.ContentType,
                FileSize = pending.SizeBytes,
                EntityType = nameof(SignatureSigner),
                EntityId = signer.Id,
                UploadedAt = occurredAtUtc,
            };
            _db.Add(evidence);
            await context.FlushBusinessAsync(ct);
            signer.DrawnSignatureStoredFileId = evidence.Id;
            pending.State = PendingFileUploadState.Finalized;
            pending.StoredFileId = evidence.Id;
            pending.UpdatedAtUtc = occurredAtUtc;
        }
        else
        {
            signer.DrawnSignatureStoredFileId = null;
        }
        signer.ConsentGivenAtUtc = occurredAtUtc;
        signer.SignedAtUtc = occurredAtUtc;
        signer.UpdatedAtUtc = occurredAtUtc;
        signer.IpAddress = command.IpAddress ?? signer.IpAddress;
        signer.UserAgent = command.UserAgent ?? signer.UserAgent;
        signer.ViewedAtUtc ??= occurredAtUtc;

        var hasOtherUnsignedSigner = await _db.Set<SignatureSigner>()
            .AnyAsync(
                candidate => candidate.SignatureRequestId == request.Id
                    && candidate.Id != signer.Id
                    && candidate.Status != SignatureSignerStatus.Signed,
                ct);
        var allSigned = !hasOtherUnsignedSigner;
        request.Status = allSigned
            ? SignatureRequestStatus.ExecutionPending
            : SignatureRequestStatus.PartiallySigned;

        _db.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignatureSignerId = signer.Id,
            Type = SignatureAuditEventType.Signed,
            PortfolioId = request.PortfolioId,
            OccurredAtUtc = occurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = $"{signer.NameSnapshot} signed ({signer.SignatureType}).",
        });
        StageSignerAndRequestAudits(context, request, signer, "Signer captured an electronic signature.");

        return Applied(signer, request, allSigned);
    }

    public async Task AuthorizeReplayAsync(
        RecordNativeSignatureCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        RecordNativeSignatureCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateSignature(command);

        var signerStillOwned = await _db.Set<SignatureSigner>()
            .AsNoTracking()
            .AnyAsync(signer =>
                signer.TokenHash == command.TokenHash &&
                signer.SignatureRequest != null &&
                (signer.Status == SignatureSignerStatus.Signed ||
                 signer.TokenExpiresAtUtc <= command.OccurredAtUtc ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Completed ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Declined ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Voided),
                ct);
        if (!signerStillOwned)
        {
            throw new UnauthorizedAccessException("The original native signature signer token is unavailable.");
        }
    }

    private static NativeSignerActionResult Missing() => new(
        NativeSignerActionOutcome.NotFound, "This signing link is invalid.", 0, null, null, null,
        SignatureSignerStatus.Pending, SignatureRequestStatus.AwaitingSignatures, false);

    private static NativeSignerActionResult Unavailable(SignatureSigner signer, SignatureRequest request) => new(
        NativeSignerActionOutcome.Expired,
        signer.Status == SignatureSignerStatus.Signed
            ? "You have already signed this document."
            : "This signing request is no longer active.",
        request.Id, request.PublicId, request.LeaseAgreementId, request.LeaseAddendumId,
        signer.Status, request.Status, false);

    private static NativeSignerActionResult Applied(
        SignatureSigner signer,
        SignatureRequest request,
        bool executionRequired) => new(
            NativeSignerActionOutcome.Applied, null, request.Id, request.PublicId,
            request.LeaseAgreementId, request.LeaseAddendumId,
            signer.Status, request.Status, executionRequired);

    internal static void StageSignerAndRequestAudits(
        IAtomicCommandContext context,
        SignatureRequest request,
        SignatureSigner signer,
        string reason)
    {
        context.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureSigner),
            signer.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new
            {
                Status = signer.Status.ToString(),
                SignatureType = signer.SignatureType.ToString(),
                signer.ConsentGivenAtUtc,
                signer.SignedAtUtc,
            }),
            ChangeReason: reason,
            IpAddress: signer.IpAddress));
        context.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureRequest),
            request.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString() }),
            ChangeReason: reason,
            IpAddress: signer.IpAddress));
    }

    internal static void ValidateToken(string tokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        if (tokenHash.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenHash));
        }
    }

    private static void ValidateSignature(RecordNativeSignatureCommand command)
    {
        ValidateToken(command.TokenHash);
        if (command.SignatureType == SignatureSignatureType.Typed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.TypedName);
        }
        else if (command.SignatureType == SignatureSignatureType.Drawn)
        {
            if (!command.DrawnSignaturePendingUploadId.HasValue ||
                string.IsNullOrWhiteSpace(command.DrawnSignatureRequestFingerprint) ||
                string.IsNullOrWhiteSpace(command.DrawnSignatureStorageKey) ||
                command.DrawnSignatureFileSize is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(command.DrawnSignaturePendingUploadId));
            }
        }
    }
}

public sealed class RecordNativeDeclineHandler
    : IAtomicCommandHandler<RecordNativeDeclineCommand, NativeSignerActionResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordNativeDeclineHandler(RentalCommandDbContext db) => _db = db;

    public async Task<NativeSignerActionResult> HandleAsync(
        RecordNativeDeclineCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task<NativeSignerActionResult> ExecuteAsync(
        RecordNativeDeclineCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var target = await _db.Set<SignatureSigner>()
            .Where(signer => signer.TokenHash == command.TokenHash)
            .Select(signer => new { signer.Id, signer.SignatureRequestId })
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            return new NativeSignerActionResult(
                NativeSignerActionOutcome.NotFound, "This signing link is invalid.", 0, null, null, null,
                SignatureSignerStatus.Pending, SignatureRequestStatus.AwaitingSignatures, false);
        }

        await context.AcquireLockAsync("SignatureRequest", target.SignatureRequestId, ct);
        var signer = await _db.Set<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(candidate => candidate.Id == target.Id, ct);
        var request = signer.SignatureRequest!;
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var occurredAtUtc = times.EffectiveNowUtc;
        if (signer.TokenExpiresAtUtc <= securityNowUtc
            || signer.Status is SignatureSignerStatus.Signed or SignatureSignerStatus.Declined
            || request.Status is SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided)
        {
            return new NativeSignerActionResult(
                NativeSignerActionOutcome.Expired,
                "This signing request is no longer active.",
                request.Id, request.PublicId, request.LeaseAgreementId, request.LeaseAddendumId,
                signer.Status, request.Status, false);
        }

        signer.Status = SignatureSignerStatus.Declined;
        signer.DeclinedAtUtc = occurredAtUtc;
        signer.UpdatedAtUtc = occurredAtUtc;
        signer.IpAddress = command.IpAddress ?? signer.IpAddress;
        signer.UserAgent = command.UserAgent ?? signer.UserAgent;
        request.Status = SignatureRequestStatus.Declined;
        request.DeclinedAtUtc = occurredAtUtc;

        _db.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignatureSignerId = signer.Id,
            Type = SignatureAuditEventType.Declined,
            PortfolioId = request.PortfolioId,
            OccurredAtUtc = occurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = string.IsNullOrWhiteSpace(command.Reason)
                ? $"{signer.NameSnapshot} declined to sign."
                : $"{signer.NameSnapshot} declined to sign: {command.Reason.Trim()}",
        });
        RecordNativeSignatureHandler.StageSignerAndRequestAudits(
            context, request, signer, "Signer declined the electronic signature request.");

        return new NativeSignerActionResult(
            NativeSignerActionOutcome.Applied, null, request.Id, request.PublicId,
            request.LeaseAgreementId, request.LeaseAddendumId,
            signer.Status, request.Status, false);
    }

    public async Task AuthorizeReplayAsync(
        RecordNativeDeclineCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        RecordNativeDeclineCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecordNativeSignatureHandler.ValidateToken(command.TokenHash);

        var signerStillOwned = await _db.Set<SignatureSigner>()
            .AsNoTracking()
            .AnyAsync(signer =>
                signer.TokenHash == command.TokenHash &&
                signer.SignatureRequest != null &&
                (signer.Status == SignatureSignerStatus.Declined ||
                 signer.TokenExpiresAtUtc <= command.OccurredAtUtc ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Completed ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Declined ||
                 signer.SignatureRequest.Status == SignatureRequestStatus.Voided),
                ct);
        if (!signerStillOwned)
        {
            throw new UnauthorizedAccessException("The original native decline signer token is unavailable.");
        }
    }
}
