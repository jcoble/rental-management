using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Payments;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/tenant-money/historical-rent")]
[Produces("application/json")]
public sealed class HistoricalRentRecoveryController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<RecoverHistoricalRentChargeResult> Codec =
        new("historical-rent-charge.recover.v1");
    private static readonly AtomicJsonResultCodec<RecoverLateFeeChargesResult> LateFeeCodec =
        new("late-fee-charges.recover.v1");
    private readonly IAtomicUnitOfWork _atomic;

    public HistoricalRentRecoveryController(IAtomicUnitOfWork atomic) =>
        _atomic = atomic;

    [HttpPost("recover")]
    public async Task<IActionResult> Recover(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecoverHistoricalRentChargeRequest request,
        CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 200 characters.",
            });
        if (!TryGetActiveAccessContext(out var access))
            return Forbid();

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey))).ToLowerInvariant();
        var command = new RecoverHistoricalRentChargeCommand(
            access.PortfolioId,
            request.TenantAccountId,
            request.LeaseAgreementId,
            request.ExistingRentChargeEntryId,
            request.ExistingReceiptEntryId,
            request.ExistingAllocationId,
            request.ExpectedCurrentRentTrackingStartOn,
            request.CorrectRentTrackingStartOn,
            request.RentPeriodStartOn,
            request.ExpectedExistingChargeDueOn,
            request.ExpectedExistingChargeAmount,
            request.ExpectedReceiptAmount,
            request.CorrectRentAmount,
            request.FinancialReference,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            $"historical-rent:{access.PortfolioId}:{request.TenantAccountId}:{digest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "historical-rent-charge.recover",
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

    [HttpPost("late-fees/recover")]
    public async Task<IActionResult> RecoverLateFees(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecoverLateFeeChargesRequest request,
        CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 200 characters.",
            });
        if (!TryGetActiveAccessContext(out var access))
            return Forbid();

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey))).ToLowerInvariant();
        var command = new RecoverLateFeeChargesCommand(
            access.PortfolioId,
            request.Corrections.Select(row => new RecoverLateFeeChargeRow(
                row.TenantAccountId,
                row.ExistingLateFeeEntryId,
                row.ExpectedExistingAmount,
                row.ReplacementAmount,
                row.AlreadyReversed)).ToArray(),
            request.ExpectedReviewedChargeCount,
            request.ExpectedReversedChargeCount,
            request.ExpectedReplacementChargeCount,
            request.ExpectedReversedTotal,
            request.ExpectedReplacementTotal,
            request.FinancialReference,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            $"late-fee-recovery:{access.PortfolioId}:{digest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "late-fee-charges.recover",
                    command.DeliveryIdempotencyKey),
                command,
                LateFeeCodec,
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
