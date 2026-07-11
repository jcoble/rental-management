using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Generates the monthly amortization schedule for active loans (spec §12, with the §18 corrections):
/// idempotent by (LoanId, PeriodKey); each period's opening balance comes from the prior
/// <see cref="LoanPayment.BalanceAfter"/> (the immutable schedule, never the live cached balance);
/// the maturity stop prevents generating any period past the loan term; the negative-amortization
/// guard prevents the balance from ever growing.
///
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>), mirroring
/// <c>RentChargeService</c>.
/// </summary>
public sealed class DebtServiceService : IDebtServiceService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<DebtServiceService> _logger;

    public DebtServiceService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<DebtServiceService> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _tz = tz;
        _claims = claims;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // The landlord's business month rolls over in their LOCAL zone, not UTC (mirrors
        // RentChargeService). Drives the "current period" math only; DB writes stay UTC.
        var today = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), _tz.BusinessTimeZone).Date;

        var claims = await _claims.ClaimDebtServiceAsync(
            $"{Environment.MachineName}:debt-service", today, _timeProvider.UtcNow(),
            TimeSpan.FromMinutes(3), 25, ct);
        var created = await ProcessClaimsAsync(claims, today, ct);

        if (created > 0)
            _logger.LogInformation("DebtServiceService created {Count} loan payment(s)", created);

        return created;
    }

    private async Task<int> ProcessClaimsAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime today, CancellationToken ct)
    {
        if (claims.Count == 0) return 0;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Two reads for the whole bounded batch: one ownership-fenced row lock and one grouped
            // tail projection. No per-loan query is issued inside the processing loop.
            var loans = await _claims.LockOwnedLoansAsync(claims, today, ct);
            var tails = await _claims.LoadLoanTailsAsync(loans.Select(loan => loan.Id).ToArray(), ct);
            var created = 0;

            foreach (var loan in loans)
            {
                ct.ThrowIfCancellationRequested();

                var startMonth = new DateTime(loan.StartDate.Year, loan.StartDate.Month, 1);
                var currentMonth = new DateTime(today.Year, today.Month, 1);

                // Period 1 = the loan's start month. The current period index is how many months
                // have elapsed (inclusive). The batch lock excludes loans that have not started.
                var currentPeriodIndex = MonthsBetween(startMonth, currentMonth) + 1;

                // Never generate past the loan term (maturity stop, §18).
                var lastPeriodToGenerate = Math.Min(currentPeriodIndex, loan.TermMonths);
                tails.TryGetValue(loan.Id, out var lastRow);

                var nextPeriodIndex = lastRow is null
                    ? 1
                    : PeriodIndexFromKey(lastRow.PeriodKey, startMonth) + 1;

                // Opening balance for the next period is the prior row's BalanceAfter (or, for the
                // first period, the original principal) — NEVER the live CurrentBalance cache.
                var openingBalance = lastRow?.BalanceAfter ?? loan.OriginalAmount;

                var loanCreated = 0;
                var lastBalanceAfter = openingBalance;
                var paidOff = false;

                for (var period = nextPeriodIndex; period <= lastPeriodToGenerate; period++)
                {
                    var periodMonth = startMonth.AddMonths(period - 1);
                    var periodKey = $"{periodMonth.Year:D4}-{periodMonth.Month:D2}";

                    var split = AmortizationCalculator.Split(
                        openingBalance,
                        loan.AnnualInterestRatePct,
                        loan.MonthlyPrincipalInterest,
                        loan.MonthlyEscrow);

                    var dueDay = Math.Min(
                        loan.DayOfMonthDue <= 0 ? 1 : loan.DayOfMonthDue,
                        DateTime.DaysInMonth(periodMonth.Year, periodMonth.Month));
                    var dueDate = new DateTime(
                        periodMonth.Year, periodMonth.Month, dueDay, 0, 0, 0, DateTimeKind.Utc);

                    _db.LoanPayments.Add(new LoanPayment
                    {
                        PortfolioId = loan.PortfolioId,
                        LoanId = loan.Id,
                        PeriodKey = periodKey,
                        DueDate = dueDate,
                        InterestAmount = split.Interest,
                        PrincipalAmount = split.Principal,
                        EscrowAmount = split.Escrow,
                        TotalAmount = split.Total,
                        BalanceAfter = split.BalanceAfter,
                        Status = LoanPaymentStatus.Scheduled,
                        PaymentDoesNotCoverInterest = split.DoesNotCoverInterest,
                        CreatedAt = _timeProvider.UtcNow(),
                    });

                    loanCreated++;
                    lastBalanceAfter = split.BalanceAfter;
                    openingBalance = split.BalanceAfter;

                    if (split.PaidOff)
                    {
                        paidOff = true;
                        break; // loan closed — stop generating further periods
                    }
                }

                ClearClaim(loan);
                if (loanCreated == 0) continue;

                // Update the derived cache. The authoritative figures live in LoanPayment rows.
                loan.CurrentBalance = lastBalanceAfter;
                loan.UpdatedAt = _timeProvider.UtcNow();
                if (paidOff) loan.Status = LoanStatus.PaidOff;
                created += loanCreated;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return created;
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "Failed debt-service batch; leases will expire for retry");
            return 0;
        }
    }

    private static void ClearClaim(Loan loan)
    {
        loan.WorkerClaimOwner = null;
        loan.WorkerClaimToken = null;
        loan.WorkerClaimExpiresAtUtc = null;
    }

    /// <summary>Whole calendar months from <paramref name="from"/> to <paramref name="to"/> (both 1st-of-month).</summary>
    private static int MonthsBetween(DateTime from, DateTime to) =>
        (to.Year - from.Year) * 12 + (to.Month - from.Month);

    /// <summary>1-based period index of a "yyyy-MM" key relative to the loan's start month.</summary>
    private static int PeriodIndexFromKey(string periodKey, DateTime startMonth)
    {
        // PeriodKey is always "yyyy-MM" (written above); parse defensively.
        if (periodKey.Length >= 7 &&
            int.TryParse(periodKey.AsSpan(0, 4), out var year) &&
            int.TryParse(periodKey.AsSpan(5, 2), out var month))
        {
            var keyMonth = new DateTime(year, month, 1);
            return MonthsBetween(startMonth, keyMonth) + 1;
        }
        return 0;
    }
}
