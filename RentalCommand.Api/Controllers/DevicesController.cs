using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Registers and removes push-notification device tokens for the calling user.
/// Registration is an upsert by selected workspace plus token value — safe to call on every app launch.
/// </summary>
[ApiController]
[Route("api/v1/devices")]
[Produces("application/json")]
public class DevicesController : AuthenticatedPortfolioControllerBase
{
    private static readonly HashSet<string> ValidPlatforms =
        new(StringComparer.OrdinalIgnoreCase) { "ios", "android", "web" };

    private readonly IDeviceService _service;

    public DevicesController(IDeviceService service)
    {
        _service = service;
    }

    /// <summary>Register (or refresh) a push-notification token for the caller's current device.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterDeviceRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { error = "Token must not be empty." });

        if (!ValidPlatforms.Contains(req.Platform))
            return BadRequest(new { error = "Platform must be one of: ios, android, web." });
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();

        await _service.RegisterAsync(GetMutationScope(), req.Token, req.Platform.ToLowerInvariant(), operationKey, ct);
        return NoContent();
    }

    /// <summary>Unregister a push-notification token. Only the token's owner may remove it.</summary>
    [HttpDelete("{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unregister(
        string token,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        var removed = await _service.UnregisterAsync(GetMutationScope(), token, operationKey, ct);
        return removed ? NoContent() : NotFound(new { error = "Device token not found." });
    }

    private WorkspaceReadScope GetMutationScope()
    {
        var active = GetActiveAccessContext();
        return new WorkspaceReadScope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
    }

    private BadRequestObjectResult InvalidKey() =>
        BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
}
