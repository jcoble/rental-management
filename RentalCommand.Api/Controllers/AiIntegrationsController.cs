using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/ai")]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.IntegrationsManage)]
public sealed class AiIntegrationsController : ManagementControllerBase
{
    private readonly IWorkspaceLlmCredentialService _credentials;

    public AiIntegrationsController(IWorkspaceLlmCredentialService credentials) =>
        _credentials = credentials;

    [HttpGet]
    public Task<AiIntegrationStatusDto> GetStatus(CancellationToken ct) =>
        _credentials.GetStatusAsync(GetPortfolioId(), ct);

    [HttpPost("test")]
    public async Task<ActionResult<AiCredentialTestResultDto>> Test(
        [FromBody] TestAiCredentialRequest request,
        CancellationToken ct)
    {
        var result = await _credentials.TestAsync(GetActiveAccessContext(), request, ct);
        return result.Succeeded ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPut]
    public async Task<ActionResult<AiIntegrationStatusDto>> Activate(
        [FromBody] ActivateAiCredentialRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(idempotencyKey, request.ClientOperationId);
        if (key is null) return BadRequest("Idempotency-Key is required and must be at most 128 characters.");
        return Ok(await _credentials.ActivateAsync(
            GetActiveAccessContext(),
            request with { ClientOperationId = key },
            ct));
    }

    [HttpPut("rotate")]
    public async Task<ActionResult<AiIntegrationStatusDto>> Rotate(
        [FromBody] RotateAiCredentialRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(idempotencyKey, request.ClientOperationId);
        if (key is null) return BadRequest("Idempotency-Key is required and must be at most 128 characters.");
        return Ok(await _credentials.RotateAsync(
            GetActiveAccessContext(),
            request with { ClientOperationId = key },
            ct));
    }

    [HttpDelete]
    public async Task<IActionResult> Remove(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(idempotencyKey, null);
        if (key is null) return BadRequest("Idempotency-Key is required and must be at most 128 characters.");
        await _credentials.RemoveAsync(GetActiveAccessContext(), key, ct);
        return NoContent();
    }

    private static string? RequireIdempotencyKey(string? header, string? clientOperationId)
    {
        var value = string.IsNullOrWhiteSpace(header) ? clientOperationId : header;
        value = value?.Trim();
        return value is { Length: > 0 and <= 128 } ? value : null;
    }
}
