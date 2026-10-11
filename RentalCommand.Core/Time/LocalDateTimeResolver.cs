namespace RentalCommand.Core.Time;

/// <summary>
/// Converts a user-supplied wall-clock value in a named timezone to UTC without allowing a DST
/// transition to crash the caller. Spring-forward gap values move forward by the timezone's gap
/// offset. Fall-back values use the larger (DST, in the usual case) offset, which is the earlier
/// of the two possible UTC instants and is deterministic for repeat processing.
/// </summary>
public static class LocalDateTimeResolver
{
    private const int GapSearchMinutes = 48 * 60;

    public static DateTime ConvertToUtc(DateTime localTime, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
            unspecified = ShiftForwardOutOfGap(unspecified, timeZone);

        if (timeZone.IsAmbiguousTime(unspecified))
        {
            // The larger offset maps to the earlier UTC instant. For ordinary fall-back rules this
            // is the DST occurrence; selecting it avoids a different instant after a retry.
            var offset = timeZone.GetAmbiguousTimeOffsets(unspecified).Max();
            return new DateTimeOffset(unspecified, offset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    private static DateTime ShiftForwardOutOfGap(DateTime localTime, TimeZoneInfo timeZone)
    {
        var before = FindValidLocalTime(localTime, timeZone, -1);
        var after = FindValidLocalTime(localTime, timeZone, 1);
        var beforeOffset = OffsetAtValidLocalTime(before, timeZone);
        var afterOffset = OffsetAtValidLocalTime(after, timeZone);
        var gap = afterOffset - beforeOffset;
        if (gap <= TimeSpan.Zero)
        {
            throw new InvalidTimeZoneException(
                $"Timezone '{timeZone.Id}' has an invalid local time without a forward gap.");
        }

        var shifted = localTime.Add(gap);
        if (timeZone.IsInvalidTime(shifted))
        {
            throw new InvalidTimeZoneException(
                $"Timezone '{timeZone.Id}' did not expose a valid local time after its DST gap.");
        }

        return shifted;
    }

    private static DateTime FindValidLocalTime(
        DateTime localTime,
        TimeZoneInfo timeZone,
        int direction)
    {
        for (var minute = 1; minute <= GapSearchMinutes; minute++)
        {
            var candidate = localTime.AddMinutes(direction * minute);
            if (!timeZone.IsInvalidTime(candidate))
                return candidate;
        }

        throw new InvalidTimeZoneException(
            $"Timezone '{timeZone.Id}' has an invalid local time with no nearby valid boundary.");
    }

    private static TimeSpan OffsetAtValidLocalTime(DateTime localTime, TimeZoneInfo timeZone) =>
        localTime - TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone);
}
