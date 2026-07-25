using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Testcontainers.PostgreSql;
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
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _conn = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
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

        var services = new ServiceCollection();
        services.AddDbContext<RentalCommandDbContext>(o => o.UseNpgsql(_conn));
        await using var provider = services.BuildServiceProvider();

        var clockState = new ClockStateProvider(provider.GetRequiredService<IServiceScopeFactory>());
        var timeProvider = new SimulationTimeProvider(clockState);
        await clockState.RefreshAsync();
        clockState.Current.Mode.Should().Be(ClockMode.Real); // seeded state

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var controller = new DevClockController(
            db,
            timeProvider,
            clockState,
            new TestAtomicInfrastructureUnitOfWork(db));

        var jan1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // set (frozen) → the clock reports exactly that instant
        var set = Body(await controller.Set(new SetClockRequest(jan1, null, "America/New_York", "frozen"), default));
        set.Mode.Should().Be("Frozen");
        set.SimNowUtc.Should().Be(jan1);
        set.TimeZoneId.Should().Be("America/New_York");

        // GET reflects it (fresh in-memory read)
        var got = Body(controller.Get());
        got.SimNowUtc.Should().Be(jan1);
        got.Mode.Should().Be("Frozen");

        // advance 35 days → Jan 1 + 35d = Feb 5, still frozen
        var advanced = Body(await controller.Advance(new AdvanceClockRequest(35, 0, 0, 0), default));
        advanced.SimNowUtc.Should().Be(new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc));
        advanced.Mode.Should().Be("Frozen");

        // reset → real time again, timezone override cleared
        var reset = Body(await controller.Reset(default));
        reset.Mode.Should().Be("Real");
        reset.TimeZoneId.Should().BeNull();
        reset.SimNowUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }

    private static ClockStateResponse Body(ActionResult<ClockStateResponse> result)
    {
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the action should return 200 OK");
        return (ClockStateResponse)ok!.Value!;
    }

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);
}
