namespace RentalCommand.Core.Services;

/// <summary>
/// One period's principal/interest/escrow split in a loan amortization schedule.
/// All amounts are decimal, rounded to cents.
/// </summary>
/// <param name="OpeningBalance">Principal outstanding at the start of the period.</param>
/// <param name="Interest">Interest accrued for the period (deductible).</param>
/// <param name="Principal">Principal repaid this period (reduces the balance; not deductible).</param>
/// <param name="Escrow">Escrow paid this period (taxes/insurance cash).</param>
/// <param name="Total">Actual cash that left the account this period (P + I + escrow).</param>
/// <param name="BalanceAfter">Principal outstanding after the period (next period's opening balance).</param>
/// <param name="PaidOff">True when this payment closed the loan (balance hit zero).</param>
/// <param name="DoesNotCoverInterest">
/// True when the scheduled P&amp;I did not cover the period's interest (negative-amortization guard,
/// spec §18): principal was forced to 0 and the balance left unchanged.
/// </param>
public readonly record struct AmortizationSplit(
    decimal OpeningBalance,
    decimal Interest,
    decimal Principal,
    decimal Escrow,
    decimal Total,
    decimal BalanceAfter,
    bool PaidOff,
    bool DoesNotCoverInterest);

/// <summary>
/// One materialized row of an amortization schedule: an <see cref="AmortizationSplit"/> plus the
/// schedule-level flag for a balance still outstanding when the term is reached.
/// </summary>
public readonly record struct AmortizationRow(
    int PeriodIndex,
    decimal OpeningBalance,
    decimal Interest,
    decimal Principal,
    decimal Escrow,
    decimal Total,
    decimal BalanceAfter,
    bool PaidOff,
    bool DoesNotCoverInterest,
    bool BalanceRemainingAtMaturity);

/// <summary>
/// Pure amortization math for fixed-rate, fully-amortizing mortgages (spec §5, with the §18
/// financial-review corrections). No I/O, no DB — every figure is decimal and rounded to cents so a
/// filed interest number is exact and reproducible.
///
/// <para>The split for a period is deterministic given its <b>opening balance</b>, the annual rate,
/// the scheduled P&amp;I, and escrow. The schedule (<see cref="BuildSchedule"/>) chains these so each
/// period opens from the prior period's <see cref="AmortizationSplit.BalanceAfter"/> — the immutable
/// opening-balance rule (§18): never the live, user-editable loan balance.</para>
/// </summary>
public static class AmortizationCalculator
{
    /// <summary>
    /// Computes one period's split from its opening balance. Applies the §18 corrections:
    /// negative-amortization guard (payment ≤ interest → principal 0, balance unchanged, flagged) and
    /// final-payment close (computed principal ≥ balance → pay off, balance 0).
    /// </summary>
    public static AmortizationSplit Split(
        decimal openingBalance,
        decimal annualRatePct,
        decimal monthlyPrincipalInterest,
        decimal monthlyEscrow)
    {
        var balance = openingBalance < 0m ? 0m : openingBalance;
        var escrow = Round(monthlyEscrow);

        // Monthly interest on the opening balance, rounded to cents.
        var monthlyRate = annualRatePct / 100m / 12m;
        var interest = Round(balance * monthlyRate);

        // ── Negative-amortization guard (§18) ───────────────────────────────────────────────────
        // If the scheduled P&I does not cover the interest, force principal to 0 (interest-only) and
        // leave the balance unchanged — it may never grow. Flag it so the owner sees a bad payment.
        if (interest >= monthlyPrincipalInterest)
        {
            return new AmortizationSplit(
                OpeningBalance: balance,
                Interest: interest,
                Principal: 0m,
                Escrow: escrow,
                // Actual cash that left the account is the scheduled P&I (under-covering interest) + escrow.
                Total: Round(monthlyPrincipalInterest) + escrow,
                BalanceAfter: balance,
                PaidOff: balance == 0m,
                DoesNotCoverInterest: true);
        }

        var principal = Round(monthlyPrincipalInterest - interest);

        // ── Final-payment close (§5) ────────────────────────────────────────────────────────────
        // When the computed principal would meet or exceed the remaining balance, this is the final
        // payment: pay exactly the remaining balance, zero the loan, mark it paid off. Never negative.
        if (principal >= balance)
        {
            principal = balance;
            return new AmortizationSplit(
                OpeningBalance: balance,
                Interest: interest,
                Principal: principal,
                Escrow: escrow,
                Total: interest + principal + escrow,
                BalanceAfter: 0m,
                PaidOff: true,
                DoesNotCoverInterest: false);
        }

        var balanceAfter = balance - principal;
        return new AmortizationSplit(
            OpeningBalance: balance,
            Interest: interest,
            Principal: principal,
            Escrow: escrow,
            Total: interest + principal + escrow,
            BalanceAfter: balanceAfter,
            PaidOff: false,
            DoesNotCoverInterest: false);
    }

    /// <summary>
    /// Builds the full immutable amortization schedule from origination, chaining each period's
    /// opening balance from the prior <see cref="AmortizationSplit.BalanceAfter"/>. Stops at the
    /// earlier of payoff or <paramref name="termMonths"/> (the maturity stop, §18): it never emits a
    /// period past the term. If a balance still remains at the final term month, that row is flagged
    /// <see cref="AmortizationRow.BalanceRemainingAtMaturity"/> rather than amortizing forever.
    /// </summary>
    public static IReadOnlyList<AmortizationRow> BuildSchedule(
        decimal originalAmount,
        decimal annualRatePct,
        decimal monthlyPrincipalInterest,
        decimal monthlyEscrow,
        int termMonths)
    {
        var rows = new List<AmortizationRow>(Math.Max(0, termMonths));
        if (termMonths <= 0)
            return rows;

        var balance = Round(originalAmount);

        for (var period = 1; period <= termMonths; period++)
        {
            var split = Split(balance, annualRatePct, monthlyPrincipalInterest, monthlyEscrow);
            var atTerm = period == termMonths;
            var balanceRemainingAtMaturity = atTerm && split.BalanceAfter > 0m && !split.PaidOff;

            rows.Add(new AmortizationRow(
                PeriodIndex: period,
                OpeningBalance: split.OpeningBalance,
                Interest: split.Interest,
                Principal: split.Principal,
                Escrow: split.Escrow,
                Total: split.Total,
                BalanceAfter: split.BalanceAfter,
                PaidOff: split.PaidOff,
                DoesNotCoverInterest: split.DoesNotCoverInterest,
                BalanceRemainingAtMaturity: balanceRemainingAtMaturity));

            if (split.PaidOff)
                break; // stop at payoff — no zero rows out to the term

            balance = split.BalanceAfter;
        }

        return rows;
    }

    /// <summary>Banker's-free, half-away-from-zero rounding to cents (matches money storage).</summary>
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
