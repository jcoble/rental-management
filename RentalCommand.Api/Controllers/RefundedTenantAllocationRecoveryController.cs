using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Payments;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/tenant-money/refunded-allocations")]
[Produces("application/json")]
public sealed class RefundedTenantAllocationRecoveryController
    : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<RecoverRefundedTenantAllocationResult> Codec =
        new("refunded-tenant-allocation.recover.v1");
    private readonly IAtomicUnitOfWork _atomic;

    public RefundedTenantAllocationRecoveryController(IAtomicUnitOfWork atomic) =>
        _atomic = atomic;

    [HttpPost("recover")]
    public async Task<IActionResult> Recover(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecoverRefundedTenantAllocationRequest request,
        CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
            return BadRequest(new
            {
                error = "A valid Idempotency-Key is required (maximum 200 characters).",
            });
        if (!TryGetActiveAccessContext(out var access))
            return Forbid();

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey))).ToLowerInvariant();
        var command = new RecoverRefundedTenantAllocationCommand(
            access.PortfolioId,
            request.TenantAccountId,
            request.ExistingAllocationId,
            request.ExpectedDebitEntryId,
            request.ExpectedCreditEntryId,
            request.ExpectedRefundPaymentAttemptId,
            request.ExpectedAllocationAmount,
            request.ExpectedRefundAmount,
            request.FinancialReference,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            $"refunded-allocation-recovery:{access.PortfolioId}:{request.TenantAccountId}:{request.ExistingAllocationId}",
            $"refunded-allocation-recovery:{access.PortfolioId}:{request.TenantAccountId}:{digest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "refunded-tenant-allocation.recover",
                    command.DeliveryIdempotencyKey),
                command,
                Codec,
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
