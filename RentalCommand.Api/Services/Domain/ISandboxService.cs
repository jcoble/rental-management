using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

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
    Task<SandboxStateResponse?> GoLiveAsync(
        WorkspaceReadScope scope,
        string idempotencyKey,
        CancellationToken ct = default);

    /// <summary>
    /// Records the first-login Sandbox-vs-Live decision for the caller's own portfolio:
    /// <list type="bullet">
    ///   <item><see cref="OnboardingChoice.Sandbox"/> flips the portfolio to Sandbox
    ///   (<c>IsSandbox = true</c>, <c>SandboxSeededAtUtc</c> stamped).</item>
    ///   <item><see cref="OnboardingChoice.Live"/> leaves the portfolio empty and Live (<c>IsSandbox =
    ///   false</c>) — the empty real portfolio the user sets up by hand.</item>
    /// </list>
    /// Idempotent: once a non-pending choice is persisted, calling it again is a no-op that returns the
    /// current state (it never wipes). Only ever affects the passed (caller's own) portfolio. Returns
    /// null if the portfolio does not exist.
    /// </summary>
    Task<SandboxStateResponse?> ApplyOnboardingChoiceAsync(
        WorkspaceReadScope scope,
        OnboardingChoice choice,
        string idempotencyKey,
        CancellationToken ct = default);
}
