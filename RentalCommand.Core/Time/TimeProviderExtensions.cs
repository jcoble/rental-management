namespace RentalCommand.Core.Time;

/// <summary>
/// Terse helpers over <see cref="TimeProvider"/> so the wall-clock sweep is mechanical and call
/// sites stay readable: inject a <see cref="TimeProvider"/> and replace <c>DateTime.UtcNow</c> with
/// <c>_timeProvider.UtcNow()</c>, <c>DateTime.Today</c> with <c>_timeProvider.TodayUtc()</c>, and
/// <c>DateTimeOffset.UtcNow</c> with <c>_timeProvider.NowOffset()</c>.
///
/// <para>In production the injected provider is <see cref="TimeProvider.System"/> (identical behavior
/// to the old direct calls); in non-production it is the controllable simulation provider.</para>
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>Replacement for <c>DateTime.UtcNow</c>; always <see cref="DateTimeKind.Utc"/>.</summary>
    public static DateTime UtcNow(this TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Replacement for <c>DateTime.Today</c> where a UTC calendar day is intended.</summary>
    public static DateOnly TodayUtc(this TimeProvider timeProvider) =>
        DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>Replacement for <c>DateTimeOffset.UtcNow</c>.</summary>
    public static DateTimeOffset NowOffset(this TimeProvider timeProvider) => timeProvider.GetUtcNow();
}
