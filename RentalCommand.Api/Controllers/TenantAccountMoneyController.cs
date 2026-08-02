using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Payments;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/tenant-accounts/{tenantAccountId:int}")]
[Produces("application/json")]
public sealed class TenantAccountMoneyController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<TenantChargeMutationResult> ChargeCodec =
        new("tenant-account.charge.mutation.v1");
    private static readonly AtomicJsonResultCodec<TenantLedgerMutationResult> LedgerCodec =
        new("tenant-account.ledger.mutation.v1");
    private static readonly AtomicJsonResultCodec<TenantPaymentRefundResult> RefundCodec =
        new("tenant-account.payment.refund.v1");
    private static readonly AtomicJsonResultCodec<SecurityDepositMutationResult> DepositCodec =
        new("tenant-account.deposit.mutation.v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IAccountingLedgerReadModelService _ledgerReadModels;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public TenantAccountMoneyController(
        IAtomicUnitOfWork atomic,
        IAccountingLedgerReadModelService ledgerReadModels,
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _atomic = atomic;
        _ledgerReadModels = ledgerReadModels;
        _db = db;
        _timeProvider = timeProvider;
    }

    [HttpGet("ledger")]
    [ProducesResponseType(typeof(AccountingPage<TenantLedgerRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccountingPage<TenantLedgerRow>>> Ledger(
        int tenantAccountId, [FromQuery] TenantLedgerQuery query, CancellationToken ct)
    {
        if (!await TenantAccountExistsAsync(tenantAccountId, ct)) return NotFound();
        var page = await _ledgerReadModels.GetTenantLedgerAsync(GetPortfolioId(), tenantAccountId, query, ct);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpGet("ledger/{entryId:long}")]
    [ProducesResponseType(typeof(TenantLedgerRow), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantLedgerRow>> LedgerEntry(
        int tenantAccountId, long entryId, CancellationToken ct)
    {
        if (!await TenantAccountExistsAsync(tenantAccountId, ct)) return NotFound();
        var row = await _ledgerReadModels.GetTenantLedgerEntryAsync(
            GetPortfolioId(), tenantAccountId, entryId, ct);
        return row is null ? NotFound() : Ok(row);
    }

    [HttpGet("month-summary")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantMonthSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<TenantMonthSummary>>> MonthSummary(
        int tenantAccountId, [FromQuery] TenantMonthSummaryQuery query, CancellationToken ct)
    {
        if (!await TenantAccountExistsAsync(tenantAccountId, ct)) return NotFound();
        var summary = await _ledgerReadModels.GetTenantMonthSummaryAsync(
            GetPortfolioId(), tenantAccountId, query, ct);
        return summary is null ? NotFound() : Ok(summary);
    }

    [HttpGet("ledger-summary")]
    [ProducesResponseType(typeof(TenantLedgerPeriodSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantLedgerPeriodSummary>> LedgerSummary(
        int tenantAccountId, [FromQuery] TenantLedgerPeriodSummaryQuery query, CancellationToken ct)
    {
        if (!await TenantAccountExistsAsync(tenantAccountId, ct)) return NotFound();
        try
        {
            var summary = await _ledgerReadModels.GetTenantLedgerPeriodSummaryAsync(
                GetPortfolioId(), tenantAccountId, query.Months, ct);
            return summary is null ? NotFound() : Ok(summary);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("recurring-charges")]
    [ProducesResponseType(typeof(AccountingPage<RecurringTenantChargeRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccountingPage<RecurringTenantChargeRow>>> RecurringCharges(
        int tenantAccountId, [FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!await TenantAccountExistsAsync(tenantAccountId, ct)) return NotFound();
        return Ok(await _ledgerReadModels.GetRecurringTenantChargesAsync(
            GetPortfolioId(), tenantAccountId, query, ct));
    }

    [HttpPost("recurring-charges")]
    [ProducesResponseType(typeof(RecurringTenantChargeRow), StatusCodes.Status201Created)]
    public async Task<ActionResult<RecurringTenantChargeRow>> CreateRecurringCharge(
        int tenantAccountId, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateRecurringTenantChargeRequest request, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out _))
            return BadRequest(new { error = "A valid Idempotency-Key is required." });
        var portfolioId = GetPortfolioId();
        var tenant = await _db.TenantAccounts.AsNoTracking().SingleOrDefaultAsync(
            account => account.PortfolioId == portfolioId && account.Id == tenantAccountId, ct);
        if (tenant is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.Amount <= 0m
            || request.LedgerAccountId <= 0 || request.LeaseAgreementId is not int leaseAgreementId
            || request.EffectiveStartOn == default || request.MonthlyDueDay is < 1 or > 31)
            return BadRequest(new { error = "Display name, amount, lease, account, start date, and due day are required." });
        if (request.EffectiveEndOn is DateOnly end && end < request.EffectiveStartOn)
            return BadRequest(new { error = "The end date cannot be before the start date." });
        if (!await _db.LedgerAccounts.AnyAsync(account => account.PortfolioId == portfolioId
                && account.Id == request.LedgerAccountId && account.IsActive, ct))
            return BadRequest(new { error = "The recurring charge account is not active in this portfolio." });
        if (!await _db.LeaseAgreements.AnyAsync(agreement => agreement.PortfolioId == portfolioId
                && agreement.Id == leaseAgreementId, ct))
            return BadRequest(new { error = "The lease agreement is not in this portfolio." });

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var schedule = new RecurringTenantCharge
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            TenantAccountId = tenantAccountId,
            LeaseAgreementId = leaseAgreementId,
            DisplayName = request.DisplayName.Trim(),
            Amount = request.Amount,
            Currency = tenant.Currency,
            LedgerAccountId = request.LedgerAccountId,
            EffectiveStartOn = request.EffectiveStartOn,
            EffectiveEndOn = request.EffectiveEndOn,
            MonthlyDueDay = (short)request.MonthlyDueDay,
            NextRunDate = request.NextRunDate ?? request.EffectiveStartOn,
            IsActive = true,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.RecurringTenantCharges.Add(schedule);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(RecurringCharges), new { tenantAccountId }, ToRecurringRow(schedule));
    }

    [HttpPatch("recurring-charges/{id:int}")]
    [ProducesResponseType(typeof(RecurringTenantChargeRow), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringTenantChargeRow>> PatchRecurringCharge(
        int tenantAccountId, int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PatchRecurringTenantChargeRequest request, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out _))
            return BadRequest(new { error = "A valid Idempotency-Key is required." });
        var portfolioId = GetPortfolioId();
        var schedule = await _db.RecurringTenantCharges.SingleOrDefaultAsync(value =>
            value.PortfolioId == portfolioId && value.TenantAccountId == tenantAccountId && value.Id == id, ct);
        if (schedule is null) return NotFound();
        if (request.Amount is decimal amount && amount <= 0m)
            return BadRequest(new { error = "Amount must be positive." });
        if (request.MonthlyDueDay is int dueDay && dueDay is < 1 or > 31)
            return BadRequest(new { error = "Monthly due day must be between 1 and 31." });
        if (request.DisplayName is string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return BadRequest(new { error = "Display name is required." });
            schedule.DisplayName = displayName.Trim();
        }
        if (request.Amount is decimal nextAmount) schedule.Amount = nextAmount;
        if (request.LedgerAccountId is int accountId)
        {
            if (!await _db.LedgerAccounts.AnyAsync(account => account.PortfolioId == portfolioId
                    && account.Id == accountId && account.IsActive, ct))
                return BadRequest(new { error = "The recurring charge account is not active in this portfolio." });
            schedule.LedgerAccountId = accountId;
        }
        if (request.EffectiveStartOn is DateOnly start) schedule.EffectiveStartOn = start;
        if (request.EffectiveEndOn is DateOnly end) schedule.EffectiveEndOn = end;
        if (schedule.EffectiveEndOn is DateOnly finalEnd && finalEnd < schedule.EffectiveStartOn)
            return BadRequest(new { error = "The end date cannot be before the start date." });
        if (request.MonthlyDueDay is int nextDueDay) schedule.MonthlyDueDay = (short)nextDueDay;
        if (request.NextRunDate is DateOnly nextRunDate) schedule.NextRunDate = nextRunDate;
        if (request.IsActive is bool active) schedule.IsActive = active;
        if (request.PropertyId is int propertyId) schedule.PropertyId = propertyId;
        if (request.UnitId is int unitId) schedule.UnitId = unitId;
        schedule.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return Ok(ToRecurringRow(schedule));
    }

    [HttpPost("recurring-charges/{id:int}/deactivate")]
    [ProducesResponseType(typeof(RecurringTenantChargeRow), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringTenantChargeRow>> DeactivateRecurringCharge(
        int tenantAccountId, int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out _))
            return BadRequest(new { error = "A valid Idempotency-Key is required." });
        var portfolioId = GetPortfolioId();
        var schedule = await _db.RecurringTenantCharges.SingleOrDefaultAsync(value =>
            value.PortfolioId == portfolioId && value.TenantAccountId == tenantAccountId && value.Id == id, ct);
        if (schedule is null) return NotFound();
        schedule.IsActive = false;
        schedule.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return Ok(ToRecurringRow(schedule));
    }

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
            request.SourceStoredFileId, request.TargetChargeEntryId, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            CapabilityKeys.MoneyPaymentsManage, $"manual-receipt:{envelope.KeyDigest}",
            $"tenant-receipt:{envelope.PortfolioId}:{tenantAccountId}:{envelope.KeyDigest}",
            _timeProvider.GetUtcNow().UtcDateTime)
        {
            AllocateOldestCharges = request.AllocateOldestCharges,
        };
        return await Execute("tenant-account.receipt.record", command.DeliveryIdempotencyKey,
            command, ReceiptCodec, ct);
    }

    [HttpPost("charges")]
    public async Task<IActionResult> PostCharge(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PostTenantChargeRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var failure)) return failure!;
        if (request.EffectiveOn == default || request.DueOn == default)
            return BadRequest(new { error = "EffectiveOn and DueOn are required." });
        var command = new PostTenantChargeCommand(envelope.PortfolioId, tenantAccountId,
            request.Amount, request.EffectiveOn, request.DueOn, request.Description,
            request.SourceStoredFileId, envelope.UserId, envelope.SessionId,
            envelope.AccessContextId, envelope.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-charge:{envelope.KeyDigest}",
            $"tenant-charge:{envelope.PortfolioId}:{tenantAccountId}:{envelope.KeyDigest}");
        return await ExecuteCharge("tenant-account.charge.post", command.DeliveryIdempotencyKey,
            command, ct);
    }

    [HttpPost("charges/{chargeEntryId:long}/reversals")]
    public async Task<IActionResult> ReverseCharge(int tenantAccountId, long chargeEntryId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReverseTenantChargeRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var failure)) return failure!;
        if (chargeEntryId <= 0 || request.EffectiveOn == default)
            return BadRequest(new { error = "Charge entry and EffectiveOn are required." });
        var command = new ReverseTenantChargeCommand(envelope.PortfolioId, tenantAccountId,
            chargeEntryId, request.EffectiveOn, request.Reason,
            request.SourceStoredFileId, envelope.UserId, envelope.SessionId,
            envelope.AccessContextId, envelope.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-charge-reversal:{envelope.KeyDigest}",
            $"tenant-charge-reversal:{envelope.PortfolioId}:{tenantAccountId}:{chargeEntryId}:{envelope.KeyDigest}");
        return await ExecuteCharge("tenant-account.charge.reverse", command.DeliveryIdempotencyKey,
            command, ct);
    }

    [HttpPost("credits")]
    public async Task<IActionResult> PostCredit(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PostTenantCreditRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveOn == default)
            return BadRequest(new { error = "EffectiveOn is required." });
        var command = new PostTenantCreditCommand(e.PortfolioId, tenantAccountId,
            request.Amount, request.EffectiveOn, request.Description,
            request.SourceStoredFileId, request.AllocateOldestCharges, e.UserId,
            e.SessionId, e.AccessContextId, e.AccessRevision,
            CapabilityKeys.MoneyChargesManage, $"tenant-credit:{e.KeyDigest}",
            $"tenant-credit:{e.PortfolioId}:{tenantAccountId}:{e.KeyDigest}");
        return await ExecuteLedger("tenant-account.credit.post",
            command.DeliveryIdempotencyKey, command, ct);
    }

    [HttpPost("adjustments")]
    public async Task<IActionResult> PostAdjustment(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PostTenantAdjustmentRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.Direction is null || request.EffectiveOn == default)
            return BadRequest(new { error = "Direction and EffectiveOn are required." });
        var command = new PostTenantAdjustmentCommand(e.PortfolioId, tenantAccountId,
            request.Direction.Value, request.Amount, request.EffectiveOn,
            request.Description, request.SourceStoredFileId, e.UserId, e.SessionId,
            e.AccessContextId, e.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-adjustment:{e.KeyDigest}",
            $"tenant-adjustment:{e.PortfolioId}:{tenantAccountId}:{e.KeyDigest}");
        return await ExecuteLedger("tenant-account.adjustment.post",
            command.DeliveryIdempotencyKey, command, ct);
    }

    [HttpPost("reversals")]
    public async Task<IActionResult> ReverseLedgerEntry(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReverseTenantLedgerEntryRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.ReversesEntryId <= 0 || request.EffectiveOn == default)
            return BadRequest(new { error = "Ledger entry and EffectiveOn are required." });
        var command = new ReverseTenantLedgerEntryCommand(e.PortfolioId, tenantAccountId,
            request.ReversesEntryId, request.EffectiveOn, request.Reason,
            request.SourceStoredFileId, e.UserId, e.SessionId, e.AccessContextId,
            e.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-ledger-reversal:{e.KeyDigest}",
            $"tenant-ledger-reversal:{e.PortfolioId}:{tenantAccountId}:{request.ReversesEntryId}:{e.KeyDigest}");
        return await ExecuteLedger("tenant-account.ledger.reverse",
            command.DeliveryIdempotencyKey, command, ct);
    }

    [HttpPost("refunds")]
    public async Task<IActionResult> RefundPayment(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RefundTenantPaymentRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.PaymentEntryId <= 0 || request.EffectiveOn == default)
            return BadRequest(new { error = "Payment entry and EffectiveOn are required." });
        var command = new RefundTenantPaymentCommand(e.PortfolioId, tenantAccountId,
            request.PaymentEntryId, request.EffectiveOn, request.Reason,
            request.PaymentMethodSummary, request.ExternalReference,
            request.SourceStoredFileId, e.UserId, e.SessionId, e.AccessContextId,
            e.AccessRevision, CapabilityKeys.MoneyPaymentsManage,
            $"tenant-payment-refund:{e.KeyDigest}",
            $"tenant-payment-refund:{e.PortfolioId}:{tenantAccountId}:{request.PaymentEntryId}:{e.KeyDigest}");
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("tenant-account.payment.refund",
                    command.DeliveryIdempotencyKey), command, RefundCodec, ct);
            if (outcome.Value.Outcome is TenantPaymentRefundOutcome.AlreadyRefunded
                    or TenantPaymentRefundOutcome.ExternalCorrectionUnavailable)
                return Conflict(new { error = outcome.Value.Error, outcome.Value.Outcome });
            return Ok(new { outcome.Value,
                replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
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

    [HttpPost("deposit/reversals")]
    public async Task<IActionResult> ReverseDepositEntry(int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReverseSecurityDepositEntryRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.SecurityDepositAccountId <= 0 || request.ReversesEntryId <= 0
            || request.EffectiveOn == default)
            return BadRequest(new
            {
                error = "Security deposit account, deposit entry, and EffectiveOn are required."
            });
        var command = new ReverseSecurityDepositEntryCommand(e.PortfolioId, tenantAccountId,
            request.SecurityDepositAccountId, request.ReversesEntryId,
            request.EffectiveOn, request.Reason, request.SourceStoredFileId,
            e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            CapabilityKeys.MoneyDepositsManage, $"deposit-reversal:{e.KeyDigest}",
            $"deposit-reversal:{e.PortfolioId}:{tenantAccountId}:{request.SecurityDepositAccountId}:{request.ReversesEntryId}:{e.KeyDigest}");
        return await ExecuteDeposit("tenant-account.deposit.reverse", command, ct);
    }

    private Task<bool> TenantAccountExistsAsync(int tenantAccountId, CancellationToken ct) =>
        _db.TenantAccounts.AnyAsync(account => account.PortfolioId == GetPortfolioId()
            && account.Id == tenantAccountId, ct);

    private static RecurringTenantChargeRow ToRecurringRow(RecurringTenantCharge schedule) =>
        new()
        {
            Id = schedule.Id,
            PublicId = schedule.PublicId,
            TenantAccountId = schedule.TenantAccountId,
            LeaseAgreementId = schedule.LeaseAgreementId,
            DisplayName = schedule.DisplayName,
            Amount = schedule.Amount,
            Currency = schedule.Currency,
            LedgerAccountId = schedule.LedgerAccountId,
            EffectiveStartOn = schedule.EffectiveStartOn,
            EffectiveEndOn = schedule.EffectiveEndOn,
            MonthlyDueDay = schedule.MonthlyDueDay,
            NextRunDate = schedule.NextRunDate,
            IsActive = schedule.IsActive,
            PropertyId = schedule.PropertyId,
            UnitId = schedule.UnitId,
        };

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

    private async Task<IActionResult> ExecuteCharge<TCommand>(string commandType, string key,
        TCommand command, CancellationToken ct) where TCommand : notnull, ITenantMoneyCommand
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(commandType, key),
                command, ChargeCodec, ct);
            if (!outcome.Value.Applied) return Conflict(new { error = outcome.Value.Error });
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task<IActionResult> ExecuteLedger<TCommand>(string commandType, string key,
        TCommand command, CancellationToken ct) where TCommand : notnull, ITenantMoneyCommand
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(commandType, key),
                command, LedgerCodec, ct);
            if (!outcome.Value.Applied) return Conflict(new { error = outcome.Value.Error });
            return Ok(new { outcome.Value,
                replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
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
        if (!TryGetActiveAccessContext(out var active))
        {
            failure = Forbid();
            return false;
        }
        envelope = new CommandEnvelope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
        return true;
    }

    private readonly record struct CommandEnvelope(int PortfolioId, int UserId, Guid SessionId,
        int AccessContextId, long AccessRevision, string KeyDigest);
}
