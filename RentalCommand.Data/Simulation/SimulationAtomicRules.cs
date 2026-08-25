using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;

namespace RentalCommand.Data.Simulation;

public static class SimulationWriteSupport
{
    public static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        RentalCommandDbContext db,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        object write = command switch
        {
            SetSimulationClockCommand value => Build(
                "simulation.clock.set", "simulation.clock.mutation.v1", value,
                new SetSimulationClockRule(db).ExecuteAsync,
                new SetSimulationClockRule(db).AuthorizeReplayAsync),
            AdvanceSimulationClockCommand value => Build(
                "simulation.clock.advance", "simulation.clock.mutation.v1", value,
                new AdvanceSimulationClockRule(db).ExecuteAsync,
                new AdvanceSimulationClockRule(db).AuthorizeReplayAsync),
            FreezeSimulationClockCommand value => Build(
                "simulation.clock.freeze", "simulation.clock.mutation.v1", value,
                new FreezeSimulationClockRule(db).ExecuteAsync,
                new FreezeSimulationClockRule(db).AuthorizeReplayAsync),
            UnfreezeSimulationClockCommand value => Build(
                "simulation.clock.unfreeze", "simulation.clock.mutation.v1", value,
                new UnfreezeSimulationClockRule(db).ExecuteAsync,
                new UnfreezeSimulationClockRule(db).AuthorizeReplayAsync),
            ResetSimulationClockCommand value => Build(
                "simulation.clock.reset", "simulation.clock.mutation.v1", value,
                new ResetSimulationClockRule(db).ExecuteAsync,
                new ResetSimulationClockRule(db).AuthorizeReplayAsync),
            EnqueueSimulationWorkerCommand value => Build(
                "simulation.worker.enqueue", "simulation.worker.enqueue.v1", value,
                new EnqueueSimulationWorkerCommandRule(db).ExecuteAsync,
                new EnqueueSimulationWorkerCommandRule(db).AuthorizeReplayAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return (TransactionalWrite<TCommand, TResult>)write;
    }

    private static TransactionalWrite<TCommand, TResult> Build<TCommand, TResult>(
        string operationName,
        string resultContract,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull => new(
            operationName,  command, resultContract,
            WriteLockPlan.None, executeAsync, authorizeReplayAsync);

}

public sealed class SetSimulationClockRule
{
    private readonly RentalCommandDbContext _db;

    public SetSimulationClockRule(RentalCommandDbContext db) => _db = db;

    public Task<SimulationClockMutationResult> ExecuteAsync(
        SetSimulationClockCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.Mode is not ClockMode.Frozen and not ClockMode.Offset)
        {
            throw new ArgumentException("The simulation clock can only be set to offset or frozen mode.");
        }

        if (command.TimeZoneIdSpecified && !string.IsNullOrWhiteSpace(command.TimeZoneId))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(command.TimeZoneId);
            }
            catch (Exception exception) when (
                exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ArgumentException($"Unknown or invalid timezone '{command.TimeZoneId}'.", exception);
            }
        }

        return SimulationAtomicCommandSupport.MutateClockAsync(
            command,
            _db,
            context,
            (row, now) =>
            {
                row.Mode = command.Mode;
                row.SimAnchorUtc = SimulationAtomicCommandSupport.AsUtc(command.SimAnchorUtc);
                row.RealAnchorUtc = command.Mode == ClockMode.Frozen
                    ? row.SimAnchorUtc
                    : now;
                if (command.TimeZoneIdSpecified)
                {
                    row.TimeZoneId = string.IsNullOrWhiteSpace(command.TimeZoneId)
                        ? null
                        : command.TimeZoneId;
                }
            },
            "Simulation clock set.",
            CapabilityKeys.AccountDestructiveActions,
            ct);
    }

    public Task AuthorizeReplayAsync(
        SetSimulationClockCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.AccountDestructiveActions, ct);
}

public sealed class AdvanceSimulationClockRule
{
    private readonly RentalCommandDbContext _db;

    public AdvanceSimulationClockRule(RentalCommandDbContext db) => _db = db;

    public Task<SimulationClockMutationResult> ExecuteAsync(
        AdvanceSimulationClockCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        SimulationAtomicCommandSupport.MutateClockAsync(
            command,
            _db,
            context,
            (row, now) =>
            {
                var delta = new TimeSpan(command.Days, command.Hours, command.Minutes, command.Seconds);
                if (row.Mode == ClockMode.Real)
                {
                    row.Mode = ClockMode.Offset;
                    row.RealAnchorUtc = now;
                    row.SimAnchorUtc = now;
                }

                row.SimAnchorUtc = SimulationAtomicCommandSupport.AsUtc(row.SimAnchorUtc).Add(delta);
            },
            "Simulation clock advanced.",
            CapabilityKeys.AccountDestructiveActions,
            ct);

    public Task AuthorizeReplayAsync(
        AdvanceSimulationClockCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.AccountDestructiveActions, ct);
}

public sealed class FreezeSimulationClockRule
{
    private readonly RentalCommandDbContext _db;

    public FreezeSimulationClockRule(RentalCommandDbContext db) => _db = db;

    public Task<SimulationClockMutationResult> ExecuteAsync(
        FreezeSimulationClockCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        SimulationAtomicCommandSupport.MutateClockAsync(
            command,
            _db,
            context,
            (row, now) =>
            {
                var simNow = SimulationAtomicCommandSupport.ComputeSimNow(row, now);
                row.Mode = ClockMode.Frozen;
                row.SimAnchorUtc = simNow;
                row.RealAnchorUtc = simNow;
            },
            "Simulation clock frozen.",
            CapabilityKeys.AccountDestructiveActions,
            ct);

    public Task AuthorizeReplayAsync(
        FreezeSimulationClockCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.AccountDestructiveActions, ct);
}

public sealed class UnfreezeSimulationClockRule
{
    private readonly RentalCommandDbContext _db;

    public UnfreezeSimulationClockRule(RentalCommandDbContext db) => _db = db;

    public Task<SimulationClockMutationResult> ExecuteAsync(
        UnfreezeSimulationClockCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        SimulationAtomicCommandSupport.MutateClockAsync(
            command,
            _db,
            context,
            (row, now) =>
            {
                var simNow = SimulationAtomicCommandSupport.ComputeSimNow(row, now);
                row.Mode = ClockMode.Offset;
                row.SimAnchorUtc = simNow;
                row.RealAnchorUtc = now;
            },
            "Simulation clock unfrozen.",
            CapabilityKeys.AccountDestructiveActions,
            ct);

    public Task AuthorizeReplayAsync(
        UnfreezeSimulationClockCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.AccountDestructiveActions, ct);
}

public sealed class ResetSimulationClockRule
{
    private readonly RentalCommandDbContext _db;

    public ResetSimulationClockRule(RentalCommandDbContext db) => _db = db;

    public Task<SimulationClockMutationResult> ExecuteAsync(
        ResetSimulationClockCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        SimulationAtomicCommandSupport.MutateClockAsync(
            command,
            _db,
            context,
            (row, _) =>
            {
                row.Mode = ClockMode.Real;
                row.TimeZoneId = null;
            },
            "Simulation clock reset to real time.",
            CapabilityKeys.AccountDestructiveActions,
            ct);

    public Task AuthorizeReplayAsync(
        ResetSimulationClockCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.AccountDestructiveActions, ct);
}

public sealed class EnqueueSimulationWorkerCommandRule
{
    private readonly RentalCommandDbContext _db;

    public EnqueueSimulationWorkerCommandRule(RentalCommandDbContext db) => _db = db;

    public async Task<EnqueueSimulationWorkerResult> ExecuteAsync(
        EnqueueSimulationWorkerCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        SimulationAtomicCommandSupport.Validate(command);
        if (!SimWorkerKeys.IsKnown(command.WorkerKey))
        {
            throw new ArgumentException($"Unknown worker key '{command.WorkerKey}'.");
        }

        await SimulationAtomicCommandSupport.LockAccessAsync(command, context, ct);
        await context.AcquireLockAsync("SimulationWorkerCommand", command.CommandId, ct);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await SimulationAtomicCommandSupport.IsAuthorizedAsync(
                command, _db, now, CapabilityKeys.SecurityManage, ct))
        {
            throw SimulationAtomicCommandSupport.Denied(CapabilityKeys.SecurityManage);
        }

        var clock = await SimulationAtomicCommandSupport.LoadClockForUpdateAsync(_db, ct);
        var requestedSimUtc = SimulationAtomicCommandSupport.ComputeSimNow(clock, now);
        var row = new SimWorkerCommand
        {
            Id = command.CommandId,
            WorkerKey = command.WorkerKey,
            RequestedSimUtc = requestedSimUtc,
            Status = SimWorkerCommandStatus.Pending,
            CreatedRealUtc = now,
        };

        _db.Add(row);
        context.UseDatabaseWallClockForAudit(now);
        context.StageSemanticEvent(
            new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(SimWorkerCommand),
                0,
                AuditLogOperation.Created,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    row.Id,
                    row.WorkerKey,
                    row.RequestedSimUtc,
                    row.Status,
                    row.CreatedRealUtc,
                }),
                ChangeReason: "Simulation worker command enqueued."),
            now);
        await context.FlushBusinessAsync(ct);

        return new EnqueueSimulationWorkerResult(
            row.Id,
            row.WorkerKey,
            row.Status,
            row.RequestedSimUtc,
            row.CreatedRealUtc);
    }

    public Task AuthorizeReplayAsync(
        EnqueueSimulationWorkerCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        SimulationAtomicCommandSupport.AuthorizeReplayAsync(
            command, _db, CapabilityKeys.SecurityManage, ct);
}

internal static class SimulationAtomicCommandSupport
{
    public static async Task<SimulationClockMutationResult> MutateClockAsync<TCommand>(
        TCommand command,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        Action<SimulationClock, DateTime> mutation,
        string changeReason,
        string requiredCapability,
        CancellationToken ct)
        where TCommand : ISimulationAtomicCommand
    {
        Validate(command);
        await LockAccessAsync(command, context, ct);
        await context.AcquireLockAsync("SimulationClock", 1, ct);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, db, now, requiredCapability, ct))
        {
            throw Denied(requiredCapability);
        }

        var row = await LoadClockForUpdateAsync(db, ct);
        mutation(row, now);
        row.UpdatedAtRealUtc = now;

        context.UseDatabaseWallClockForAudit(now);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(SimulationClock),
            row.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                row.Mode,
                row.SimAnchorUtc,
                row.RealAnchorUtc,
                row.TimeZoneId,
                row.UpdatedAtRealUtc,
            }),
            ChangeReason: changeReason),
            now);
        await context.FlushBusinessAsync(ct);

        return ToResult(row, now);
    }

    public static async Task AuthorizeReplayAsync(
        ISimulationAtomicCommand command,
        RentalCommandDbContext db,
        string requiredCapability,
        CancellationToken ct)
    {
        Validate(command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await IsAuthorizedAsync(command, db, now, requiredCapability, ct))
        {
            throw Denied(requiredCapability);
        }
    }

    public static void Validate(ISimulationAtomicCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0)
        {
            throw new ArgumentException(
                "Portfolio, actor, session, and access context are required.");
        }
    }

    public static async Task LockAccessAsync(
        ISimulationAtomicCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
    }

    public static Task<bool> IsAuthorizedAsync(
        ISimulationAtomicCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string requiredCapability,
        CancellationToken ct) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == requiredCapability
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Workspace), ct);

    public static async Task<SimulationClock> LoadClockForUpdateAsync(
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        await db.Database.SqlQuery<int>($"""
            SELECT clock."Id" AS "Value"
            FROM "SimulationClocks" AS clock
            WHERE clock."Id" = 1
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

        var row = await db.Set<SimulationClock>()
            .SingleOrDefaultAsync(clock => clock.Id == 1, ct);
        if (row is not null)
        {
            return row;
        }

        row = new SimulationClock { Id = 1, Mode = ClockMode.Real };
        db.Add(row);
        return row;
    }

    public static SimulationClockMutationResult ToResult(SimulationClock row, DateTime realNowUtc)
    {
        var simNowUtc = ComputeSimNow(row, realNowUtc);
        var offsetSeconds = Math.Round((simNowUtc - realNowUtc).TotalSeconds, 3);
        return new SimulationClockMutationResult(
            simNowUtc,
            row.Mode.ToString(),
            row.TimeZoneId,
            offsetSeconds);
    }

    public static DateTime ComputeSimNow(SimulationClock row, DateTime realNowUtc) =>
        row.Mode switch
        {
            ClockMode.Real => AsUtc(realNowUtc),
            ClockMode.Frozen => AsUtc(row.SimAnchorUtc),
            ClockMode.Offset => AsUtc(row.SimAnchorUtc) + (AsUtc(realNowUtc) - AsUtc(row.RealAnchorUtc)),
            _ => AsUtc(realNowUtc),
        };

    public static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime(),
        };

    public static UnauthorizedAccessException Denied(string capability) => new(
        $"Workspace access changed or no longer grants '{capability}'. Refresh and try again.");
}
