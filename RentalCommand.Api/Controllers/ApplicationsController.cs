using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Applications;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Landlord-facing review of rental applications submitted via the public no-login link. All routes
/// use the server-validated workspace context. Approving creates a real Tenant.
/// </summary>
[ApiController]
[Route("api/v1/applications")]
[Produces("application/json")]
public class ApplicationsController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<ApplicationFinanceMutationResult> FeeResultCodec =
        new("application-finance.mutation.v1");
    private readonly IApplicationService _service;
    private readonly IScreeningService _screening;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly IWorkspaceAuthorizationEvaluator _authorization;
    private readonly TimeProvider _timeProvider;

    public ApplicationsController(
        IApplicationService service,
        IScreeningService screening,
        IAtomicUnitOfWork atomic,
        RentalCommandDbContext db,
        IFileStorage files,
        IWorkspaceAuthorizationEvaluator authorization,
        TimeProvider timeProvider)
    {
        _service = service;
        _screening = screening;
        _atomic = atomic;
        _db = db;
        _files = files;
        _authorization = authorization;
        _timeProvider = timeProvider;
    }

    /// <summary>Lists applications in the portfolio, newest first; optionally filtered by <c>?status=</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApplicationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> List(
        [FromQuery] string? status, [FromQuery] int? unitId, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), status, query, unitId, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(ApplicationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationListResponse>> ListPage(
        [FromQuery] string? status, [FromQuery] int? unitId, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), status, query, unitId, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Application not found" }) : Ok(item);
    }

    /// <summary>Corrects landlord-editable details on a submitted/under-review application.</summary>
    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApplicationResponse>> Update(
        int id,
        [FromBody] UpdateApplicationRequest request,
        CancellationToken ct)
    {
        var item = await _service.UpdateAsync(GetPortfolioId(), id, request, GetUserId(), ct);
        return item == null ? NotFound(new { error = "Application not found" }) : Ok(item);
    }

    /// <summary>Streams the original scanned application document.</summary>
    [HttpGet("{id:int}/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetScan(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
        => ServeEntityScanAsync(_db, _files, "Application", id, thumb, ct);

    /// <summary>Approves the application and creates a Tenant from its data.</summary>
    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(ApproveApplicationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        try
        {
            var result = await _service.ApproveAsync(GetPortfolioId(), id, GetUserId(), ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/decline")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decline(int id, [FromBody] DeclineApplicationRequest? body, CancellationToken ct)
    {
        try
        {
            var result = await _service.DeclineAsync(GetPortfolioId(), id, GetUserId(), body?.Reason, ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/withdraw")]
    [ProducesResponseType(typeof(ApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct)
    {
        try
        {
            var result = await _service.WithdrawAsync(GetPortfolioId(), id, GetUserId(), ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, GetUserId(), ct);
        return deleted ? NoContent() : NotFound(new { error = "Application not found" });
    }

    /// <summary>
    /// Records an immutable application-fee collection in the application's pre-tenancy account.
    /// </summary>
    [HttpPost("{id:int}/fee")]
    [ProducesResponseType(typeof(ApplicationFinanceMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RecordFee(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecordApplicationFeeRequest req,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var sessionId, out var accessContextId, out var accessRevision))
            return Task.FromResult<IActionResult>(Forbid());

        return ExecuteFinanceMutation(
            id,
            idempotencyKey,
            key => new RecordApplicationFeeCommand(
                GetPortfolioId(),
                id,
                req.Amount,
                req.Currency.Trim().ToUpperInvariant(),
                req.EffectiveOn,
                req.Method,
                null,
                null,
                ApplicationFinancialEntrySource.Manual,
                null,
                key,
                GetUserId(),
                sessionId,
                accessContextId,
                accessRevision),
            "application-finance.record-fee",
            StatusCodes.Status201Created,
            ct);
    }

    [HttpPost("{id:int}/fee-refunds")]
    [ProducesResponseType(typeof(ApplicationFinanceMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RefundFee(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RefundApplicationFeeRequest req,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var sessionId, out var accessContextId, out var accessRevision))
            return Task.FromResult<IActionResult>(Forbid());

        return ExecuteFinanceMutation(
            id,
            idempotencyKey,
            key => new RefundApplicationFeeCommand(
                GetPortfolioId(),
                id,
                req.CollectionEntryId,
                req.Amount,
                req.EffectiveOn,
                req.Method,
                null,
                null,
                ApplicationFinancialEntrySource.Manual,
                null,
                req.Reason,
                key,
                GetUserId(),
                sessionId,
                accessContextId,
                accessRevision),
            "application-finance.refund-fee",
            StatusCodes.Status201Created,
            ct);
    }

    private async Task<IActionResult> ExecuteFinanceMutation<TCommand>(
        int applicationId,
        string? idempotencyKey,
        Func<string, TCommand> createCommand,
        string commandType,
        int successStatus,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
            return BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });

        var portfolioId = GetPortfolioId();
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        var commandKey = $"{portfolioId}:{applicationId}:{keyDigest}";
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(commandType, commandKey),
                createCommand(commandKey),
                FeeResultCodec,
                ct);
            var value = outcome.Value;
            if (value.Outcome == ApplicationFinanceMutationOutcome.ApplicationNotFound)
                return NotFound(new { error = value.Error });
            if (value.Outcome == ApplicationFinanceMutationOutcome.CollectionNotFound)
                return NotFound(new { error = value.Error });
            if (value.Outcome is ApplicationFinanceMutationOutcome.CurrencyMismatch
                or ApplicationFinanceMutationOutcome.RefundExceedsCollectedAmount)
                return Conflict(new { error = value.Error });
            if (value.Outcome != ApplicationFinanceMutationOutcome.Posted
                || value.AccountId is null || value.EntryId is null || value.EntryType is null
                || value.Direction is null || value.Amount is null || value.Currency is null
                || value.EffectiveOn is null || value.OccurredAtUtc is null)
                return StatusCode(StatusCodes.Status500InternalServerError);

            return StatusCode(successStatus, new ApplicationFinanceMutationResponse
            {
                ApplicationId = value.ApplicationId,
                AccountId = value.AccountId.Value,
                EntryId = value.EntryId.Value,
                RelatedEntryId = value.RelatedEntryId,
                EntryType = value.EntryType.Value,
                Direction = value.Direction.Value,
                Amount = value.Amount.Value,
                Currency = value.Currency,
                EffectiveOn = value.EffectiveOn.Value,
                OccurredAtUtc = value.OccurredAtUtc.Value,
                AccountCreated = value.AccountCreated,
                Replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private bool TryReadAccessContext(
        out Guid sessionId,
        out int accessContextId,
        out long accessRevision)
    {
        sessionId = default;
        accessContextId = default;
        accessRevision = default;
        if (!TryGetActiveAccessContext(out var active)) return false;
        sessionId = active.SessionId;
        accessContextId = active.AccessContextId;
        accessRevision = active.AccessRevision;
        return true;
    }

    [HttpPost("{id:int}/screening/integrated")]
    [ProducesResponseType(typeof(ApplicantScreeningResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StartIntegratedScreening(
        int id, [FromBody] StartIntegratedScreeningRequest body, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        try
        {
            var result = await _screening.StartIntegratedAsync(GetPortfolioId(), id, GetUserId(), body, ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (ConsentRequiredException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ScreeningNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/screening/external")]
    [ProducesResponseType(typeof(ApplicantScreeningResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> TrackExternalScreening(
        int id, [FromBody] TrackExternalScreeningRequest body, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        try
        {
            var result = await _screening.TrackExternalAsync(GetPortfolioId(), id, GetUserId(), body, ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("{id:int}/screening/{screeningId:int}/external")]
    [ProducesResponseType(typeof(ApplicantScreeningResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateExternalScreening(
        int id, int screeningId, [FromBody] UpdateExternalScreeningRequest body, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        try
        {
            var result = await _screening.UpdateExternalAsync(GetPortfolioId(), id, screeningId, body, ct);
            return result == null ? NotFound(new { error = "External screening not found" }) : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:int}/screening/{screeningId:int}/decision")]
    [ProducesResponseType(typeof(ApplicantScreeningResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordScreeningDecision(
        int id, int screeningId, [FromBody] RecordScreeningDecisionRequest body, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        try
        {
            var result = await _screening.RecordDecisionAsync(
                GetPortfolioId(), id, screeningId, GetUserId(), body, ct);
            return result == null ? NotFound(new { error = "Screening not found" }) : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id:int}/screening")]
    [ProducesResponseType(typeof(ScreeningWorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScreening(int id, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        var results = await _screening.GetWorkspaceAsync(GetPortfolioId(), id, ct);
        return results == null ? NotFound(new { error = "Application not found" }) : Ok(results);
    }

    /// <summary>
    /// Generates an FCRA adverse-action (denial) notice PDF for the application, stores it, and
    /// (optionally) emails it to the applicant. Pairs with declining the application.
    /// </summary>
    [HttpPost("{id:int}/adverse-action")]
    [ProducesResponseType(typeof(AdverseActionNoticeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateAdverseAction(
        int id, [FromBody] GenerateAdverseActionRequest body, CancellationToken ct)
    {
        if (!await CanManageScreeningAsync(id, ct)) return Forbid();
        try
        {
            var result = await _screening.GenerateAdverseActionAsync(
                GetPortfolioId(), id, GetUserId(), body, ct);
            return result == null ? NotFound(new { error = "Application not found" }) : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generates/rotates the portfolio's public application token and returns the apply path the
    /// landlord can share. Rotating invalidates any previously shared link.
    /// </summary>
    [HttpPost("link")]
    [ProducesResponseType(typeof(ApplicationLinkResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationLinkResult>> GenerateLink(CancellationToken ct)
    {
        var result = await _service.GenerateLinkAsync(GetPortfolioId(), ct);
        return Ok(result);
    }

    private Task<bool> CanManageScreeningAsync(int applicationId, CancellationToken ct)
    {
        var active = GetActiveAccessContext();
        return _authorization.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingApplicationsManage,
            new RentalApplicationCapabilityAuthorizationTarget(active.PortfolioId, applicationId),
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
    }
}
