namespace RentalCommand.Core.Entities;

/// <summary>
/// Dev-only command queue (non-production) bridging the API and Engine for on-demand scheduled-job runs
/// under the simulation clock. The API enqueues a <c>Pending</c> row — it cannot call the Engine's
/// automation services directly (project dependency is <c>Engine → Api</c>, and the Engine has no HTTP
/// port) — and the dev-only <c>SimWorkerCommandWorker</c> in the Engine claims and runs it in the
/// Engine's native admin-RLS + system-actor context, then writes the result back for the API's long-poll.
///
/// <para>Guid PK (a dev queue table; intentionally off the int-key domain convention). Global (no
/// PortfolioId): it MUST be excluded from the tenant_isolation RLS policy set, exactly like
/// <see cref="SimulationClock"/>.</para>
/// </summary>
public class SimWorkerCommand
{
    public Guid Id { get; set; }

    /// <summary>Worker key (see <c>SimWorkerKeys</c>) — an individual job or <c>run-due</c>.</summary>
    public string WorkerKey { get; set; } = string.Empty;

    /// <summary>The simulated "now" at enqueue time (recorded for audit/debugging).</summary>
    public DateTime RequestedSimUtc { get; set; }

    /// <summary>Lifecycle state — see <see cref="SimWorkerCommandStatus"/>.</summary>
    public string Status { get; set; } = SimWorkerCommandStatus.Pending;

    /// <summary>Engine lease metadata. Terminal writes are fenced by <see cref="ClaimToken"/>.</summary>
    public string? ClaimOwner { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>On success, the JSON result (e.g. <c>{"created":3}</c>).</summary>
    public string? ResultJson { get; set; }

    /// <summary>On failure, the error message.</summary>
    public string? Error { get; set; }

    public DateTime CreatedRealUtc { get; set; }

    public DateTime? CompletedRealUtc { get; set; }
}

/// <summary>Lifecycle states for <see cref="SimWorkerCommand.Status"/> (stored as plain strings).</summary>
public static class SimWorkerCommandStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Error = "Error";
}
