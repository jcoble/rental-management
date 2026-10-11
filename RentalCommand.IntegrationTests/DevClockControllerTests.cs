using Microsoft.AspNetCore.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Simulation;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Drives <see cref="DevClockController"/> against a real Postgres (Testcontainers): set → GET round-trip,
/// advance moves the clock, reset returns to real. The controller mutates the single SimulationClock row
/// and refreshes the in-memory <see cref="ClockStateProvider"/>; <see cref="SimulationTimeProvider"/> then
/// computes "now" from it — the full backend clock loop against real DB persistence.
/// </summary>
public sealed class DevClockControllerTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _pg;
    private bool _dockerAvailable;
    private string _conn = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _conn = _pg.GetConnectionString();
        await using var ctx = NewContext(_conn);
        await ctx.Database.MigrateAsync(); // creates the schema + seeds SimulationClock row 1 (Real)
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Set_Advance_Reset_RoundTripsThroughPostgres()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; DevClockController Postgres round-trip skipped.");

        await using var provider = CreateAtomicProvider();
        var access = await SeedAdministratorAccessAsync(provider);

        var clockState = new ClockStateProvider(provider.GetRequiredService<IServiceScopeFactory>());
        var timeProvider = new SimulationTimeProvider(clockState);
        await clockState.RefreshAsync();
        clockState.Current.Mode.Should().Be(ClockMode.Real); // seeded state

        await using var requestScope = provider.CreateAsyncScope();
        var controller = new DevClockController(
            timeProvider,
            clockState,
            new FixedTimeZoneProvider("America/New_York"),
            requestScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            requestScope.ServiceProvider.GetRequiredService<IWriteExecutor>());
        InstallAccessContext(controller, access);

        var jan1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // set (frozen) → the clock reports exactly that instant
        var set = Body(await controller.Set(
            new SetClockRequest(jan1, null, "America/New_York", "frozen"),
            "clock-set-jan1",
            default));
        set.Mode.Should().Be("Frozen");
        set.SimNowUtc.Should().Be(jan1);
        set.TimeZoneId.Should().Be("America/New_York");

        // GET reflects it (fresh in-memory read)
        var got = Body(controller.Get());
        got.SimNowUtc.Should().Be(jan1);
        got.Mode.Should().Be("Frozen");

        // advance 35 days → Jan 1 + 35d = Feb 5, still frozen
        var advanced = Body(await controller.Advance(
            new AdvanceClockRequest(35, 0, 0, 0),
            "clock-advance-35",
            default));
        advanced.SimNowUtc.Should().Be(new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc));
        advanced.Mode.Should().Be("Frozen");

        // reset → real time again, timezone override cleared
        var reset = Body(await controller.Reset("clock-reset", default));
        reset.Mode.Should().Be("Real");
        reset.TimeZoneId.Should().BeNull();
        reset.SimNowUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task Set_DateOnly_AnchorsAtStartOfSelectedBusinessDate()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; DevClockController Postgres round-trip skipped.");

        await using var provider = CreateAtomicProvider();
        var access = await SeedAdministratorAccessAsync(provider);

        var clockState = new ClockStateProvider(provider.GetRequiredService<IServiceScopeFactory>());
        var timeProvider = new SimulationTimeProvider(clockState);
        await clockState.RefreshAsync();

        await using var requestScope = provider.CreateAsyncScope();
        var controller = new DevClockController(
            timeProvider,
            clockState,
            new FixedTimeZoneProvider("America/New_York"),
            requestScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            requestScope.ServiceProvider.GetRequiredService<IWriteExecutor>());
        InstallAccessContext(controller, access);

        var set = Body(await controller.Set(
            new SetClockRequest(null, "2027-01-03", null, "frozen"),
            "clock-set-business-date",
            default));

        set.SimNowUtc.Should().Be(new DateTime(2027, 1, 3, 5, 0, 0, DateTimeKind.Utc));
    }

    [SkippableFact]
    public async Task Set_ReplaysExactReceiptResult_AndDoesNotCreateSecondAudit()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; DevClockController Postgres round-trip skipped.");

        await using var provider = CreateAtomicProvider();
        var access = await SeedAdministratorAccessAsync(provider);
        var clockState = new ClockStateProvider(provider.GetRequiredService<IServiceScopeFactory>());
        var timeProvider = new SimulationTimeProvider(clockState);
        await clockState.RefreshAsync();

        await using var requestScope = provider.CreateAsyncScope();
        var controller = new DevClockController(
            timeProvider,
            clockState,
            new FixedTimeZoneProvider("America/New_York"),
            requestScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            requestScope.ServiceProvider.GetRequiredService<IWriteExecutor>());
        InstallAccessContext(controller, access);

        var instant = new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc);
        var first = Body(await controller.Set(
            new SetClockRequest(instant, null, "America/New_York", "frozen"),
            "clock-replay-proof",
            default));
        var replay = Body(await controller.Set(
            new SetClockRequest(instant, null, "America/New_York", "frozen"),
            "clock-replay-proof",
            default));

        replay.Should().BeEquivalentTo(first);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var commandKey = $"{access.PortfolioId}:{access.UserId}:clock-replay-proof";
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "simulation.clock.set"
            && receipt.IdempotencyKey == commandKey)).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(log =>
            log.CommandType == "simulation.clock.set"
            && log.CommandIdempotencyKey == commandKey
            && log.EntityType == nameof(SimulationClock)
            && log.EntityId == 1)).Should().BeGreaterThanOrEqualTo(1);
    }

    private static ClockStateResponse Body(ActionResult<ClockStateResponse> result)
    {
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the action should return 200 OK");
        return (ClockStateResponse)ok!.Value!;
    }

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    private ServiceProvider CreateAtomicProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_conn)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task<ActiveAccessContext> SeedAdministratorAccessAsync(ServiceProvider provider)
    {
        var now = DateTime.UtcNow;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var portfolio = new Portfolio
        {
            Name = $"Dev clock {Guid.NewGuid():N}",
            ManagementCompanyName = "Dev Clock Co",
            TimeZone = "America/New_York",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            UserName = $"dev-clock-{Guid.NewGuid():N}@example.test",
            Email = $"dev-clock-{Guid.NewGuid():N}@example.test",
            DisplayName = "Dev Clock Admin",
            CreatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(user, accessContext, membership, assignment, session);
        await db.SaveChangesAsync();

        return new ActiveAccessContext(
            session.Id,
            user.Id,
            accessContext.Id,
            portfolio.Id,
            accessContext.AccessRevision,
            accessContext.LastAuthorizedExperience,
            membership.Id,
            membership.DefaultExperience);
    }

    private static void InstallAccessContext(ControllerBase controller, ActiveAccessContext access)
    {
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = access;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private sealed class FixedTimeZoneProvider(string id) : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
