using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

/// <summary>Atomically records at most one first-view fact for a native signing db.</summary>
public sealed class RecordNativeEsignViewHandler
    : IAtomicCommandHandler<RecordNativeEsignViewCommand, RecordNativeEsignViewResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordNativeEsignViewHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordNativeEsignViewResult> HandleAsync(
        RecordNativeEsignViewCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task<RecordNativeEsignViewResult> ExecuteAsync(
        RecordNativeEsignViewCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var target = await _db.Set<SignatureSigner>()
            .Where(signer => signer.TokenHash == command.TokenHash)
            .Select(signer => new { signer.Id, signer.SignatureRequestId })
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.NotFound,
                "This signing link is invalid.",
                0);
        }

        await context.AcquireLockAsync(
            "SignatureRequest",
            target.SignatureRequestId,
            ct);

        // Re-read after obtaining the aggregate lock so a concurrent sign/decline decides first.
        var signer = await _db.Set<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(
                candidate => candidate.Id == target.Id
                    && candidate.SignatureRequestId == target.SignatureRequestId,
                ct);
        var request = signer.SignatureRequest!;
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var occurredAtUtc = times.EffectiveNowUtc;

        if (signer.TokenExpiresAtUtc <= securityNowUtc
            && signer.Status is not (SignatureSignerStatus.Signed or SignatureSignerStatus.Declined)
            && request.Status is not (SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided))
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.Expired,
                "This signing link has expired. Please ask the sender for a new one.",
                request.Id);
        }

        if (signer.Status != SignatureSignerStatus.Pending)
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.Available,
                null,
                request.Id);
        }

        signer.Status = SignatureSignerStatus.Viewed;
        signer.ViewedAtUtc = occurredAtUtc;
        signer.UpdatedAtUtc = occurredAtUtc;
        signer.IpAddress ??= command.IpAddress;
        signer.UserAgent ??= command.UserAgent;

        var requestAdvanced = request.Status == SignatureRequestStatus.AwaitingSignatures;
        if (requestAdvanced)
        {
            request.Status = SignatureRequestStatus.Viewed;
        }

        _db.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignatureSignerId = signer.Id,
            Type = SignatureAuditEventType.Viewed,
            PortfolioId = request.PortfolioId,
            OccurredAtUtc = occurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = $"{signer.NameSnapshot} opened the signing page.",
        });

        context.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureSigner),
            signer.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new
            {
                Status = signer.Status.ToString(),
                signer.ViewedAtUtc,
            }),
            ChangeReason: "Signer opened the native electronic signing page.",
            IpAddress: signer.IpAddress));

        // SignatureSigner and SignatureRequest deliberately are not IAuditable/IPortfolioScoped.
        // Stage both semantic facts directly instead of binding them to the tracked-mutation
        // interceptor, which cannot infer the signer's portfolio and would reject the binding.
        // Keeping the pair unconditional also gives every genuine first view one stable two-row
        // command audit, even when another signer already advanced the request beyond Sent.
        context.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureRequest),
            request.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString() }),
            ChangeReason: requestAdvanced
                ? "The native electronic signature request was first viewed."
                : "A signer first viewed the native electronic signature request.",
            IpAddress: signer.IpAddress));

        return new RecordNativeEsignViewResult(
            NativeEsignViewOutcome.Available,
            null,
            request.Id);
    }

    public async Task AuthorizeReplayAsync(
        RecordNativeEsignViewCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        RecordNativeEsignViewCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecordNativeSignatureHandler.ValidateToken(command.TokenHash);

        var signerStillOwned = await _db.Set<SignatureSigner>()
            .AsNoTracking()
            .AnyAsync(signer =>
                signer.TokenHash == command.TokenHash &&
                signer.SignatureRequest != null &&
                (signer.ViewedAtUtc != null ||
                 signer.Status != SignatureSignerStatus.Pending ||
                 signer.TokenExpiresAtUtc <= command.OccurredAtUtc),
                ct);
        if (!signerStillOwned)
        {
            throw new UnauthorizedAccessException("The original native e-sign view token is unavailable.");
        }
    }
}
