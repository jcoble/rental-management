using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/lease-managements/{leaseManagementId:int}")]
[Produces("application/json")]
public sealed class TenantAccountLifecycleController : ManagementControllerBase
{
    private readonly IRequestWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;

    public TenantAccountLifecycleController(IRequestWriteExecutor writes, RentalCommandDbContext db)
    {
        _writes = writes;
        _db = db;
    }

    [HttpPost("close-account")]
    public async Task<IActionResult> Close(int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CloseTenantAccountRequest request, CancellationToken ct)
    {
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
            return BadRequest(new { error = "A request key is required." });
        if (!TryGetActiveAccessContext(out var active)) return Forbid();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        var portfolioId = GetPortfolioId();
        var command = new CloseTenantAccountCommand(portfolioId, leaseManagementId,
            request.TenantAccountId, request.CloseReasonCode, request.CloseNote, GetUserId(),
            active.SessionId, active.AccessContextId, active.AccessRevision,
            $"tenant-account-close:{portfolioId}:{request.TenantAccountId}:{digest}");
        try
        {
            var outcome = await _writes.ExecuteAsync(
                $"{portfolioId}:{request.TenantAccountId}:{digest}",
                LeasingWriteSupport.Write<CloseTenantAccountCommand, CloseTenantAccountResult>(_db, command), ct);
            return outcome.Value.Outcome == CloseTenantAccountOutcome.Closed
                ? Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed })
                : Conflict(new { error = outcome.Value.Error });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }
}
