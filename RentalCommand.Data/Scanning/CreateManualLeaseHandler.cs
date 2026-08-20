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

    public Task<ConfirmScanDraftResult> HandleAsync(
        CreateManualLeaseCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        throw ScanDraftWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        CreateManualLeaseCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        throw ScanDraftWriteSupport.RetiredPath();

    public static async Task<ConfirmScanDraftResult> ExecuteAsync(
        RentalCommandDbContext db,
        IScanConfirmationTargetWriter targetWriter,
        CreateManualLeaseCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateCommand(command);

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
        db.Add(draft);
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
        return await ConfirmScanDraftHandler.ExecuteAsync(
            db, targetWriter, confirmation, context, ct);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db,
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
            db,
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

    internal static void Validate(CreateManualLeaseCommand command) => ValidateCommand(command);

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

        ValidateManualTarget(command.Target);

        if (command.Target.ReviewDisposition != LeaseScanReviewDisposition.NeedsSignatures)
            throw new ScanConfirmationValidationException(
                "A manual lease must use the built-in unsigned agreement confirmation flow.");
    }

    private static void ValidateManualTarget(ScanLeaseTargetData target)
    {
        // The command uses zero only as the explicit full-home bootstrap sentinel; the public
        // DTO keeps a supplied PropertyId nullable and rejects zero/negative values at the edge.
        if (target.PropertyId < 0)
            Reject("PropertyId must be positive when supplied.");
        ValidateOptionalPositiveId(target.UnitId, nameof(target.UnitId));
        ValidateOptionalPositiveId(target.TenantId, nameof(target.TenantId));
        ValidateOptionalPositiveId(target.LeaseManagementId, nameof(target.LeaseManagementId));
        ValidateOptionalPositiveId(target.TenantAccountId, nameof(target.TenantAccountId));
        ValidateOptionalPositiveId(target.LeaseAgreementId, nameof(target.LeaseAgreementId));
        ValidateOptionalPositiveId(target.DocumentTemplateId, nameof(target.DocumentTemplateId));

        var leaseNumber = target.LeaseNumber?.Trim();
        if (string.IsNullOrWhiteSpace(leaseNumber))
            Reject("LeaseNumber is required and cannot be blank.");
        if (leaseNumber!.Length > 100)
            Reject("LeaseNumber cannot exceed 100 characters.");

        if (target.StartDate is null)
            Reject("StartDate is required.");
        if (target.EndDate is null)
            Reject("EndDate is required.");
        if (target.EndDate!.Value.Date < target.StartDate!.Value.Date)
            Reject("EndDate cannot be before StartDate.");
        if (target.MonthlyRent is not > 0m)
            Reject("MonthlyRent must be greater than zero.");
        if (target.RentDueDay is not (>= 1 and <= 31))
            Reject("RentDueDay must be between 1 and 31.");
        if (target.SecurityDeposit is < 0m)
            Reject("SecurityDeposit cannot be negative.");
        if (target.LateFee is < 0m)
            Reject("LateFee cannot be negative.");
        if (target.UnitBedrooms is < 0m)
            Reject("UnitBedrooms cannot be negative.");
        if (target.UnitBathrooms is < 0m)
            Reject("UnitBathrooms cannot be negative.");
        if (target.UnitSquareFeet is < 0)
            Reject("UnitSquareFeet cannot be negative.");

        if (!Enum.IsDefined(target.RentTrackingStartMode))
            Reject("RentTrackingStartMode is invalid.");
        if (target.RentTrackingStartMode == RentTrackingStartMode.CustomCutoffDate
            && target.RentTrackingStartOn is null)
        {
            Reject("RentTrackingStartOn is required for a custom cutoff.");
        }
        if (target.RentTrackingStartMode != RentTrackingStartMode.CustomCutoffDate
            && target.RentTrackingStartOn is not null)
        {
            Reject("RentTrackingStartOn is only valid for a custom cutoff.");
        }
        if (target.GracePeriodDays is < 0 or > 31)
            Reject("GracePeriodDays must be between 0 and 31.");
        if (target.TermsSchemaVersion <= 0)
            Reject("TermsSchemaVersion must be positive.");
        ValidateTermsPayload(target.TermsPayload);
    }

    private static void ValidateOptionalPositiveId(int? value, string fieldName)
    {
        if (value is <= 0)
            Reject($"{fieldName} must be positive when supplied.");
    }

    private static void ValidateTermsPayload(string? termsPayload)
    {
        if (string.IsNullOrWhiteSpace(termsPayload))
            return;
        try
        {
            using var document = JsonDocument.Parse(termsPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                Reject("TermsPayload must be a JSON object.");
        }
        catch (JsonException)
        {
            Reject("TermsPayload must be valid JSON.");
        }
    }

    private static void Reject(string message) =>
        throw new ScanConfirmationValidationException($"Manual lease validation failed: {message}");

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
