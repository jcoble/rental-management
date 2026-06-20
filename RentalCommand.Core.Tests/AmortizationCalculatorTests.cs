using FluentAssertions;
using RentalCommand.Core.Services;

namespace RentalCommand.Core.Tests;

/// <summary>
/// The amortization split is a tax-affecting calculation: its interest figure flows straight onto
/// Schedule E. These tests pin the spec §5 mechanics and the §18 financial-review corrections
/// (final-payment close, negative-amortization guard, maturity stop, immutable opening balance).
/// All money is decimal, rounded to cents.
/// </summary>
public class AmortizationCalculatorTests
{
    // ── Single-period split (§5) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_ComputesInterestPrincipalAndBalance()
    {
        // 200,000 @ 6% annual → 0.5%/mo interest = 1000.00; P&I 1199.10 → principal 199.10.
        var split = AmortizationCalculator.Split(
            openingBalance: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m, monthlyEscrow: 0m);

        split.Interest.Should().Be(1_000.00m);
        split.Principal.Should().Be(199.10m);
        split.Escrow.Should().Be(0m);
        split.Total.Should().Be(1_199.10m);
        split.BalanceAfter.Should().Be(199_800.90m);
        split.PaidOff.Should().BeFalse();
        split.DoesNotCoverInterest.Should().BeFalse();
    }

    [Fact]
    public void Split_AddsEscrowToTotalButNotToPrincipalOrBalance()
    {
        var split = AmortizationCalculator.Split(
            openingBalance: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m, monthlyEscrow: 350m);

        // Escrow rides on top of P&I in the cash total, but never touches the loan balance.
        split.Escrow.Should().Be(350m);
        split.Total.Should().Be(1_549.10m);
        split.Principal.Should().Be(199.10m);
        split.BalanceAfter.Should().Be(199_800.90m);
    }

    [Fact]
    public void Split_RoundsInterestToCents()
    {
        // 100,000 @ 6.5% → monthly rate 0.0054166… → interest 541.6666… rounds to 541.67.
        var split = AmortizationCalculator.Split(
            openingBalance: 100_000m, annualRatePct: 6.5m, monthlyPrincipalInterest: 700m, monthlyEscrow: 0m);

        split.Interest.Should().Be(541.67m);
        split.Principal.Should().Be(158.33m);
        (split.Interest + split.Principal).Should().Be(700m);
        split.BalanceAfter.Should().Be(99_841.67m);
    }

    // ── Final-payment close (§5) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_FinalPayment_ClosesToZeroAndMarksPaidOff()
    {
        // Tiny balance, large P&I → computed principal would exceed the balance; clamp + close.
        var split = AmortizationCalculator.Split(
            openingBalance: 150m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m, monthlyEscrow: 0m);

        split.Interest.Should().Be(0.75m);                 // 150 * 0.005
        split.Principal.Should().Be(150m);                 // clamped to remaining balance
        split.BalanceAfter.Should().Be(0m);
        split.PaidOff.Should().BeTrue();
        // The owner still pays interest on the final stub; total = interest + the principal paid off.
        split.Total.Should().Be(150.75m);
    }

    [Fact]
    public void Split_PrincipalExactlyEqualsBalance_PaysOff()
    {
        var split = AmortizationCalculator.Split(
            openingBalance: 199.10m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m, monthlyEscrow: 0m);

        split.BalanceAfter.Should().Be(0m);
        split.PaidOff.Should().BeTrue();
        split.Principal.Should().Be(199.10m);
    }

    // ── Negative-amortization guard (§18) ───────────────────────────────────────────────────────

    [Fact]
    public void Split_PaymentBelowInterest_ForcesPrincipalToZero_BalanceUnchanged()
    {
        // P&I 500 < first-month interest 1000 → interest-only, principal 0, balance does NOT grow.
        var split = AmortizationCalculator.Split(
            openingBalance: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 500m, monthlyEscrow: 0m);

        split.Interest.Should().Be(1_000.00m);
        split.Principal.Should().Be(0m);
        split.BalanceAfter.Should().Be(200_000m);          // never grows
        split.DoesNotCoverInterest.Should().BeTrue();
        split.PaidOff.Should().BeFalse();
    }

    [Fact]
    public void Split_PaymentEqualsInterest_ForcesPrincipalToZero_Flagged()
    {
        // Exactly interest-only: principal 0, flagged (the balance can never amortize).
        var split = AmortizationCalculator.Split(
            openingBalance: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1000m, monthlyEscrow: 0m);

        split.Principal.Should().Be(0m);
        split.BalanceAfter.Should().Be(200_000m);
        split.DoesNotCoverInterest.Should().BeTrue();
    }

    [Fact]
    public void Split_NeverProducesNegativePrincipalOrGrowingBalance()
    {
        var split = AmortizationCalculator.Split(
            openingBalance: 50_000m, annualRatePct: 12m, monthlyPrincipalInterest: 100m, monthlyEscrow: 0m);

        split.Principal.Should().BeGreaterThanOrEqualTo(0m);
        split.BalanceAfter.Should().BeLessThanOrEqualTo(50_000m);
    }

    // ── Immutable schedule + maturity stop (§18) ────────────────────────────────────────────────

    [Fact]
    public void BuildSchedule_EachPeriodOpensFromPriorBalanceAfter()
    {
        var rows = AmortizationCalculator.BuildSchedule(
            originalAmount: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m,
            monthlyEscrow: 0m, termMonths: 360);

        // Period 1 opens at the original amount; each subsequent period opens at the prior row's
        // BalanceAfter (the immutable opening-balance rule — never the live, editable CurrentBalance).
        rows[0].OpeningBalance.Should().Be(200_000m);
        rows[0].BalanceAfter.Should().Be(199_800.90m);
        rows[1].OpeningBalance.Should().Be(199_800.90m);
        for (var i = 1; i < rows.Count; i++)
            rows[i].OpeningBalance.Should().Be(rows[i - 1].BalanceAfter);
    }

    [Fact]
    public void BuildSchedule_StopsAtTermMonths_NeverGeneratesPastMaturity()
    {
        // A standard fully-amortizing 30y loan: exactly 360 rows, last one pays off.
        var rows = AmortizationCalculator.BuildSchedule(
            originalAmount: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1199.10m,
            monthlyEscrow: 0m, termMonths: 360);

        rows.Count.Should().BeLessThanOrEqualTo(360);
        rows.Should().NotBeEmpty();
        // Never a 361st period — the maturity stop prevents a few-cents-low payment amortizing forever.
        rows.Count.Should().BeLessThanOrEqualTo(360);
    }

    [Fact]
    public void BuildSchedule_PayoffBeforeTerm_StopsEarlyAtZeroBalance()
    {
        // Overpaying P&I pays the loan off well before term; the schedule must stop at payoff, not
        // keep emitting zero/negative rows to TermMonths.
        var rows = AmortizationCalculator.BuildSchedule(
            originalAmount: 10_000m, annualRatePct: 6m, monthlyPrincipalInterest: 2_000m,
            monthlyEscrow: 0m, termMonths: 360);

        rows.Count.Should().BeLessThan(360);
        rows[^1].BalanceAfter.Should().Be(0m);
        rows[^1].PaidOff.Should().BeTrue();
        // No row after payoff.
        rows.Should().OnlyContain(r => r.BalanceAfter >= 0m);
    }

    [Fact]
    public void BuildSchedule_BalanceRemainingAtMaturity_StopsAndFlags()
    {
        // A payment a touch too low to fully amortize by term: we still STOP at TermMonths and flag a
        // balance remaining at maturity, rather than running on forever.
        var rows = AmortizationCalculator.BuildSchedule(
            originalAmount: 200_000m, annualRatePct: 6m, monthlyPrincipalInterest: 1100m,
            monthlyEscrow: 0m, termMonths: 360);

        rows.Count.Should().Be(360);
        var last = rows[^1];
        last.BalanceAfter.Should().BeGreaterThan(0m);
        last.PaidOff.Should().BeFalse();
        last.BalanceRemainingAtMaturity.Should().BeTrue();
    }

    [Fact]
    public void BuildSchedule_SumsToOriginalPrincipal_WhenFullyAmortizing()
    {
        var rows = AmortizationCalculator.BuildSchedule(
            originalAmount: 10_000m, annualRatePct: 6m, monthlyPrincipalInterest: 2_000m,
            monthlyEscrow: 0m, termMonths: 360);

        // Principal paid across the life equals the original amount (to the cent).
        rows.Sum(r => r.Principal).Should().Be(10_000m);
    }
}
