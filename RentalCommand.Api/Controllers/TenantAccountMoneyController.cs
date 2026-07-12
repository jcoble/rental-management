using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Payments;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/tenant-accounts/{tenantAccountId:int}")]
[Produces("application/json")]
public sealed class TenantAccountMoneyController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<SecurityDepositMutationResult> DepositCodec =
        new("tenant-account.deposit.mutation.v1");
    private readonly IAtomicUnitOfWork _atomic;

    public TenantAccountMoneyController(IAtomicUnitOfWork atomic) => _atomic = atomic;

    [HttpPost("receipts")]
    public async Task<IActionResult> RecordReceipt(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecordTenantReceiptRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var failure)) return failure!;
        if (request.EffectiveOn == default) return BadRequest(new { error = "EffectiveOn is required." });
        var command = new RecordTenantReceiptCommand(envelope.PortfolioId, tenantAccountId,
            request.Amount, request.EffectiveOn, request.Description, request.PaymentMethodSummary,
            request.ExternalReference, request.PayerName, request.CheckNumber, request.BankName,
            request.SourceStoredFileId, request.AllocateOldestCharges, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            CapabilityKeys.MoneyPaymentsManage, $"manual-receipt:{envelope.KeyDigest}",
            $"tenant-receipt:{envelope.PortfolioId}:{tenantAccountId}:{envelope.KeyDigest}");
        return await Execute("tenant-account.receipt.record", command.DeliveryIdempotencyKey,
            command, ReceiptCodec, ct);
    }

    [HttpPost("deposit/fund")]
    public async Task<IActionResult> FundDeposit(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] FundSecurityDepositRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveOn == default) return BadRequest(new { error = "EffectiveOn is required." });
        var command = new FundSecurityDepositCommand(e.PortfolioId, tenantAccountId,
            request.SecurityDepositAccountId, request.Amount, request.EffectiveOn,
            request.Description, request.PaymentMethodSummary, request.ExternalReference,
            request.SourceStoredFileId, e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            CapabilityKeys.MoneyDepositsManage, $"deposit-fund:{e.KeyDigest}",
            $"deposit-fund:{e.PortfolioId}:{tenantAccountId}:{request.SecurityDepositAccountId}:{e.KeyDigest}");
        return await ExecuteDeposit("tenant-account.deposit.fund", command, ct);
    }

    [HttpPost("deposit/deductions")]
    public async Task<IActionResult> DeductDeposit(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] DeductSecurityDepositRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveOn == default) return BadRequest(new { error = "EffectiveOn is required." });
        var command = new DeductSecurityDepositCommand(e.PortfolioId, tenantAccountId,
            request.SecurityDepositAccountId, request.Amount, request.EffectiveOn, request.Reason,
            request.Notes, request.SourceStoredFileId, e.UserId, e.SessionId, e.AccessContextId,
            e.AccessRevision, CapabilityKeys.MoneyDepositsManage, $"deposit-deduction:{e.KeyDigest}",
            $"deposit-deduction:{e.PortfolioId}:{tenantAccountId}:{request.SecurityDepositAccountId}:{e.KeyDigest}");
        return await ExecuteDeposit("tenant-account.deposit.deduct", command, ct);
    }

    [HttpPost("deposit/refunds")]
    public async Task<IActionResult> RefundDeposit(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RefundSecurityDepositRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveOn == default) return BadRequest(new { error = "EffectiveOn is required." });
        var command = new RefundSecurityDepositCommand(e.PortfolioId, tenantAccountId,
            request.SecurityDepositAccountId, request.Amount, request.EffectiveOn,
            request.Description, request.ExternalReference, e.UserId, e.SessionId,
            e.AccessContextId, e.AccessRevision, CapabilityKeys.MoneyDepositsManage,
            $"deposit-refund:{e.KeyDigest}",
            $"deposit-refund:{e.PortfolioId}:{tenantAccountId}:{request.SecurityDepositAccountId}:{e.KeyDigest}");
        return await ExecuteDeposit("tenant-account.deposit.refund", command, ct);
    }

    private async Task<IActionResult> ExecuteDeposit<TCommand>(string commandType,
        TCommand command, CancellationToken ct) where TCommand : notnull, ISecurityDepositMoneyCommand
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(commandType,
                command.DeliveryIdempotencyKey), command, DepositCodec, ct);
            if (!outcome.Value.Applied) return Conflict(new { error = outcome.Value.Error });
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task<IActionResult> Execute<TCommand, TResult>(string commandType, string key,
        TCommand command, AtomicJsonResultCodec<TResult> codec, CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(commandType, key), command, codec, ct);
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private bool TryEnvelope(string? idempotencyKey, out CommandEnvelope envelope, out IActionResult? failure)
    {
        envelope = default;
        failure = null;
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        {
            failure = BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });
            return false;
        }
        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)
            || !int.TryParse(User.FindFirstValue("ctx"), out var contextId)
            || !long.TryParse(User.FindFirstValue("ar"), out var revision))
        {
            failure = Forbid();
            return false;
        }
        envelope = new CommandEnvelope(GetPortfolioId(), GetUserId(), sessionId, contextId,
            revision, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
        return true;
    }

    private readonly record struct CommandEnvelope(int PortfolioId, int UserId, Guid SessionId,
        int AccessContextId, long AccessRevision, string KeyDigest);
}
