namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Helpers for coercing client-supplied <see cref="DateTime"/> values to UTC before they reach Npgsql.
/// The Postgres columns are <c>timestamp with time zone</c>, which only accepts <see cref="DateTimeKind.Utc"/>;
/// JSON-bound dates arrive as <see cref="DateTimeKind.Unspecified"/> (treated as UTC) or
/// <see cref="DateTimeKind.Local"/> (converted to UTC).
/// </summary>
internal static class DateTimeExtensions
{
    public static DateTime ToUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? ToUtc(this DateTime? value) => value.HasValue ? value.Value.ToUtc() : null;

    /// <summary>
    /// Converts an offset-bearing instant to the equivalent UTC <see cref="DateTime"/> (Kind=Utc) for
    /// storage. Clients send schedule times as ISO-8601 with the landlord's local UTC offset; this turns
    /// that into the true UTC instant the <c>timestamp with time zone</c> columns require, while the
    /// caller keeps the original <see cref="DateTimeOffset"/> when it needs to render local wall-clock.
    /// </summary>
    public static DateTime ToUtcDateTime(this DateTimeOffset value) => value.UtcDateTime;

    public static DateTime? ToUtcDateTime(this DateTimeOffset? value) => value.HasValue ? value.Value.UtcDateTime : null;
}
