using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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
[Authorize(Roles = "Admin,Manager")]
public class AccountingIntegrationsController : ManagementControllerBase
{
    private readonly AccountingConnectionService _service;
    private readonly ILogger<AccountingIntegrationsController> _logger;

    public AccountingIntegrationsController(
        AccountingConnectionService service,
        ILogger<AccountingIntegrationsController> logger)
    {
        _service = service;
        _logger = logger;
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
    public async Task<ActionResult<StartAccountingConnectResponse>> Connect(string provider, CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        try
        {
            var authorizeUrl = await _service.StartConnectAsync(GetPortfolioId(), parsed, BuildCallbackUrl(), ct);
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
    public async Task<IActionResult> Disconnect(string provider, CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        await _service.DisconnectAsync(GetPortfolioId(), parsed, ct);
        return NoContent();
    }

    /// <summary>Set the per-direction (pull/push) toggles for <paramref name="provider"/>.</summary>
    [HttpPost("{provider}/direction")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Direction(
        string provider,
        [FromBody] SetAccountingDirectionRequest request,
        CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var parsed))
        {
            return BadRequest(new { error = $"Unknown accounting provider '{provider}'." });
        }

        await _service.SetDirectionAsync(GetPortfolioId(), parsed, request.PullEnabled, request.PushEnabled, ct);
        return NoContent();
    }

    private static bool TryParseProvider(string provider, out AccountingProvider parsed) =>
        Enum.TryParse(provider, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    /// <summary>The absolute URL of this controller's callback action, derived from the current request.</summary>
    private string BuildCallbackUrl() =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/integrations/accounting/callback";

    /// <summary>302 to the web settings accounting section with a single result hint query param.</summary>
    private RedirectResult RedirectToSettings(string key, string value) =>
        Redirect($"/settings?{key}={value}#accounting");
}
