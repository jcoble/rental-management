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
    public Task<AiIntegrationStatusDto> Activate(
        [FromBody] ActivateAiCredentialRequest request,
        CancellationToken ct) =>
        _credentials.ActivateAsync(GetActiveAccessContext(), request, ct);

    [HttpPut("rotate")]
    public Task<AiIntegrationStatusDto> Rotate(
        [FromBody] RotateAiCredentialRequest request,
        CancellationToken ct) =>
        _credentials.RotateAsync(GetActiveAccessContext(), request, ct);

    [HttpDelete]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        await _credentials.RemoveAsync(GetActiveAccessContext(), ct);
        return NoContent();
    }
}
