using RentalCommand.Api.Simulation;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Tests.Simulation;

public class SimulationTimeProviderTests
{
    [Fact]
    public void Real_ReturnsSystemTime()
    {
        var sut = new SimulationTimeProvider(new StubClockState(ClockState.RealTime));

        var before = DateTimeOffset.UtcNow;
        var now = sut.GetUtcNow();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(now, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void Frozen_ReturnsAnchorExactly()
    {
        var anchor = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var sut = new SimulationTimeProvider(new StubClockState(new ClockState(ClockMode.Frozen, anchor, default, null)));

        Assert.Equal(anchor, sut.GetUtcNow().UtcDateTime);
        Assert.Equal(TimeSpan.Zero, sut.GetUtcNow().Offset);
    }

    [Fact]
    public void Offset_ShiftsRealTimeByAnchorDelta()
    {
        // Sim anchor is 100 days ahead of the real anchor → sim now ≈ real now + 100 days.
        var realAnchor = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var simAnchor = realAnchor.AddDays(100);
        var sut = new SimulationTimeProvider(new StubClockState(new ClockState(ClockMode.Offset, simAnchor, realAnchor, null)));

        var expected = DateTimeOffset.UtcNow.AddDays(100);
        var actual = sut.GetUtcNow();

        Assert.True(
            (actual - expected).Duration() < TimeSpan.FromSeconds(5),
            $"Offset now {actual:o} should be ~100 days ahead of real now (expected ~{expected:o}).");
    }

    [Fact]
    public void LocalTimeZone_UsesSimZoneWhenSet()
    {
        var sut = new SimulationTimeProvider(new StubClockState(new ClockState(ClockMode.Frozen, default, default, "America/Chicago")));

        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"), sut.LocalTimeZone);
    }

    [Fact]
    public void LocalTimeZone_FallsBackToBaseWhenUnset()
    {
        var sut = new SimulationTimeProvider(new StubClockState(ClockState.RealTime));

        Assert.Equal(TimeProvider.System.LocalTimeZone, sut.LocalTimeZone);
    }

    private sealed class StubClockState : IClockStateProvider
    {
        public StubClockState(ClockState state) => Current = state;

        public ClockState Current { get; }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
