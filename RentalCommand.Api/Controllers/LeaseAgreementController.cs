using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Esign;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/lease-managements/{leaseManagementId:int}/agreements")]
[Produces("application/json")]
public sealed class LeaseAgreementController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<LeaseAgreementDraftMutationResult> EditCodec =
        new("lease-agreement.draft.edit.v1");
    private static readonly AtomicJsonResultCodec<LeaseAgreementDraftMutationResult> SuccessorCodec =
        new("lease-agreement.successor-draft.create.v2");
    private static readonly AtomicJsonResultCodec<CancelLeaseAgreementSuccessorDraftResult> CancelDraftCodec =
        new("lease-agreement.successor-draft.cancel.v1");
    private static readonly AtomicJsonResultCodec<IssueLeaseAgreementResult> IssueCodec =
        new("lease-agreement.issue.v1");
    private static readonly AtomicJsonResultCodec<ResendNativeEsignInvitationResult> ResendInvitationCodec =
        new("native-esign.invitation-resend.v1");
    private static readonly AtomicJsonResultCodec<VoidLegalArtifactResult> VoidCodec =
        new("lease-agreement.void.v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly ILeaseManagementQueryService _queryService;
    private readonly IFileStorage _files;
    private readonly ILegalDocumentIssuancePreparationService _issuancePreparations;
    private readonly string _webBaseUrl;

    public LeaseAgreementController(
        IAtomicUnitOfWork atomic,
        ILeaseManagementQueryService queryService,
        IFileStorage files,
        ILegalDocumentIssuancePreparationService issuancePreparations,
        IConfiguration configuration)
    {
        _atomic = atomic;
        _queryService = queryService;
        _files = files;
        _issuancePreparations = issuancePreparations;
        _webBaseUrl = (configuration["App:WebBaseUrl"] ?? "https://localhost:5667").TrimEnd('/');
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LeaseAgreementHistoryPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAgreementHistoryPageResponse>> ListPage(
        int leaseManagementId,
        [FromQuery] LeaseLegalHistoryQuery query,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var page = await _queryService.ListAgreementHistoryPageAsync(access, leaseManagementId, query, ct);
        return page is null
            ? NotFound(new { error = "Lease management relationship not found" })
            : Ok(page);
    }

    [HttpGet("{leaseAgreementId:int}/draft")]
    [ProducesResponseType(typeof(LeaseAgreementDraftDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAgreementDraftDetailResponse>> GetDraft(
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var draft = await _queryService.GetAgreementDraftAsync(
            access, leaseManagementId, leaseAgreementId, ct);
        return draft is null
            ? NotFound(new { error = "Agreement draft not found" })
            : Ok(draft);
    }

    [HttpGet("{leaseAgreementId:int}/signature-progress")]
    [ProducesResponseType(typeof(LeaseAgreementSignatureProgressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAgreementSignatureProgressResponse>> GetSignatureProgress(
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var progress = await _queryService.GetAgreementSignatureProgressAsync(
            access, leaseManagementId, leaseAgreementId, ct);
        return progress is null
            ? NotFound(new { error = "Issued Agreement signature packet not found" })
            : Ok(progress);
    }

    [HttpGet("{sourceAgreementId:int}/effective-addendum-series")]
    [ProducesResponseType(typeof(LeaseAgreementEffectiveAddendumSeriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAgreementEffectiveAddendumSeriesResponse>> GetEffectiveAddendumSeries(
        int leaseManagementId,
        int sourceAgreementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var series = await _queryService.GetEffectiveAddendumSeriesAsync(
            access, leaseManagementId, sourceAgreementId, ct);
        return series is null
            ? NotFound(new { error = "The current lease source document was not found." })
            : Ok(series);
    }

    [HttpGet("{leaseAgreementId:int}/artifacts/{artifactId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadArtifact(
        int leaseManagementId,
        int leaseAgreementId,
        int artifactId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var reference = await _queryService.GetAgreementArtifactAsync(
            access, leaseManagementId, leaseAgreementId, artifactId, ct);
        if (reference is null) return NotFound(new { error = "Agreement artifact not found" });

        Stream stream;
        try
        {
            stream = await _files.DownloadAsync(reference.StorageKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotFound(new { error = "Agreement artifact file not found on storage" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, reference.ContentType, reference.FileName, enableRangeProcessing: true);
    }

    [HttpGet("{leaseAgreementId:int}/source-scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadSourceScan(
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var reference = await _queryService.GetAgreementSourceScanAsync(
            access, leaseManagementId, leaseAgreementId, ct);
        if (reference is null) return NotFound(new { error = "Agreement source scan not found" });

        Stream stream;
        try
        {
            stream = await _files.DownloadAsync(reference.StorageKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotFound(new { error = "Agreement source scan file not found on storage" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, reference.ContentType, reference.FileName, enableRangeProcessing: true);
    }

    [HttpPatch("{leaseAgreementId:int}/draft")]
    [ProducesResponseType(typeof(LeaseAgreementDraftMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> EditDraft(
        int leaseManagementId,
        int leaseAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] EditLeaseAgreementDraftRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        if (request.TermType is null || request.TermsPayload.ValueKind != JsonValueKind.Object
            || request.Signers.Any(signer => signer.SignerRole is null))
        {
            return BadRequest(new { error = "Lease term, lease details, and every signer role are required." });
        }
        var command = new EditLeaseAgreementDraftCommand(
            envelope.PortfolioId, leaseManagementId, leaseAgreementId, request.DraftRevision,
            request.AgreementNumber, request.TermType.Value, request.TermStartOn, request.TermEndOn,
            request.GoverningFromOn, request.BaseRentAmount, request.RentDueDay,
            request.SecurityDepositObligation, request.LateFeeAmount, request.GracePeriodDays,
            request.TermsSchemaVersion, request.TermsPayload.GetRawText(), request.DocumentTemplateId,
            request.Signers.Select(signer =>
                new LeaseAgreementDraftSignerInput(signer.LeaseManagementPartyId, signer.TenantId,
                    signer.SignerRole!.Value, signer.NameSnapshot, signer.EmailSnapshot,
                    signer.SigningOrder, signer.IsRequired)).ToArray(), envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            $"agreement-draft-edit:{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{envelope.KeyDigest}");
        return await Execute("lease-agreement.draft.edit",
            $"{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{envelope.KeyDigest}",
            command, EditCodec, StatusCodes.Status200OK, ct);
    }

    [HttpPost("{sourceAgreementId:int}/successor-drafts")]
    [HttpPost("{sourceAgreementId:int}/{operation:regex(^(correct|restatement|renew|month-to-month)$)}")]
    [ProducesResponseType(typeof(LeaseAgreementDraftMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateSuccessorDraft(
        int leaseManagementId,
        int sourceAgreementId,
        string? operation,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateLeaseAgreementSuccessorDraftRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        var routeChangeType = operation switch
        {
            "correct" => RentalCommand.Core.Enums.LeaseAgreementChangeType.Correction,
            "restatement" => RentalCommand.Core.Enums.LeaseAgreementChangeType.Restatement,
            "renew" => RentalCommand.Core.Enums.LeaseAgreementChangeType.Renewal,
            "month-to-month" => RentalCommand.Core.Enums.LeaseAgreementChangeType.MonthToMonth,
            _ => request.ChangeType,
        };
        request.ChangeType = routeChangeType;
        if (request.ChangeType is null || request.AddendumDecisions.Any(item => item.Decision is null)
            || (request.ChangeType == RentalCommand.Core.Enums.LeaseAgreementChangeType.Correction
                && (string.IsNullOrWhiteSpace(request.CorrectionReason)
                    || request.CorrectionReason.Trim().Length > 1000))
            || (request.ChangeType != RentalCommand.Core.Enums.LeaseAgreementChangeType.Correction
                && !string.IsNullOrWhiteSpace(request.CorrectionReason)))
        {
            return BadRequest(new
            {
                error = "ChangeType, every Addendum decision, and a CorrectionReason for corrections are required.",
            });
        }
        var command = new CreateLeaseAgreementSuccessorDraftCommand(
            envelope.PortfolioId, leaseManagementId, sourceAgreementId, request.ChangeType.Value,
            request.TermStartOn, request.TermEndOn, request.GoverningFromOn, request.CorrectionReason,
            request.DocumentTemplateId,
            request.AddendumDecisions.Select(item => new LeaseRenewalAddendumDecisionInput(
                item.SourceAddendumSeriesPublicId, item.Decision!.Value)).ToArray(),
            envelope.UserId, envelope.SessionId,
            envelope.AccessContextId, envelope.AccessRevision,
            $"agreement-successor:{envelope.PortfolioId}:{leaseManagementId}:{sourceAgreementId}:{envelope.KeyDigest}");
        return await Execute("lease-agreement.successor-draft.create",
            $"{envelope.PortfolioId}:{leaseManagementId}:{sourceAgreementId}:{envelope.KeyDigest}",
            command, SuccessorCodec, StatusCodes.Status201Created, ct);
    }

    [HttpPost("{sourceAgreementId:int}/issued-replacement-draft")]
    [ProducesResponseType(typeof(LeaseAgreementDraftMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReplaceIssuedAgreementWithDraft(
        int leaseManagementId,
        int sourceAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReplaceIssuedAgreementWithDraftRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.ReissueReason)
            || request.ReissueReason.Trim().Length > 1000
            || (request.VoidNote?.Trim().Length ?? 0) > 2000)
        {
            return BadRequest(new
            {
                error = "ReissueReason is required and cannot exceed 1,000 characters; VoidNote cannot exceed 2,000 characters.",
            });
        }

        var command = new ReplaceIssuedAgreementWithDraftCommand(
            envelope.PortfolioId,
            leaseManagementId,
            sourceAgreementId,
            request.VoidNote,
            request.ReissueReason,
            envelope.UserId,
            envelope.SessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"agreement-issued-replacement:{envelope.PortfolioId}:{leaseManagementId}:{sourceAgreementId}:{envelope.KeyDigest}");
        return await Execute(
            "lease-agreement.issued-replacement-draft.create",
            $"{envelope.PortfolioId}:{leaseManagementId}:{sourceAgreementId}:{envelope.KeyDigest}",
            command,
            SuccessorCodec,
            StatusCodes.Status201Created,
            ct);
    }

    [HttpPost("{leaseAgreementId:int}/cancel-draft")]
    [ProducesResponseType(typeof(CancelLeaseAgreementSuccessorDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CancelSuccessorDraft(
        int leaseManagementId,
        int leaseAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CancelLeaseAgreementSuccessorDraftRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.CancellationReason)
            || request.CancellationReason.Trim().Length > 1000)
        {
            return BadRequest(new { error = "CancellationReason is required and cannot exceed 1000 characters." });
        }

        var command = new CancelLeaseAgreementSuccessorDraftCommand(
            envelope.PortfolioId,
            leaseManagementId,
            leaseAgreementId,
            request.CancellationReason,
            envelope.UserId,
            envelope.SessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"agreement-successor-cancel:{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{envelope.KeyDigest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-agreement.successor-draft.cancel",
                    $"{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{envelope.KeyDigest}"),
                command,
                CancelDraftCodec,
                ct);
            return outcome.Value.Outcome switch
            {
                CancelLeaseAgreementSuccessorDraftOutcome.Canceled
                    when outcome.Value.DraftCanceledAtUtc.HasValue
                        && outcome.Value.DraftCanceledByUserId.HasValue
                        && outcome.Value.DraftCancellationReason is not null => Ok(
                        new CancelLeaseAgreementSuccessorDraftResponse(
                            outcome.Value.LeaseManagementId,
                            outcome.Value.LeaseAgreementId,
                            outcome.Value.DraftCanceledAtUtc.Value,
                            outcome.Value.DraftCanceledByUserId.Value,
                            outcome.Value.DraftCancellationReason,
                            outcome.Disposition == AtomicCommandDisposition.Replayed)),
                CancelLeaseAgreementSuccessorDraftOutcome.AlreadyCanceled =>
                    Conflict(new { error = outcome.Value.Error }),
                CancelLeaseAgreementSuccessorDraftOutcome.NotSuccessorDraft
                    or CancelLeaseAgreementSuccessorDraftOutcome.IssuedOrExecuted =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseAgreementId:int}/issuance-preparations")]
    [ProducesResponseType(typeof(LegalDocumentIssuancePreparationResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> PrepareIssuance(
        int leaseManagementId,
        int leaseAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PrepareLegalDocumentIssuanceRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        try
        {
            var prepared = await _issuancePreparations.PrepareAgreementAsync(
                new WorkspaceReadScope(envelope.PortfolioId, envelope.UserId, envelope.SessionId,
                    envelope.AccessContextId, envelope.AccessRevision),
                leaseManagementId,
                leaseAgreementId,
                request.DraftRevision,
                envelope.KeyDigest,
                ct);
            return StatusCode(StatusCodes.Status201Created,
                LegalDocumentIssuancePreparationResponse.From(prepared));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DomainValidationException exception) { return Conflict(new { error = exception.Message }); }
        catch (RentalCommand.Data.Documents.UploadOperationConflictException exception)
        { return Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseAgreementId:int}/issue")]
    [ProducesResponseType(typeof(IssueLeaseAgreementResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Issue(
        int leaseManagementId,
        int leaseAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] IssueLeaseAgreementRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        var signerIds = await _queryService.ListAuthorizedAgreementIssueSignerIdsAsync(
            new LeaseManagementReadContext(envelope.PortfolioId, envelope.UserId, envelope.SessionId,
                envelope.AccessContextId, envelope.AccessRevision),
            leaseManagementId,
            leaseAgreementId,
            ct);
        if (signerIds.Count == 0)
            return UnprocessableEntity(new { error = "The Agreement is unavailable or has no signer snapshot." });
        var command = new IssueLeaseAgreementCommand(
            request.PendingUploadId, request.DocumentSourceVersionId, request.IssuanceFingerprint, envelope.PortfolioId,
            leaseManagementId, leaseAgreementId, request.DraftRevision, envelope.KeyDigest,
            request.Subject, request.StorageKey, request.FileName, request.FileSize,
            request.ContentSha256, _webBaseUrl,
            signerIds.Select(id => new NativeEsignSignerCommand(id)).ToArray(), envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("lease-agreement.issue",
                    $"{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{envelope.KeyDigest}"),
                command, IssueCodec, ct);
            return StatusCode(StatusCodes.Status201Created, new IssueLeaseAgreementResponse(
                outcome.Value.PublicId, outcome.Value.LeaseManagementId, outcome.Value.LeaseAgreementId,
                outcome.Value.SignatureRequestId, outcome.Value.IssuedArtifactId,
                outcome.Disposition == AtomicCommandDisposition.Replayed));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DomainValidationException exception) { return Conflict(new { error = exception.Message }); }
    }

    [HttpPost("{leaseAgreementId:int}/signers/{leaseAgreementSignerId:int}/resend-invitation")]
    [ProducesResponseType(typeof(ResendNativeEsignInvitationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResendInvitation(
        int leaseManagementId,
        int leaseAgreementId,
        int leaseAgreementSignerId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        var command = new ResendNativeEsignInvitationCommand(
            envelope.PortfolioId,
            leaseManagementId,
            NativeEsignInvitationParentKind.LeaseAgreement,
            leaseAgreementId,
            leaseAgreementSignerId,
            envelope.UserId,
            envelope.SessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            envelope.KeyDigest);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-agreement.esign-invitation.resend",
                    $"{envelope.PortfolioId}:{leaseManagementId}:{leaseAgreementId}:{leaseAgreementSignerId}:{envelope.KeyDigest}"),
                command,
                ResendInvitationCodec,
                ct);
            return Ok(new ResendNativeEsignInvitationResponse(
                outcome.Value.SignatureRequestId,
                outcome.Value.SignatureSignerId,
                outcome.Disposition == AtomicCommandDisposition.Replayed));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DomainValidationException exception) { return Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseAgreementId:int}/void")]
    public async Task<IActionResult> Void(
        int leaseManagementId,
        int leaseAgreementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] VoidLegalArtifactRequest request,
        CancellationToken ct)
    {
        if (!TryPrepare(idempotencyKey, out var envelope, out var error)) return error!;
        var command = new VoidLeaseAgreementCommand(envelope.PortfolioId, leaseManagementId,
            leaseAgreementId, request.VoidReasonCode, request.VoidNote, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            $"agreement-void:{envelope.PortfolioId}:{leaseAgreementId}:{envelope.KeyDigest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity("lease-agreement.void",
                $"{envelope.PortfolioId}:{leaseAgreementId}:{envelope.KeyDigest}"), command, VoidCodec, ct);
            return outcome.Value.Outcome == VoidLegalArtifactOutcome.Voided
                ? Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed })
                : Conflict(new { error = outcome.Value.Error });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private async Task<IActionResult> Execute<TCommand>(
        string commandType,
        string identity,
        TCommand command,
        AtomicJsonResultCodec<LeaseAgreementDraftMutationResult> codec,
        int successStatus,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(commandType, identity), command, codec, ct);
            if (outcome.Value.Outcome == LeaseAgreementDraftMutationOutcome.Applied)
            {
                return StatusCode(successStatus, LeaseAgreementDraftMutationResponse.FromResult(
                    outcome.Value, outcome.Disposition == AtomicCommandDisposition.Replayed));
            }
            return outcome.Value.Outcome switch
            {
                LeaseAgreementDraftMutationOutcome.StaleDraftRevision
                    or LeaseAgreementDraftMutationOutcome.DraftNotEditable
                    or LeaseAgreementDraftMutationOutcome.SourceAgreementNotCurrent
                    or LeaseAgreementDraftMutationOutcome.SourceAgreementNotRecoverable =>
                    Conflict(new { error = outcome.Value.Error }),
                LeaseAgreementDraftMutationOutcome.InvalidTerms
                    or LeaseAgreementDraftMutationOutcome.InvalidSigners
                    or LeaseAgreementDraftMutationOutcome.InvalidTemplate
                    or LeaseAgreementDraftMutationOutcome.InvalidSuccessorType
                    or LeaseAgreementDraftMutationOutcome.InvalidAddendumDecisions =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (JsonException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private bool TryPrepare(string? idempotencyKey, out Envelope envelope, out IActionResult? error)
    {
        envelope = default;
        error = null;
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        {
            error = BadRequest(new { error = "A request key is required and cannot exceed 200 characters." });
            return false;
        }
        if (!TryGetActiveAccessContext(out var active))
        {
            error = Forbid();
            return false;
        }
        envelope = new(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
        return true;
    }

    private readonly record struct Envelope(int PortfolioId, int UserId, Guid SessionId,
        int AccessContextId, long AccessRevision, string KeyDigest);
}
