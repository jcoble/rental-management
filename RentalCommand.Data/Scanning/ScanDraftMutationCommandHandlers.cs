using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

public sealed class RetryScanDraftHandler
    : IAtomicCommandHandler<RetryScanDraftCommand, ScanDraftMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public RetryScanDraftHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ScanDraftMutationResult> HandleAsync(
        RetryScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        await ScanDraftMutationAuthorization.LockAsync(command.AuthSessionId,
            command.AccessContextId, command.PortfolioId, command.DraftId, context, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanConfirmationPersistence.IsAuthorizedForReviewAsync(_db, context, scope, command.DraftId, now, ct))
            return ScanDraftMutationResults.NotFound(command.DraftId);

        var draft = await _db.Set<ScanDraft>().SingleOrDefaultAsync(candidate =>
            candidate.Id == command.DraftId && candidate.PortfolioId == command.PortfolioId, ct);
        if (draft is null) return ScanDraftMutationResults.NotFound(command.DraftId);
        if (draft.Status != "Failed")
            return new(ScanDraftMutationOutcome.InvalidStatus, command.DraftId);

        draft.Status = "Pending";
        draft.ExtractedFields = null;
        draft.ModelId = null;
        draft.TokensUsed = null;
        draft.CostUsd = null;
        draft.FailureReason = null;
        draft.ReviewedAt = null;
        draft.ReviewedBy = null;
        draft.ConfirmedAt = null;
        draft.ConfirmedEntityId = null;
        draft.ProcessingClaimOwner = null;
        draft.ProcessingClaimToken = null;
        draft.ProcessingClaimExpiresAtUtc = null;
        draft.ProcessingAttemptCount = 0;
        draft.ProcessingLastAttemptAtUtc = null;
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(draft, ScanDraftMutationResults.Audit(
            command.PortfolioId, draft.Id, AuditLogOperation.Updated, command.UserId,
            "Failed scan draft requeued for extraction"));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(ScanDraftMutationResults.DataUpdate(
            command.PortfolioId, draft.Id, "retry", command.DeliveryIdempotencyKey, now));
        return ScanDraftMutationResults.Applied(await ScanDraftMutationResults.SnapshotAsync(
            _db, command.PortfolioId, draft.Id, ct));
    }

    public async Task AuthorizeReplayAsync(
        RetryScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanAuthorizationQueries.IsAuthorizedForReviewAsync(
                _db, context, scope, command.DraftId, now, ct))
            throw new UnauthorizedAccessException("The scan draft is outside the current review scope.");
    }
}

public sealed class CreateVoiceScanDraftHandler
    : IAtomicCommandHandler<CreateVoiceScanDraftCommand, ScanDraftMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CreateVoiceScanDraftHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ScanDraftMutationResult> HandleAsync(
        CreateVoiceScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        await ScanDraftMutationAuthorization.LockAsync(command.AuthSessionId,
            command.AccessContextId, command.PortfolioId, null, context, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanConfirmationPersistence.CanCreateAuthorizedAsync(_db,
                context, scope, command.TargetEntityType, command.CapturePropertyId, now, ct))
            throw new UnauthorizedAccessException("The current assignment cannot create this voice draft.");

        context.UseDatabaseWallClockForAudit(now);
        StoredFile? source = null;
        if (command.SourceFileName is not null)
        {
            source = new StoredFile
            {
                PortfolioId = command.PortfolioId,
                FileName = command.SourceFileName,
                FilePath = command.FilePath,
                ContentType = command.SourceContentType!,
                FileSize = command.SourceFileSize,
                EntityType = nameof(ScanDraft),
                UploadedAt = now,
            };
            _db.Add(source);
            context.BindSemanticAudit(source, new AtomicSemanticAudit(
                command.PortfolioId, nameof(StoredFile), 0, AuditLogOperation.Created,
                command.UserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    source.FileName,
                    source.ContentType,
                    source.FileSize,
                    command.SourceContentSha256,
                }),
                ChangeReason: "Voice note source uploaded"));
            await context.FlushBusinessAsync(ct);
        }

        var draft = new ScanDraft
        {
            PortfolioId = command.PortfolioId,
            FilePath = command.FilePath,
            SourceStoredFileId = source?.Id,
            SourceContentSha256 = command.SourceContentSha256,
            TargetEntityType = command.TargetEntityType,
            Status = "Reviewing",
            ExtractedFields = command.ExtractedFieldsJson,
            ModelId = command.ModelId,
            TokensUsed = command.TokensUsed,
            CaptureAccessContextId = command.AccessContextId,
            CaptureAccessRevision = command.ExpectedAccessRevision,
            CapturePropertyId = command.CapturePropertyId,
            CaptureExperience = WorkspaceExperience.Management,
            CreatedAt = now,
            ReviewedAt = now,
        };
        _db.Add(draft);
        context.BindSemanticAudit(draft, ScanDraftMutationResults.Audit(
            command.PortfolioId, 0, AuditLogOperation.Created, command.UserId,
            "Voice note classified into a review draft"));
        await context.FlushBusinessAsync(ct);

        if (source is not null)
        {
            source.EntityId = draft.Id;
            context.BindSemanticAudit(source, new AtomicSemanticAudit(
                command.PortfolioId, nameof(StoredFile), source.Id, AuditLogOperation.Updated,
                command.UserId,
                NewValues: JsonSerializer.Serialize(new { source.EntityType, EntityId = draft.Id }),
                ChangeReason: "Voice source linked to scan draft"));
            await context.FlushBusinessAsync(ct);
        }

        context.StageOutbox(ScanDraftMutationResults.DataUpdate(
            command.PortfolioId, draft.Id, "create", command.DeliveryIdempotencyKey, now));
        return ScanDraftMutationResults.Applied(await ScanDraftMutationResults.SnapshotAsync(
            _db, command.PortfolioId, draft.Id, ct));
    }

    public async Task AuthorizeReplayAsync(
        CreateVoiceScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanAuthorizationQueries.CanCreateAsync(_db,
                context, scope, command.TargetEntityType, command.CapturePropertyId, now, ct))
            throw new UnauthorizedAccessException("The current assignment cannot create this voice draft.");
    }
}

public sealed class AnswerVoiceScanDraftHandler
    : IAtomicCommandHandler<AnswerVoiceScanDraftCommand, ScanDraftMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AnswerVoiceScanDraftHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ScanDraftMutationResult> HandleAsync(
        AnswerVoiceScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        await ScanDraftMutationAuthorization.LockAsync(command.AuthSessionId,
            command.AccessContextId, command.PortfolioId, command.DraftId, context, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanConfirmationPersistence.IsAuthorizedForReviewAsync(_db, context, scope, command.DraftId, now, ct))
            return ScanDraftMutationResults.NotFound(command.DraftId);
        if (!await AtomicScanConfirmationPersistence.CanCreateAuthorizedAsync(_db,
                context, scope, command.TargetEntityType, command.CapturePropertyId, now, ct))
            throw new UnauthorizedAccessException("The current assignment cannot update this voice draft.");

        var draft = await _db.Set<ScanDraft>().SingleOrDefaultAsync(candidate =>
            candidate.Id == command.DraftId && candidate.PortfolioId == command.PortfolioId, ct);
        if (draft is null) return ScanDraftMutationResults.NotFound(command.DraftId);
        if (draft.Status != "Reviewing")
            return new(ScanDraftMutationOutcome.InvalidStatus, command.DraftId);
        if (!string.Equals(draft.ExtractedFields, command.ExpectedExtractedFieldsJson,
                StringComparison.Ordinal))
            return new(ScanDraftMutationOutcome.Stale, command.DraftId);

        draft.ExtractedFields = command.MergedExtractedFieldsJson;
        draft.TargetEntityType = command.TargetEntityType;
        draft.CapturePropertyId = command.CapturePropertyId;
        draft.ModelId = command.ModelId;
        draft.TokensUsed = command.TokensUsed;
        draft.ReviewedAt = now;
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(draft, ScanDraftMutationResults.Audit(
            command.PortfolioId, draft.Id, AuditLogOperation.Updated, command.UserId,
            "Voice answer merged into review draft"));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(ScanDraftMutationResults.DataUpdate(
            command.PortfolioId, draft.Id, "update", command.DeliveryIdempotencyKey, now));
        return ScanDraftMutationResults.Applied(await ScanDraftMutationResults.SnapshotAsync(
            _db, command.PortfolioId, draft.Id, ct));
    }

    public async Task AuthorizeReplayAsync(
        AnswerVoiceScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanAuthorizationQueries.IsAuthorizedForReviewAsync(
                _db, context, scope, command.DraftId, now, ct)
            || !await AtomicScanAuthorizationQueries.CanCreateAsync(_db,
                context, scope, command.TargetEntityType, command.CapturePropertyId, now, ct))
            throw new UnauthorizedAccessException("The voice draft is outside the current review scope.");
    }
}

public sealed class SetScanDraftPaymentAccountHandler
    : IAtomicCommandHandler<SetScanDraftPaymentAccountCommand, ScanDraftMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public SetScanDraftPaymentAccountHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ScanDraftMutationResult> HandleAsync(
        SetScanDraftPaymentAccountCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        await ScanDraftMutationAuthorization.LockAsync(command.AuthSessionId,
            command.AccessContextId, command.PortfolioId, command.DraftId, context, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanConfirmationPersistence.IsAuthorizedForReviewAsync(_db, context, scope, command.DraftId, now, ct))
            return ScanDraftMutationResults.NotFound(command.DraftId);

        var accountIsAuthorized = await AtomicScanAuthorizationQueries.IsTenantAccountAuthorizedForPaymentReviewAsync(_db,
            context, scope, command.DraftId, command.TenantAccountId, now, ct);
        if (!accountIsAuthorized)
            return ScanDraftMutationResults.NotFound(command.DraftId);

        var draft = await _db.Set<ScanDraft>().SingleOrDefaultAsync(candidate =>
            candidate.Id == command.DraftId && candidate.PortfolioId == command.PortfolioId, ct);
        if (draft is null) return ScanDraftMutationResults.NotFound(command.DraftId);
        if (draft.Status != "Reviewing" || !string.Equals(draft.TargetEntityType, "Payment", StringComparison.Ordinal))
            return new(ScanDraftMutationOutcome.InvalidStatus, command.DraftId);

        draft.CaptureTenantAccountId = command.TenantAccountId;
        draft.ReviewedAt = now;
        draft.ReviewedBy = command.UserId.ToString();
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(draft, ScanDraftMutationResults.Audit(
            command.PortfolioId, draft.Id, AuditLogOperation.Updated, command.UserId,
            "Payment scan draft tenant account selected during review"));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(ScanDraftMutationResults.DataUpdate(
            command.PortfolioId, draft.Id, "update", command.DeliveryIdempotencyKey, now));
        return ScanDraftMutationResults.Applied(await ScanDraftMutationResults.SnapshotAsync(
            _db, command.PortfolioId, draft.Id, ct));
    }

    public async Task AuthorizeReplayAsync(
        SetScanDraftPaymentAccountCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ScanDraftMutationValidation.Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var scope = ScanDraftMutationAuthorization.Scope(command.PortfolioId, command.UserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision);
        if (!await AtomicScanAuthorizationQueries.IsAuthorizedForReviewAsync(
                _db, context, scope, command.DraftId, now, ct)
            || !await AtomicScanAuthorizationQueries.IsTenantAccountAuthorizedForPaymentReviewAsync(_db,
                context, scope, command.DraftId, command.TenantAccountId, now, ct))
        {
            throw new UnauthorizedAccessException("The scan draft or payment account is outside the current review scope.");
        }
    }
}

internal static class ScanDraftMutationAuthorization
{
    internal static WorkspaceReadScope Scope(
        int portfolioId, int userId, Guid sessionId, int accessContextId, long accessRevision) =>
        new(portfolioId, userId, sessionId, accessContextId, accessRevision);

    internal static async Task LockAsync(
        Guid sessionId, int accessContextId, int portfolioId, int? draftId,
        IAtomicCommandContext context, CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", sessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", accessContextId, ct);
        await context.AcquireLockAsync("Portfolio", portfolioId, ct);
        if (draftId is > 0)
            await context.AcquireLockAsync("ScanDraft", draftId.Value, ct);
    }
}

internal static class ScanDraftMutationValidation
{
    internal static void Validate(RetryScanDraftCommand command)
    {
        ValidateCommon(command.PortfolioId, command.UserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        if (command.DraftId <= 0) throw new ArgumentOutOfRangeException(nameof(command.DraftId));
    }

    internal static void Validate(CreateVoiceScanDraftCommand command)
    {
        ValidateCommon(command.PortfolioId, command.UserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetEntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExtractedFieldsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FilePath);
        if (command.CapturePropertyId is <= 0 || command.SourceFileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        if ((command.SourceFileName is null) != (command.SourceContentType is null)
            || command.SourceFileName is null && command.SourceFileSize != 0
            || command.SourceFileName is not null && string.IsNullOrWhiteSpace(command.SourceContentSha256))
            throw new ArgumentException("Voice source metadata is incomplete.", nameof(command));
    }

    internal static void Validate(AnswerVoiceScanDraftCommand command)
    {
        ValidateCommon(command.PortfolioId, command.UserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        if (command.DraftId <= 0 || command.CapturePropertyId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedExtractedFieldsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetEntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.MergedExtractedFieldsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelId);
    }

    internal static void Validate(SetScanDraftPaymentAccountCommand command)
    {
        ValidateCommon(command.PortfolioId, command.UserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, command.DeliveryIdempotencyKey);
        if (command.DraftId <= 0 || command.TenantAccountId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static void ValidateCommon(
        int portfolioId, int userId, Guid sessionId, int accessContextId,
        long accessRevision, string operationKey)
    {
        if (portfolioId <= 0 || userId <= 0 || sessionId == Guid.Empty
            || accessContextId <= 0 || accessRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(operationKey));
    }
}

internal static class ScanDraftMutationResults
{
    internal static ScanDraftMutationResult NotFound(int draftId) =>
        new(ScanDraftMutationOutcome.NotFound, draftId);

    internal static ScanDraftMutationResult Applied(ScanDraftReceiptSnapshot snapshot) =>
        new(ScanDraftMutationOutcome.Applied, snapshot.Id, snapshot);

    internal static AtomicSemanticAudit Audit(
        int portfolioId, int draftId, AuditLogOperation operation, int userId, string reason) =>
        new(portfolioId, nameof(ScanDraft), draftId, operation, userId,
            ChangeReason: reason);

    internal static OutboxMessage DataUpdate(
        int portfolioId, int draftId, string operation, string operationKey, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType = nameof(ScanDraft),
            entityId = draftId,
            operation,
        }),
        IdempotencyKey = $"scan-draft-{operation}:{operationKey}",
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
    };

    internal static Task<ScanDraftReceiptSnapshot> SnapshotAsync(
        RentalCommandDbContext db, int portfolioId, int draftId, CancellationToken ct) =>
        db.Set<ScanDraft>().AsNoTracking()
            .Where(draft => draft.Id == draftId && draft.PortfolioId == portfolioId)
            .Select(draft => new ScanDraftReceiptSnapshot(
                draft.Id,
                draft.PortfolioId,
                draft.FilePath,
                draft.SourceStoredFileId,
                draft.SourceContentSha256,
                draft.SourceLabel,
                draft.CaptureExperience,
                draft.CaptureAccessContextId,
                draft.CaptureAccessRevision,
                draft.CapturePropertyId,
                draft.CaptureUnitId,
                draft.CaptureLeaseManagementId,
                draft.CaptureLeaseAgreementId,
                draft.CaptureTenantAccountId,
                draft.CaptureTenantLedgerEntryId,
                draft.CaptureWorkOrderId,
                draft.CaptureApplicationId,
                draft.CaptureRentalListingId,
                draft.TargetEntityType,
                draft.Status,
                draft.ExtractedFields,
                draft.ModelId,
                draft.TokensUsed,
                draft.CostUsd,
                draft.FailureReason,
                draft.CreatedAt,
                draft.ReviewedAt,
                draft.ReviewedBy,
                draft.ConfirmedAt,
                draft.ConfirmedEntityId))
            .SingleAsync(ct);
}
