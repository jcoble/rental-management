using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Dev-only worker command-bridge surface (spec §6.2) at <c>api/v1/dev/workers</c>. Because the API
/// cannot call the Engine's automation services directly (dependency is <c>Engine → Api</c>, and the
/// Engine has no HTTP port), the API <b>enqueues a <see cref="SimWorkerCommand"/></b> and long-polls the
/// row for a terminal state; the Engine's dev-only <c>SimWorkerCommandWorker</c> executes it cross-process
/// in its native admin-RLS + system-actor context and writes the result back.
///
/// <para>Mapped ONLY when simulation is active (<see cref="SimulationOnlyAttribute"/>); admin-gated.</para>
/// </summary>
[ApiController]
[Route("api/v1/dev/workers")]
[SimulationOnly]
[Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.SecurityManage)]
[Produces("application/json")]
public sealed class DevWorkersController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<EnqueueSimulationWorkerResult> EnqueueWorkerCodec =
        new("simulation.worker.enqueue.v1");
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;

    public DevWorkersController(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _atomic = atomic;
    }

    /// <summary>Enqueue one automation job and wait (long-poll) for it to finish.</summary>
    [HttpPost("{key}/run-once")]
    public async Task<ActionResult<SimWorkerCommandResponse>> RunOnce(
        string key,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!SimWorkerKeys.All.Contains(key))
            return BadRequest(new { error = $"Unknown worker key '{key}'.", validKeys = SimWorkerKeys.All });

        return await EnqueueAndAwaitAsync(key, idempotencyKey, ct);
    }

    /// <summary>Enqueue the full due-order batch (see <see cref="SimWorkerKeys.RunDueSequence"/>) and wait.</summary>
    [HttpPost("run-due")]
    public Task<ActionResult<SimWorkerCommandResponse>> RunDue(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
        => EnqueueAndAwaitAsync(SimWorkerKeys.RunDue, idempotencyKey, ct);

    /// <summary>Poll a single command's status/result (non-blocking).</summary>
    [HttpGet("commands/{id:guid}")]
    public async Task<ActionResult<SimWorkerCommandResponse>> GetCommand(Guid id, CancellationToken ct)
    {
        var row = await _db.SimWorkerCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? NotFound() : Ok(ToResponse(row));
    }

    private async Task<ActionResult<SimWorkerCommandResponse>> EnqueueAndAwaitAsync(
        string key,
        string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var deliveryKey))
        {
            return BadRequest(new { error = "Idempotency-Key header is required and must be 128 characters or fewer." });
        }

        if (!TryGetActiveAccessContext(out var access))
        {
            return Forbid();
        }

        var commandId = BuildCommandId(access, key, deliveryKey);
        var command = new EnqueueSimulationWorkerCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            commandId,
            key);

        EnqueueSimulationWorkerResult enqueue;
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("simulation.worker.enqueue", BuildIdentityKey(access, deliveryKey)),
                command,
                EnqueueWorkerCodec,
                ct);
            enqueue = outcome.Value;
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }

        // Long-poll the row for a terminal state — the Engine worker runs it cross-process. Poll timing is
        // on the REAL clock so the timeout still fires under a frozen sim clock.
        var deadline = TimeProvider.System.GetUtcNow() + LongPollTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(LongPollInterval, ct);

            var row = await _db.SimWorkerCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == enqueue.CommandId, ct);
            if (row is not null
                && (row.Status == SimWorkerCommandStatus.Done || row.Status == SimWorkerCommandStatus.Error))
            {
                return Ok(ToResponse(row));
            }
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "Engine command worker not responding (timed out waiting for the command to complete).",
            commandId = enqueue.CommandId,
        });
    }

    private static SimWorkerCommandResponse ToResponse(SimWorkerCommand row)
    {
        JsonElement? result = null;
        if (!string.IsNullOrWhiteSpace(row.ResultJson))
        {
            try { result = JsonSerializer.Deserialize<JsonElement>(row.ResultJson); }
            catch (JsonException) { /* leave null on a malformed dev payload */ }
        }

        return new SimWorkerCommandResponse(
            row.Id, row.WorkerKey, row.Status, row.RequestedSimUtc,
            result, row.Error, row.CreatedRealUtc, row.CompletedRealUtc);
    }

    private static Guid BuildCommandId(ActiveAccessContext access, string workerKey, string idempotencyKey)
    {
        var payload = $"simulation-worker:{access.PortfolioId}:{access.UserId}:{workerKey}:{idempotencyKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new Guid(hash[..16]);
    }

    private static string BuildIdentityKey(ActiveAccessContext access, string idempotencyKey) =>
        $"{access.PortfolioId}:{access.UserId}:{idempotencyKey}";
}

/// <summary>A command's status + result. <c>Result</c> is the parsed <c>ResultJson</c> (e.g. <c>{"created":3}</c>).</summary>
public sealed record SimWorkerCommandResponse(
    Guid Id,
    string WorkerKey,
    string Status,
    DateTime RequestedSimUtc,
    JsonElement? Result,
    string? Error,
    DateTime CreatedRealUtc,
    DateTime? CompletedRealUtc);
