namespace RentalCommand.Engine.Services;

/// <summary>
/// Generates monthly <see cref="Core.Entities.LoanPayment"/> rows for active loans, idempotently
/// (one row per loan per billing period), computing the principal/interest/escrow split from the
/// immutable prior balance and stopping at loan maturity.
/// </summary>
public interface IDebtServiceService
{
    /// <summary>
    /// For each active loan, generate any missing scheduled <c>LoanPayment</c> rows up to the current
    /// billing period (catching up if the worker missed months), each chained from the prior period's
    /// closing balance, never past the loan term. Updates the loan's cached balance/status. Returns
    /// the number of new payments created this run.
    /// </summary>
    Task<int> GenerateAsync(CancellationToken ct = default);
}
