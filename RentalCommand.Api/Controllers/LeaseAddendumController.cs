using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/lease-managements/{leaseManagementId:int}/addenda")]
[Produces("application/json")]
public sealed class LeaseAddendumController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<LeaseAddendumDraftMutationResult> DraftCodec =
        new("lease-addendum.draft.mutation.v1");
    private static readonly AtomicJsonResultCodec<IssueLeaseAddendumResult> IssueCodec =
        new("lease-addendum.issue.v1");
    private static readonly AtomicJsonResultCodec<VoidLegalArtifactResult> VoidCodec =
        new("lease-addendum.void.v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly RentalCommandDbContext _db;
    private readonly ILeaseManagementQueryService _queryService;
    private readonly IFileStorage _files;
    private readonly ILegalDocumentIssuancePreparationService _issuancePreparations;
    private readonly string _webBaseUrl;

    public LeaseAddendumController(
        IAtomicUnitOfWork atomic,
        RentalCommandDbContext db,
        ILeaseManagementQueryService queryService,
        IFileStorage files,
        ILegalDocumentIssuancePreparationService issuancePreparations,
        IConfiguration configuration)
    {
        _atomic = atomic;
        _db = db;
        _queryService = queryService;
        _files = files;
        _issuancePreparations = issuancePreparations;
        _webBaseUrl = (configuration["App:WebBaseUrl"] ?? "https://localhost:5667").TrimEnd('/');
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LeaseAddendumHistoryPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAddendumHistoryPageResponse>> ListPage(
        int leaseManagementId,
        [FromQuery] LeaseLegalHistoryQuery query,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var page = await _queryService.ListAddendumHistoryPageAsync(access, leaseManagementId, query, ct);
        return page is null
            ? NotFound(new { error = "Lease management relationship not found" })
            : Ok(page);
    }

    [HttpGet("eligible-base-agreements/page")]
    [ProducesResponseType(typeof(LeaseAddendumEligibleBaseAgreementPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAddendumEligibleBaseAgreementPageResponse>> ListEligibleBaseAgreements(
        int leaseManagementId,
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var page = await _queryService.ListAddendumEligibleBaseAgreementsAsync(
            access, leaseManagementId, query, ct);
        return page is null
            ? NotFound(new { error = "Lease management relationship not found" })
            : Ok(page);
    }

    [HttpGet("signer-candidates")]
    [ProducesResponseType(typeof(LeaseAddendumSignerCandidatesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAddendumSignerCandidatesResponse>> GetSignerCandidates(
        int leaseManagementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var candidates = await _queryService.GetAddendumSignerCandidatesAsync(
            access, leaseManagementId, ct);
        return candidates is null
            ? NotFound(new { error = "Lease management relationship not found" })
            : Ok(candidates);
    }

    [HttpGet("{leaseAddendumId:int}/draft")]
    [ProducesResponseType(typeof(LeaseAddendumDraftDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseAddendumDraftDetailResponse>> GetDraft(
        int leaseManagementId,
        int leaseAddendumId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var draft = await _queryService.GetAddendumDraftAsync(
            access, leaseManagementId, leaseAddendumId, ct);
        return draft is null
            ? NotFound(new { error = "Addendum draft not found" })
            : Ok(draft);
    }

    [HttpGet("{leaseAddendumId:int}/artifacts/{artifactId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadArtifact(
        int leaseManagementId,
        int leaseAddendumId,
        int artifactId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        var reference = await _queryService.GetAddendumArtifactAsync(
            access, leaseManagementId, leaseAddendumId, artifactId, ct);
        if (reference is null) return NotFound(new { error = "Addendum artifact not found" });

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
            return NotFound(new { error = "Addendum artifact file not found on storage" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, reference.ContentType, reference.FileName, enableRangeProcessing: true);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateLeaseAddendumDraftRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        if (!TryInputs(request, out var signers, out var effects, out error)) return error!;
        var command = new CreateLeaseAddendumDraftCommand(envelope.PortfolioId, leaseManagementId,
            request.BaseAgreementId, request.AddendumNumber, request.Purpose!.Value,
            request.EffectiveFromOn, request.EffectiveThroughOn, request.TermsSchemaVersion,
            request.TermsPayload.GetRawText(), request.DocumentTemplateId,
            signers!, effects!, envelope.UserId, envelope.SessionId, envelope.AccessContextId,
            envelope.AccessRevision, $"addendum-create:{envelope.PortfolioId}:{leaseManagementId}:{envelope.KeyDigest}");
        return await ExecuteDraft("lease-addendum.draft.create",
            $"{envelope.PortfolioId}:{leaseManagementId}:{envelope.KeyDigest}", command,
            StatusCodes.Status201Created, ct);
    }

    [HttpPatch("{leaseAddendumId:int}/draft")]
    public async Task<IActionResult> Edit(int leaseManagementId, int leaseAddendumId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] EditLeaseAddendumDraftRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        if (!TryInputs(request, out var signers, out var effects, out error)) return error!;
        var command = new EditLeaseAddendumDraftCommand(envelope.PortfolioId, leaseManagementId,
            leaseAddendumId, request.DraftRevision, request.AddendumNumber, request.Purpose!.Value,
            request.EffectiveFromOn, request.EffectiveThroughOn, request.TermsSchemaVersion,
            request.TermsPayload.GetRawText(), request.DocumentTemplateId,
            signers!, effects!, envelope.UserId, envelope.SessionId, envelope.AccessContextId,
            envelope.AccessRevision, $"addendum-edit:{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}");
        return await ExecuteDraft("lease-addendum.draft.edit",
            $"{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}", command,
            StatusCodes.Status200OK, ct);
    }

    [HttpPost("{sourceAddendumId:int}/correct")]
    public async Task<IActionResult> Correct(int leaseManagementId, int sourceAddendumId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CorrectLeaseAddendumRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        var command = new CorrectLeaseAddendumDraftCommand(envelope.PortfolioId, leaseManagementId,
            sourceAddendumId, request.SupersessionEffectiveOn, envelope.UserId, envelope.SessionId,
            envelope.AccessContextId, envelope.AccessRevision,
            $"addendum-correct:{envelope.PortfolioId}:{sourceAddendumId}:{envelope.KeyDigest}");
        return await ExecuteDraft("lease-addendum.draft.correct",
            $"{envelope.PortfolioId}:{sourceAddendumId}:{envelope.KeyDigest}", command,
            StatusCodes.Status201Created, ct);
    }

    [HttpPost("{leaseAddendumId:int}/issuance-preparations")]
    [ProducesResponseType(typeof(LegalDocumentIssuancePreparationResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> PrepareIssuance(
        int leaseManagementId,
        int leaseAddendumId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PrepareLegalDocumentIssuanceRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        try
        {
            var prepared = await _issuancePreparations.PrepareAddendumAsync(
                new WorkspaceReadScope(envelope.PortfolioId, envelope.UserId, envelope.SessionId,
                    envelope.AccessContextId, envelope.AccessRevision),
                leaseManagementId,
                leaseAddendumId,
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

    [HttpPost("{leaseAddendumId:int}/issue")]
    public async Task<IActionResult> Issue(int leaseManagementId, int leaseAddendumId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] IssueLeaseAddendumRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        var signerIds = await _db.LeaseAddendumSigners.AsNoTracking()
            .Where(item => item.PortfolioId == envelope.PortfolioId && item.LeaseAddendumId == leaseAddendumId)
            .OrderBy(item => item.SigningOrder).Select(item => item.Id).ToArrayAsync(ct);
        if (signerIds.Length == 0) return UnprocessableEntity(new { error = "The Addendum has no signer snapshot." });
        var command = new IssueLeaseAddendumCommand(request.PendingUploadId,
            request.DocumentSourceVersionId, request.IssuanceFingerprint,
            envelope.PortfolioId, leaseManagementId, leaseAddendumId, request.DraftRevision,
            $"addendum-issue:{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}", request.Subject,
            request.StorageKey, request.FileName, request.FileSize, request.ContentSha256,
            _webBaseUrl, signerIds.Select(id => new NativeEsignAddendumSignerCommand(id)).ToArray(),
            envelope.UserId, envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision);
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity("lease-addendum.issue",
                $"{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}"), command, IssueCodec, ct);
            return StatusCode(StatusCodes.Status201Created, new IssueLeaseAddendumResponse(outcome.Value.PublicId,
                outcome.Value.LeaseManagementId, outcome.Value.LeaseAddendumId, outcome.Value.SignatureRequestId,
                outcome.Value.IssuedArtifactId, outcome.Disposition == AtomicCommandDisposition.Replayed));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DomainValidationException exception) { return Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseAddendumId:int}/void")]
    public async Task<IActionResult> Void(int leaseManagementId, int leaseAddendumId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] VoidLegalArtifactRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var error)) return error!;
        var command = new VoidLeaseAddendumCommand(envelope.PortfolioId, leaseManagementId,
            leaseAddendumId, request.VoidReasonCode, request.VoidNote, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            $"addendum-void:{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity("lease-addendum.void",
                $"{envelope.PortfolioId}:{leaseAddendumId}:{envelope.KeyDigest}"), command, VoidCodec, ct);
            return outcome.Value.Outcome == VoidLegalArtifactOutcome.Voided
                ? Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed })
                : Conflict(new { error = outcome.Value.Error });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private async Task<IActionResult> ExecuteDraft<TCommand>(string type, string id, TCommand command,
        int successStatus, CancellationToken ct) where TCommand : notnull, IAtomicCommandData
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(type, id), command, DraftCodec, ct);
            return outcome.Value.Outcome == LeaseAddendumDraftMutationOutcome.Applied
                ? StatusCode(successStatus, LeaseAddendumDraftMutationResponse.From(outcome.Value,
                    outcome.Disposition == AtomicCommandDisposition.Replayed))
                : outcome.Value.Outcome is LeaseAddendumDraftMutationOutcome.StaleDraftRevision
                    or LeaseAddendumDraftMutationOutcome.DraftNotEditable
                    or LeaseAddendumDraftMutationOutcome.SourceAddendumNotEligible
                    ? Conflict(new { error = outcome.Value.Error })
                    : UnprocessableEntity(new { error = outcome.Value.Error });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (JsonException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private bool TryInputs(CreateLeaseAddendumDraftRequest request,
        out LeaseAddendumDraftSignerInput[]? signers, out LeaseAddendumFinancialEffectInput[]? effects,
        out IActionResult? error)
    {
        signers = null; effects = null; error = null;
        if (request.Purpose is null || request.TermsPayload.ValueKind != JsonValueKind.Object
            || request.Signers.Any(item => item.SignerRole is null)
            || request.FinancialEffects.Any(item => item.EffectType is null))
        {
            error = BadRequest(new { error = "Purpose, object TermsPayload, signer roles, and effect types are required." });
            return false;
        }
        signers = request.Signers.Select(item => new LeaseAddendumDraftSignerInput(
            item.LeaseManagementPartyId, item.TenantId, item.SignerRole!.Value, item.NameSnapshot,
            item.EmailSnapshot, item.SigningOrder, item.IsRequired)).ToArray();
        effects = request.FinancialEffects.Select(item => new LeaseAddendumFinancialEffectInput(
            item.EffectType!.Value, item.Amount, item.Currency, item.ChargeCode, item.EffectiveFromOn,
            item.EffectiveThroughOn, item.DueOn, item.Description)).ToArray();
        return true;
    }

    private bool TryEnvelope(string? key, out Envelope envelope, out IActionResult? error)
    {
        envelope = default; error = null;
        var normalized = key?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        { error = BadRequest(new { error = "A valid Idempotency-Key is required." }); return false; }
        if (!TryGetActiveAccessContext(out var active))
        { error = Forbid(); return false; }
        envelope = new(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
        return true;
    }

    private readonly record struct Envelope(int PortfolioId, int UserId, Guid SessionId,
        int AccessContextId, long AccessRevision, string KeyDigest);
}
