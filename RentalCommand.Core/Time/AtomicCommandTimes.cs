namespace RentalCommand.Core.Time;

public sealed record AtomicCommandTimes(DateTime WallClockUtc, DateOnly BusinessDate)
{
    public DateTime EffectiveNowUtc { get; init; } = WallClockUtc;
}
