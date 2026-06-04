using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Owns the account-wide Sandbox/Live lifecycle for a single portfolio (the account).
///
/// Model: GRADUATE-ONCE → WIPE DEMO. New signups start in Sandbox seeded with demo data. "Go Live" is a
/// one-way graduation: it deletes the portfolio's data, flips <c>IsSandbox</c> to false, and clears
/// <c>SandboxSeededAtUtc</c>. There is no path back into Sandbox afterwards. All operations are strictly
/// portfolio-scoped — a caller can only read/graduate their own portfolio (IDOR-safe).
/// </summary>
public interface ISandboxService
{
    /// <summary>Returns the current sandbox state for the caller's portfolio, or null if it does not exist.</summary>
    Task<SandboxStateResponse?> GetStateAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// One-way "Go Live": if the portfolio is currently Sandbox, deletes all of its portfolio-scoped data,
    /// sets <c>IsSandbox = false</c>, and clears <c>SandboxSeededAtUtc</c>. Transactional and idempotent —
    /// calling it on an already-Live portfolio is a no-op that simply returns the Live state. Only ever
    /// affects the passed (caller's own) portfolio.
    /// </summary>
    Task<SandboxStateResponse?> GoLiveAsync(int portfolioId, CancellationToken ct = default);
}
