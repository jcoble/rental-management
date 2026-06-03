using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only banking and reconciliation endpoints. This stores imported/Plaid-synced bank
/// transactions and lets the landlord match them to payments or expenses; it never initiates
/// transfers or writes back to the bank.
/// </summary>
[ApiController]
[Route("api/v1/banking")]
[Produces("application/json")]
[Authorize(Roles = "Admin,Manager")]
public class BankingController : AuthenticatedPortfolioControllerBase
{
    private readonly IBankingService _service;

    public BankingController(IBankingService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(BankingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankingSummaryResponse>> Summary(CancellationToken ct)
    {
        return Ok(await _service.GetSummaryAsync(GetPortfolioId(), ct));
    }

    [HttpGet("connections")]
    [ProducesResponseType(typeof(IReadOnlyList<BankConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BankConnectionResponse>>> Connections(CancellationToken ct)
    {
        return Ok(await _service.ListConnectionsAsync(GetPortfolioId(), ct));
    }

    [HttpGet("plaid/settings")]
    [ProducesResponseType(typeof(PlaidSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaidSettingsResponse>> PlaidSettings(CancellationToken ct)
    {
        return Ok(await _service.GetPlaidSettingsAsync(GetPortfolioId(), ct));
    }

    [HttpPost("plaid/link-token")]
    [ProducesResponseType(typeof(PlaidLinkTokenResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaidLinkTokenResponse>> PlaidLinkToken(
        [FromBody] PlaidLinkTokenRequest? request,
        CancellationToken ct)
    {
        return Ok(await _service.CreatePlaidLinkTokenAsync(GetPortfolioId(), GetUserId(), request?.Platform, ct));
    }

    [HttpPost("plaid/exchange-public-token")]
    [ProducesResponseType(typeof(BankConnectionResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankConnectionResponse>> ExchangePlaidPublicToken(
        [FromBody] ExchangePlaidPublicTokenRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PublicToken) || string.IsNullOrWhiteSpace(request.AccountId))
        {
            return BadRequest(new { error = "Plaid public token and account id are required." });
        }

        return Ok(await _service.ExchangePlaidPublicTokenAsync(GetPortfolioId(), request, ct));
    }

    [HttpPost("connections/{id:int}/sync")]
    [ProducesResponseType(typeof(SyncBankConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SyncBankConnectionResponse>> SyncConnection(int id, CancellationToken ct)
    {
        var synced = await _service.SyncPlaidConnectionAsync(GetPortfolioId(), id, ct);
        return synced == null ? NotFound(new { error = "Plaid bank connection not found" }) : Ok(synced);
    }

    [HttpGet("transactions")]
    [ProducesResponseType(typeof(IReadOnlyList<BankTransactionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BankTransactionResponse>>> Transactions(
        [FromQuery] string? status,
        CancellationToken ct)
    {
        return Ok(await _service.ListTransactionsAsync(GetPortfolioId(), status, ct));
    }

    [HttpPost("transactions/import")]
    [ProducesResponseType(typeof(ImportBankTransactionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportBankTransactionsResponse>> Import(
        [FromBody] ImportBankTransactionsRequest request,
        CancellationToken ct)
    {
        if (request.Transactions.Count == 0)
        {
            return BadRequest(new { error = "At least one bank transaction is required." });
        }

        return Ok(await _service.ImportAsync(GetPortfolioId(), request, ct));
    }

    [HttpPost("transactions/{id:int}/match")]
    [ProducesResponseType(typeof(BankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionResponse>> Match(
        int id,
        [FromBody] MatchBankTransactionRequest request,
        CancellationToken ct)
    {
        var updated = await _service.MatchAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Bank transaction or match target not found" }) : Ok(updated);
    }

    [HttpPost("transactions/{id:int}/clear-match")]
    [ProducesResponseType(typeof(BankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionResponse>> ClearMatch(int id, CancellationToken ct)
    {
        var updated = await _service.ClearMatchAsync(GetPortfolioId(), id, ct);
        return updated == null ? NotFound(new { error = "Bank transaction not found" }) : Ok(updated);
    }
}
