using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Admits a Guided Setup manual lease as an in-transaction Reviewing scan draft, then immediately
/// sends that draft through the canonical scan confirmation handler. The lease cascade remains in
/// <see cref="CanonicalLeaseScanConfirmationWriter"/> and is not reimplemented here.
/// </summary>
public sealed class CreateManualLeaseHandler
    : IAtomicCommandHandler<CreateManualLeaseCommand, ConfirmScanDraftResult>
{
    private const string SourceLabel = "Guided Setup manual lease";

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicCommandHandler<ConfirmScanDraftCommand, ConfirmScanDraftResult> _confirmation;

    public CreateManualLeaseHandler(
        RentalCommandDbContext db,
        IAtomicCommandHandler<ConfirmScanDraftCommand, ConfirmScanDraftResult> confirmation)
    {
        _db = db;
        _confirmation = confirmation;
    }

    public async Task<ConfirmScanDraftResult> HandleAsync(
        CreateManualLeaseCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateCommand(command);

        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var extractedFields = JsonSerializer.Serialize(new
        {
            Source = "manual",
            command.Target,
        });
        var draft = new ScanDraft
        {
            PortfolioId = command.PortfolioId,
            FilePath = "manual://guided-setup/lease",
            SourceLabel = SourceLabel,
            CaptureExperience = WorkspaceExperience.Management,
            CaptureAccessContextId = command.AccessContextId,
            CaptureAccessRevision = command.ExpectedAccessRevision,
            CapturePropertyId = Positive(command.Target.PropertyId),
            CaptureUnitId = Positive(command.Target.UnitId),
            TargetEntityType = nameof(ScanConfirmationTargetKind.LeaseAgreement),
            Status = "Reviewing",
            ExtractedFields = extractedFields,
            ModelId = "manual-guided-setup",
            CreatedAt = now,
            ReviewedAt = now,
            ReviewedBy = command.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        _db.Add(draft);
        context.BindSemanticAudit(draft, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(ScanDraft),
            0,
            AuditLogOperation.Created,
            UserId: command.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                Source = SourceLabel,
                command.Target,
            }),
            ChangeReason: "Created a manual lease confirmation draft from Guided Setup."));
        await context.FlushBusinessAsync(ct);

        var confirmation = BuildConfirmationCommand(command, draft, now);
        return await _confirmation.HandleAsync(confirmation, context, ct);
    }

    public async Task AuthorizeReplayAsync(
        CreateManualLeaseCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateCommand(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        var authorizationCommand = new ConfirmScanDraftCommand(
            command.PortfolioId,
            0,
            command.UserId,
            context.BusinessNowUtc,
            "manual-lease-replay",
            new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.LeaseAgreement,
                LeaseAgreement: command.Target),
            AuthSessionId: command.AuthSessionId,
            AccessContextId: command.AccessContextId,
            ExpectedAccessRevision: command.ExpectedAccessRevision,
            DeliveryIdempotencyKey: command.DeliveryIdempotencyKey,
            SourceLabel: SourceLabel);
        await CanonicalLeaseScanConfirmationWriter.AuthorizeAsync(
            authorizationCommand,
            _db,
            ct);
    }

    private static ConfirmScanDraftCommand BuildConfirmationCommand(
        CreateManualLeaseCommand command,
        ScanDraft draft,
        DateTime confirmedAtUtc)
    {
        var capturePropertyId = draft.CapturePropertyId;
        var captureUnitId = draft.CaptureUnitId;
        var fingerprint = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256,
            draft.CaptureAccessContextId,
            draft.CaptureAccessRevision,
            capturePropertyId,
            captureUnitId,
            sourceLabel: draft.SourceLabel);
        return new ConfirmScanDraftCommand(
            command.PortfolioId,
            draft.Id,
            command.UserId,
            confirmedAtUtc,
            fingerprint,
            new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.LeaseAgreement,
                LeaseAgreement: command.Target),
            AuthSessionId: command.AuthSessionId,
            AccessContextId: command.AccessContextId,
            ExpectedAccessRevision: command.ExpectedAccessRevision,
            DeliveryIdempotencyKey: command.DeliveryIdempotencyKey,
            SourceLabel: draft.SourceLabel,
            CaptureContext: new ScanCaptureContextData(
                draft.CaptureExperience,
                draft.CaptureAccessContextId,
                draft.CaptureAccessRevision,
                capturePropertyId,
                captureUnitId,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                draft.SourceLabel));
    }

    private static void ValidateCommand(CreateManualLeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DeliveryIdempotencyKey);
        if (command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentOutOfRangeException(
                nameof(command.DeliveryIdempotencyKey),
                "Delivery idempotency key cannot exceed 200 characters.");
        if (command.PortfolioId <= 0 || command.UserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0)
            throw new UnauthorizedAccessException("The current access envelope is incomplete.");
        if (command.Target.ReviewDisposition != LeaseScanReviewDisposition.NeedsSignatures)
            throw new ScanConfirmationValidationException(
                "A manual lease must use the built-in unsigned agreement confirmation flow.");
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
