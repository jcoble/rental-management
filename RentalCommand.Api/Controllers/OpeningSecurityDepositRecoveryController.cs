using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Payments;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/opening-balances/security-deposits")]
[Produces("application/json")]
public sealed class OpeningSecurityDepositRecoveryController
    : AuthenticatedPortfolioControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly IWriteExecutor _writes;

    public OpeningSecurityDepositRecoveryController(
        RentalCommandDbContext db,
        IWriteExecutor writes)
    {
        _db = db;
        _writes = writes;
    }

    [HttpPost("recover")]
    public async Task<IActionResult> Recover(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecoverOpeningSecurityDepositsRequest request,
        CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 200 characters.",
            });
        if (request.EffectiveOn == default)
            return BadRequest(new { error = "EffectiveOn is required." });
        if (!TryGetActiveAccessContext(out var access))
            return Forbid();

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey))).ToLowerInvariant();
        var command = new RecoverOpeningSecurityDepositsCommand(
            access.PortfolioId,
            request.EffectiveOn,
            request.ExpectedAccountCount,
            request.ExpectedTotal,
            request.FinancialReference,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            CapabilityKeys.MoneyDepositsManage,
            $"opening-security-deposits:{access.PortfolioId}:{digest}");
        try
        {
            var handler = new RecoverOpeningSecurityDepositsRule(_db);
            var outcome = await _writes.ExecuteAsync(
                command.DeliveryIdempotencyKey,
                TenantMoneyWriteSupport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync),
                ct);
            return Ok(new
            {
                outcome.Value,
                replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
