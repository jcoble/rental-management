using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only banking and reconciliation endpoints. This stores imported/Plaid-synced bank
/// transactions and lets the landlord match them to tenant-account receipts or expenses; it never initiates
/// transfers or writes back to the bank.
/// </summary>
[ApiController]
[Route("api/v1/banking")]
[Produces("application/json")]
public class BankingController : ManagementControllerBase
{
    private readonly IBankingService _service;

    public BankingController(IBankingService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(BankingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankingSummaryResponse>> Summary(CancellationToken ct)
    {
        return Ok(await _service.GetSummaryAsync(GetPortfolioId(), ct));
    }

    [HttpGet("connections")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(IReadOnlyList<BankConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BankConnectionResponse>>> Connections(CancellationToken ct)
    {
        return Ok(await _service.ListConnectionsAsync(GetPortfolioId(), ct));
    }

    [HttpGet("plaid/settings")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(PlaidSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaidSettingsResponse>> PlaidSettings(CancellationToken ct)
    {
        return Ok(await _service.GetPlaidSettingsAsync(GetPortfolioId(), ct));
    }

    [HttpPost("plaid/link-token")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(PlaidLinkTokenResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaidLinkTokenResponse>> PlaidLinkToken(
        [FromBody] PlaidLinkTokenRequest? request,
        CancellationToken ct)
    {
        return Ok(await _service.CreatePlaidLinkTokenAsync(GetPortfolioId(), GetUserId(), request?.Platform, ct));
    }

    [HttpPost("plaid/exchange-public-token")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(BankConnectionResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankConnectionResponse>> ExchangePlaidPublicToken(
        [FromBody] ExchangePlaidPublicTokenRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientOperationId)
            || string.IsNullOrWhiteSpace(request.PublicToken)
            || string.IsNullOrWhiteSpace(request.AccountId))
        {
            return BadRequest(new { error = "A Plaid request key, public token, and account are required." });
        }

        return Ok(await _service.ExchangePlaidPublicTokenAsync(GetWorkspaceReadScope(), request, ct));
    }

    [HttpPost("connections/{id:int}/sync")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(SyncBankConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SyncBankConnectionResponse>> SyncConnection(int id, CancellationToken ct)
    {
        var synced = await _service.SyncPlaidConnectionAsync(GetPortfolioId(), id, ct);
        return synced == null ? NotFound(new { error = "Plaid bank connection not found" }) : Ok(synced);
    }

    [HttpGet("transactions")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(BankTransactionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankTransactionListResponse>> Transactions(
        [FromQuery] string? status,
        [FromQuery] int skip,
        [FromQuery] int take = ListQuery.DefaultTake,
        CancellationToken ct = default)
    {
        return Ok(await _service.ListTransactionsAsync(GetPortfolioId(), status, skip, take, ct));
    }

    [HttpPost("transactions/import")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage)]
    [ProducesResponseType(typeof(ImportBankTransactionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportBankTransactionsResponse>> Import(
        [FromBody] ImportBankTransactionsRequest request,
        CancellationToken ct)
    {
        if (request.Statement is null && request.Transactions.Count == 0)
        {
            return BadRequest(new { error = "A bank statement or at least one bank transaction is required." });
        }

        return Ok(await _service.ImportAsync(GetPortfolioId(), request, ct));
    }

    [HttpPost("transactions/{id:int}/match")]
    [ProducesResponseType(typeof(OperationalBankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationalBankTransactionResponse>> Match(
        int id,
        [FromBody] MatchBankTransactionRequest request,
        CancellationToken ct)
    {
        try
        {
            var updated = await _service.MatchAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null ? NotFound(new { error = "Bank transaction or match target not found" }) : Ok(updated);
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPut("transactions/{id:int}/route")]
    [ProducesResponseType(typeof(OperationalBankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OperationalBankTransactionResponse>> Route(
        int id, [FromBody] RouteBankTransactionRequest request, CancellationToken ct)
    {
        try
        {
            var updated = await _service.RouteTransactionAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null
                ? NotFound(new { error = "Bank transaction or property not found" })
                : Ok(ToOperationalResponse(updated));
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost("transactions/{id:int}/clear-match")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive)]
    [ProducesResponseType(typeof(BankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionResponse>> ClearMatch(
        int id, [FromBody] BankTransactionMutationRequest request, CancellationToken ct)
    {
        try
        {
            var updated = await _service.ClearMatchAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null ? NotFound(new { error = "Bank transaction not found" }) : Ok(updated);
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    /// <summary>
    /// Duplicate / match review queue: imported bank lines with a suggested tenant receipt or expense
    /// match that still need the landlord to confirm or dismiss, so a scanned receipt and the bank
    /// deposit/withdrawal are not double-counted.
    /// </summary>
    [HttpGet("review-queue")]
    [ProducesResponseType(typeof(BankReviewQueueResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankReviewQueueResponse>> ReviewQueue(
        CancellationToken ct,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        return Ok(await _service.GetReviewQueueAsync(GetWorkspaceReadScope(), skip, take, ct));
    }

    /// <summary>
    /// Confirm a suggested match. Links the bank line to a canonical tenant-account receipt or an
    /// expense and marks it Matched so accounting treats them as the same money. A receipt target
    /// requires both tenantAccountId and tenantLedgerEntryId; omitting target ids accepts the suggestion.
    /// </summary>
    [HttpPost("transactions/{id:int}/confirm-match")]
    [ProducesResponseType(typeof(OperationalBankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationalBankTransactionResponse>> ConfirmMatch(
        int id,
        [FromBody] ConfirmBankMatchRequest request,
        CancellationToken ct)
    {
        try
        {
            var updated = await _service.ConfirmMatchAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null
                ? NotFound(new { error = "Bank transaction, suggested match, or match target not found" })
                : Ok(updated);
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    /// <summary>
    /// Dismiss a suggested match (not a match). Clears any link and marks it Dismissed so it leaves
    /// the review queue; the bank line stays in the books as its own real money.
    /// </summary>
    [HttpPost("transactions/{id:int}/dismiss-match")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive)]
    [ProducesResponseType(typeof(BankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionResponse>> DismissMatch(
        int id, [FromBody] BankTransactionMutationRequest request, CancellationToken ct)
    {
        try
        {
            var updated = await _service.DismissMatchAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null ? NotFound(new { error = "Bank transaction not found" }) : Ok(updated);
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    /// <summary>
    /// Mark a bank line as personal / not business money (e.g. Starbucks, Uber, an owner draw). It
    /// drops out of the unmatched review queue and is excluded from the business books, but stays
    /// listable under the "Removed" status filter so it can be reviewed or un-ignored later.
    /// </summary>
    [HttpPost("transactions/{id:int}/ignore")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive)]
    [ProducesResponseType(typeof(BankTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionResponse>> Ignore(
        int id, [FromBody] BankTransactionMutationRequest request, CancellationToken ct)
    {
        try
        {
            var updated = await _service.IgnoreTransactionAsync(GetWorkspaceReadScope(), id, request, ct);
            return updated == null ? NotFound(new { error = "Bank transaction not found" }) : Ok(updated);
        }
        catch (BankingConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    private static OperationalBankTransactionResponse ToOperationalResponse(BankTransactionResponse transaction) => new()
    {
        Id = transaction.Id,
        PostedAt = transaction.PostedAt,
        Description = transaction.Description,
        MerchantName = transaction.MerchantName,
        Amount = transaction.Amount,
        IsoCurrencyCode = transaction.IsoCurrencyCode,
        Category = transaction.Category,
        MatchStatus = transaction.MatchStatus,
        UpdatedAt = transaction.UpdatedAt,
    };
}
