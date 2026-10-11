using Microsoft.Extensions.Configuration;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Tests.Simulation;

public class AppTimeZoneProviderTests
{
    [Fact]
    public void SimOverride_WinsOverConfig()
    {
        var sut = new AppTimeZoneProvider(Config("America/New_York"), new StubClockState("America/Chicago"));

        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"), sut.BusinessTimeZone);
    }

    [Fact]
    public void FallsBackToConfig_WhenNoSimOverride()
    {
        // Sim override empty → use App:TimeZone.
        var sut = new AppTimeZoneProvider(Config("America/Los_Angeles"), new StubClockState(null));

        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"), sut.BusinessTimeZone);
    }

    [Fact]
    public void FallsBackToEasternDefault_WhenNoOverrideOrConfig()
    {
        // Null provider (simulation disabled) + no config → America/New_York.
        var sut = new AppTimeZoneProvider(Config(null), clockState: null);

        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), sut.BusinessTimeZone);
    }

    private static IConfiguration Config(string? appTimeZone) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:TimeZone"] = appTimeZone })
            .Build();

    private sealed class StubClockState : IClockStateProvider
    {
        public StubClockState(string? timeZoneId) =>
            Current = new ClockState(ClockMode.Real, default, default, timeZoneId);

        public ClockState Current { get; }

        public bool HasLoadedPersistedState => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureInitializedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
