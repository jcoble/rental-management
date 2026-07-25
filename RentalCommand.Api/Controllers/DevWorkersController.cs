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
public sealed class DevWorkersController : ControllerBase
{
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicInfrastructureUnitOfWork _infrastructure;

    public DevWorkersController(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAtomicInfrastructureUnitOfWork infrastructure)
    {
        _db = db;
        _timeProvider = timeProvider;
        _infrastructure = infrastructure;
    }

    /// <summary>Enqueue one automation job and wait (long-poll) for it to finish.</summary>
    [HttpPost("{key}/run-once")]
    public async Task<ActionResult<SimWorkerCommandResponse>> RunOnce(string key, CancellationToken ct)
    {
        if (!SimWorkerKeys.All.Contains(key))
            return BadRequest(new { error = $"Unknown worker key '{key}'.", validKeys = SimWorkerKeys.All });

        return await EnqueueAndAwaitAsync(key, ct);
    }

    /// <summary>Enqueue the full due-order batch (see <see cref="SimWorkerKeys.RunDueSequence"/>) and wait.</summary>
    [HttpPost("run-due")]
    public Task<ActionResult<SimWorkerCommandResponse>> RunDue(CancellationToken ct)
        => EnqueueAndAwaitAsync(SimWorkerKeys.RunDue, ct);

    /// <summary>Poll a single command's status/result (non-blocking).</summary>
    [HttpGet("commands/{id:guid}")]
    public async Task<ActionResult<SimWorkerCommandResponse>> GetCommand(Guid id, CancellationToken ct)
    {
        var row = await _db.SimWorkerCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? NotFound() : Ok(ToResponse(row));
    }

    private async Task<ActionResult<SimWorkerCommandResponse>> EnqueueAndAwaitAsync(string key, CancellationToken ct)
    {
        var command = new SimWorkerCommand
        {
            Id = Guid.NewGuid(),
            WorkerKey = key,
            RequestedSimUtc = _timeProvider.GetUtcNow().UtcDateTime,   // the (usually frozen) sim instant
            Status = SimWorkerCommandStatus.Pending,
            CreatedRealUtc = TimeProvider.System.GetUtcNow().UtcDateTime,
        };
        await _infrastructure.ExecuteAsync(
            AtomicInfrastructureOperation.SimulationWorkerCommand,
            _ =>
            {
                _db.SimWorkerCommands.Add(command);
                return Task.CompletedTask;
            },
            ct);

        // Long-poll the row for a terminal state — the Engine worker runs it cross-process. Poll timing is
        // on the REAL clock so the timeout still fires under a frozen sim clock.
        var deadline = TimeProvider.System.GetUtcNow() + LongPollTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(LongPollInterval, ct);

            var row = await _db.SimWorkerCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == command.Id, ct);
            if (row is not null
                && (row.Status == SimWorkerCommandStatus.Done || row.Status == SimWorkerCommandStatus.Error))
            {
                return Ok(ToResponse(row));
            }
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "Engine command worker not responding (timed out waiting for the command to complete).",
            commandId = command.Id,
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
