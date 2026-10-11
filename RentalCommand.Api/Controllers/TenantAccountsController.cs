using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/tenant-accounts")]
[Produces("application/json")]
public sealed class TenantAccountsController : ManagementControllerBase
{
    private readonly ITenantAccountQueryService _service;
    private readonly ITenantAccountMoveOutStatementService _moveOutStatementService;

    public TenantAccountsController(
        ITenantAccountQueryService service,
        ITenantAccountMoveOutStatementService moveOutStatementService)
    {
        _service = service;
        _moveOutStatementService = moveOutStatementService;
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(TenantAccountPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantAccountPageResponse>> AccountsPage(
        [FromQuery] TenantAccountListQuery query,
        CancellationToken ct) =>
        Ok(await _service.ListAccountsPageAsync(GetWorkspaceReadScope(), query, ct));

    [HttpGet("entries/page")]
    [ProducesResponseType(typeof(TenantLedgerEntryGlobalPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantLedgerEntryGlobalPageResponse>> EntriesPage(
        [FromQuery] TenantLedgerEntryGlobalListQuery query,
        CancellationToken ct) =>
        Ok(await _service.ListEntriesPageAsync(GetWorkspaceReadScope(), query, ct));

    [HttpGet("deposits/page")]
    [ProducesResponseType(typeof(TenantAccountDepositPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantAccountDepositPageResponse>> DepositsPage(
        [FromQuery] TenantAccountDepositListQuery query,
        CancellationToken ct) =>
        Ok(await _service.ListDepositsPageAsync(GetWorkspaceReadScope(), query, ct));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TenantAccountDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantAccountDetailResponse>> Get(int id, CancellationToken ct)
    {
        var account = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return account is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(account);
    }

    [HttpGet("{id:int}/entries/page")]
    [ProducesResponseType(typeof(TenantLedgerEntryPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantLedgerEntryPageResponse>> EntriesPage(
        int id,
        [FromQuery] TenantLedgerEntryListQuery query,
        CancellationToken ct)
    {
        var page = await _service.ListEntriesPageAsync(GetWorkspaceReadScope(), id, query, ct);
        return page is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(page);
    }

    [HttpGet("{tenantAccountId:int}/entries/{tenantLedgerEntryId:long}")]
    [ProducesResponseType(typeof(TenantLedgerEntryDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantLedgerEntryDetailResponse>> Entry(
        int tenantAccountId,
        long tenantLedgerEntryId,
        CancellationToken ct)
    {
        var entry = await _service.GetEntryAsync(
            GetWorkspaceReadScope(), tenantAccountId, tenantLedgerEntryId, ct);
        return entry is null
            ? NotFound(new { error = "Tenant ledger entry not found" })
            : Ok(entry);
    }

    [HttpGet("{id:int}/charges/page")]
    [ProducesResponseType(typeof(TenantChargePageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantChargePageResponse>> ChargesPage(
        int id,
        [FromQuery] TenantChargeListQuery query,
        CancellationToken ct)
    {
        var page = await _service.ListChargesPageAsync(GetWorkspaceReadScope(), id, query, ct);
        return page is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(page);
    }

    [HttpGet("{id:int}/deposit")]
    [ProducesResponseType(typeof(TenantAccountDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantAccountDepositResponse>> Deposit(int id, CancellationToken ct)
    {
        var deposit = await _service.GetDepositAsync(GetWorkspaceReadScope(), id, ct);
        return deposit is null
            ? NotFound(new { error = "Tenant account deposit not found" })
            : Ok(deposit);
    }

    [HttpGet("{id:int}/deposit/move-out-statement")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DepositMoveOutStatement(int id, CancellationToken ct)
    {
        var pdf = await _moveOutStatementService.GetAsync(
            GetWorkspaceReadScope(), id, ct);
        return pdf is null
            ? NotFound(new { error = "Tenant account deposit not found" })
            : File(pdf, "application/pdf", $"move-out-statement-{id}.pdf");
    }
}
