using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Tenant-notice drafts generated from enabled policies and canonical lease/account facts.
/// </summary>
[ApiController]
[Route("api/v1/notices")]
[Produces("application/json")]
public class NoticeDraftsController : ManagementControllerBase
{
    private readonly INoticeDraftService _service;
    private readonly INotificationFoundationService _foundation;

    public NoticeDraftsController(
        INoticeDraftService service,
        INotificationFoundationService foundation)
    {
        _service = service;
        _foundation = foundation;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NoticeDraftResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NoticeDraftResponse>>> List(
        [FromQuery] string? status,
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        return Ok(await _service.ListAsync(GetWorkspaceReadScope(), status, query, ct));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Get(int id, CancellationToken ct)
    {
        var draft = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return draft == null ? NotFound(new { error = "Draft notice not found" }) : Ok(draft);
    }

    /// <summary>
    /// Runs one PostgreSQL set command for the caller's portfolio. An empty body evaluates every due
    /// enabled policy; canonical relationship/account/ledger identifiers and <c>noticeType</c> narrow
    /// the same command without loading candidate ids into application memory.
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(GenerateNoticeDraftsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GenerateNoticeDraftsResponse>> Generate(
        [FromBody] GenerateNoticeDraftsRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        }

        return Ok(await _service.GenerateAsync(GetWorkspaceReadScope(), request, operationKey, ct));
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Update(
        int id,
        [FromBody] UpdateNoticeDraftRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        }

        var updated = await _service.UpdateAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or no longer editable" }) : Ok(updated);
    }

    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Approve(
        int id,
        [FromBody] ApproveNoticeDraftRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        }
        if (!TryMapChannels(request.Channels, out var channels))
        {
            return BadRequest(new { error = "Choose at least one valid delivery channel." });
        }

        var scope = GetWorkspaceReadScope();
        try
        {
            await _foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                id,
                new ApproveAndQueueNoticeRequest(channels),
                null,
                operationKey,
                ct);
        }
        catch (NoticeApprovalAuthorizationException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }

        var approved = await _service.GetAsync(scope, id, ct);
        return approved is null
            ? NotFound(new { error = "Approved notice could not be read in the current scope" })
            : Ok(approved);
    }

    [HttpPost("{id:int}/dismiss")]
    [ProducesResponseType(typeof(NoticeDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoticeDraftResponse>> Dismiss(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
        {
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        }

        var updated = await _service.DismissAsync(GetWorkspaceReadScope(), id, operationKey, ct);
        return updated == null ? NotFound(new { error = "Draft notice not found or cannot be dismissed" }) : Ok(updated);
    }

    private static bool TryMapChannels(
        IReadOnlyList<string> requested,
        out IReadOnlyList<NoticeDeliveryChannel> channels)
    {
        var mapped = new List<NoticeDeliveryChannel>(requested.Count);
        foreach (var raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                channels = [];
                return false;
            }
            var channel = raw.Trim().ToLowerInvariant() switch
            {
                "portal" or "tenantportal" or "tenant portal" => NoticeDeliveryChannel.TenantPortal,
                "push" or "mobilepush" or "mobile push" => NoticeDeliveryChannel.MobilePush,
                "email" => NoticeDeliveryChannel.Email,
                "sms" or "text" => NoticeDeliveryChannel.Sms,
                _ => (NoticeDeliveryChannel?)null,
            };
            if (channel is null)
            {
                channels = [];
                return false;
            }
            mapped.Add(channel.Value);
        }

        channels = mapped.Distinct().OrderBy(channel => channel).ToArray();
        return channels.Count > 0;
    }
}
