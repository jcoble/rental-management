using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Simulation;

public class ClockStateProviderTests
{
    [Fact]
    public void Current_BeforeRefresh_DefaultsToRealTime()
    {
        using var ctx = new SqliteTestContext();
        var sut = new ClockStateProvider(new SingleDbScopeFactory(ctx.Db));

        Assert.Same(ClockState.RealTime, sut.Current);
        Assert.Equal(ClockMode.Real, sut.Current.Mode);
    }

    [Fact]
    public async Task RefreshAsync_LoadsRowOne_IntoCurrent()
    {
        using var ctx = new SqliteTestContext();
        var anchor = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.Db.SimulationClocks.Add(new SimulationClock
        {
            Id               = 1,
            Mode             = ClockMode.Frozen,
            SimAnchorUtc     = anchor,
            RealAnchorUtc    = anchor,
            TimeZoneId       = "America/Chicago",
            UpdatedAtRealUtc = anchor,
        });
        ctx.Db.SaveChanges();

        var sut = new ClockStateProvider(new SingleDbScopeFactory(ctx.Db));
        await sut.RefreshAsync();

        Assert.Equal(ClockMode.Frozen, sut.Current.Mode);
        Assert.Equal(anchor, sut.Current.SimAnchorUtc);
        Assert.Equal("America/Chicago", sut.Current.TimeZoneId);
    }

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> that hands <see cref="ClockStateProvider"/> the test's
    /// own <see cref="RentalCommandDbContext"/>. Dispose is a no-op so the shared <see cref="SqliteTestContext"/>
    /// outlives each (using) scope the provider opens.
    /// </summary>
    private sealed class SingleDbScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly RentalCommandDbContext _db;

        public SingleDbScopeFactory(RentalCommandDbContext db) => _db = db;

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(RentalCommandDbContext) ? _db : null;

        public void Dispose() { }
    }
}
