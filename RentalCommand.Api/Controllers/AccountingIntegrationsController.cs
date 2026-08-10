using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Provider-agnostic accounting-integration endpoints — connect a landlord's
/// external accounting system (QuickBooks is provider #1), choose direction(s),
/// and read connection status. The <c>{provider}</c> route segment is parsed to the
/// <see cref="AccountingProvider"/> enum; nothing here names a provider, so a 2nd
/// provider needs zero controller changes (AC-1).
///
/// <para>
/// Phase 1 ships the connection-lifecycle subset only (connect / callback /
/// disconnect / status / direction). The mappings / import / review-queue endpoints
/// are Phase 2.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/integrations/accounting")]
[Produces("application/json")]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.IntegrationsManage)]
public class AccountingIntegrationsController : ManagementControllerBase
{
    private readonly AccountingConnectionService _service;
    private readonly ILogger<AccountingIntegrationsController> _logger;
    private readonly IConfiguration _configuration;

    public AccountingIntegrationsController(
        AccountingConnectionService service,
        ILogger<AccountingIntegrationsController> logger,
        IConfiguration configuration)
    {
        _service = service;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>One status card per available provider (connected or not) for the settings shell.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(IReadOnlyList<AccountingConnectionStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AccountingConnectionStatusResponse>>> Status(CancellationToken ct)
    {
        return Ok(await _service.GetStatusAsync(GetPortfolioId(), ct));
    }

    /// <summary>
    /// Begin a connect flow for <paramref name="provider"/>. Returns the provider's
    /// authorize URL for a full-page browser redirect. 422 when the provider has no
    /// server credentials (fail-closed, AC-8); 400 on an unknown provider.
    /// </summary>
    [HttpPost("{provider}/connect")]
    [ProducesResponseType(typeof(StartAccountingConnectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StartAccountingConnectResponse>> Connect(
        string provider,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 128 characters.",
            });
        }

        try
        {
            var authorizeUrl = await _service.StartConnectAsync(
                GetWorkspaceReadScope(), parsed, BuildCallbackUrl(), operationKey, ct);
            return Ok(new StartAccountingConnectResponse { AuthorizeUrl = authorizeUrl });
        }
        catch (AccountingNotConfiguredException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    /// <summary>
    /// OAuth callback the provider redirects the browser to. Unauthenticated by design:
    /// the redirect carries no Bearer token, so the portfolio + provider come from the
    /// single-use <c>state</c> row, never from a request parameter. Always 302s back to
    /// the web settings page with a result hint (never returns an error body to the browser).
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? realmId,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        // The landlord denied/aborted consent at the provider.
        if (!string.IsNullOrEmpty(error))
        {
            return RedirectToSettings("error", Uri.EscapeDataString(error));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return RedirectToSettings("error", "missing_code_or_state");
        }

        try
        {
            var callback = new AccountingCallback(code, state, realmId, error);
            await _service.CompleteCallbackFromStateAsync(callback, ct);
            return RedirectToSettings("connected", "1");
        }
        catch (Exception ex)
        {
            // Never surface internals to the browser; the web page shows a friendly reconnect prompt.
            _logger.LogWarning(ex, "Accounting OAuth callback failed");
            return RedirectToSettings("error", "connect_failed");
        }
    }

    /// <summary>Disconnect <paramref name="provider"/> for the caller's portfolio (best-effort revoke + local disconnect).</summary>
    [HttpPost("{provider}/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Disconnect(
        string provider,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 128 characters.",
            });
        }

        await _service.DisconnectAsync(GetWorkspaceReadScope(), parsed, operationKey, ct);
        return NoContent();
    }

    /// <summary>Set the per-direction (pull/push) toggles for <paramref name="provider"/>.</summary>
    [HttpPost("{provider}/direction")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Direction(
        string provider,
        [FromBody] SetAccountingDirectionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 128 characters.",
            });
        }

        await _service.SetDirectionAsync(
            GetWorkspaceReadScope(), parsed, request.PullEnabled, request.PushEnabled, operationKey, ct);
        return NoContent();
    }

    /// <summary>
    /// Run a one-time / backfill import for <paramref name="provider"/> over an optional date range.
    /// Idempotent (the ledger gates re-import); returns the per-resource counts.
    /// </summary>
    [HttpPost("{provider}/import")]
    [ProducesResponseType(typeof(RunAccountingImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RunAccountingImportResponse>> Import(
        string provider, [FromBody] RunAccountingImportRequest request, CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        try
        {
            var s = await _service.RunImportAsync(GetPortfolioId(), parsed, request.FromDate, request.ToDate, ct);
            return Ok(new RunAccountingImportResponse
            {
                CustomersMapped = s.CustomersMapped,
                VendorsMapped = s.VendorsMapped,
                AccountsMapped = s.AccountsMapped,
                PaymentsImported = s.PaymentsImported,
                ExpensesImported = s.ExpensesImported,
                NeedsReview = s.NeedsReview,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>The entity mappings (suggested + confirmed) for <paramref name="provider"/> (mapping-review panel).</summary>
    [HttpGet("{provider}/mappings")]
    [ProducesResponseType(typeof(IReadOnlyList<AccountingMappingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<AccountingMappingResponse>>> Mappings(
        string provider,
        [FromQuery] bool? confirmed,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        return Ok(await _service.GetMappingsAsync(GetPortfolioId(), parsed, confirmed, skip, take, ct));
    }

    /// <summary>
    /// Confirm (or hand-create) an entity mapping for <paramref name="provider"/>, then promote any
    /// transactions parked waiting on it. Returns the number of newly-promoted transactions.
    /// </summary>
    [HttpPost("{provider}/mappings/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmMapping(
        string provider, [FromBody] ConfirmAccountingMappingRequest request, CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        try
        {
            return Ok(await _service.ConfirmMappingAsync(GetWorkspaceReadScope(), parsed, request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{provider}/mappings/promotions/{continuationId:guid}/continue")]
    [ProducesResponseType(typeof(ContinueAccountingMappingPromotionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ContinueMappingPromotion(
        string provider,
        Guid continuationId,
        [FromBody] ContinueAccountingMappingPromotionRequest request,
        CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        try
        {
            return Ok(await _service.ContinueMappingPromotionAsync(
                GetWorkspaceReadScope(), parsed, continuationId, request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>The review queue (unmatched / needs-review imported transactions) for <paramref name="provider"/>.</summary>
    [HttpGet("{provider}/review-queue")]
    [ProducesResponseType(typeof(IReadOnlyList<AccountingReviewItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<AccountingReviewItemResponse>>> ReviewQueue(
        string provider,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        return Ok(await _service.GetReviewQueueAsync(GetPortfolioId(), parsed, skip, take, ct));
    }

    private static bool TryParseProvider(string provider, out AccountingProvider parsed) =>
        Enum.TryParse(provider, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    /// <summary>The absolute URL of this controller's callback action, derived from the current request.</summary>
    private string BuildCallbackUrl() =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/integrations/accounting/callback";

    /// <summary>
    /// 302 to the web "Connect your accounting" settings page with a single result hint query param.
    /// The OAuth callback carries no auth and may be hit cross-origin (in local dev the API and web
    /// are different origins), so we redirect to the absolute web base URL rather than a relative path
    /// that would resolve against the API host. Reuses the existing <c>App:WebBaseUrl</c> config the
    /// email-verify and e-sign links already use (same key + dev fallback), so prod inherits the
    /// correct web origin automatically.
    /// </summary>
    private RedirectResult RedirectToSettings(string key, string value)
    {
        var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
        return Redirect($"{webBase}/settings/accounting?{key}={value}");
    }
}
