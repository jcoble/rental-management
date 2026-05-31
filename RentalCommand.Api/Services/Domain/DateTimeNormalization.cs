namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Helpers for coercing client-supplied <see cref="DateTime"/> values to UTC before they reach Npgsql.
/// The Postgres columns are <c>timestamp with time zone</c>, which only accepts <see cref="DateTimeKind.Utc"/>;
/// JSON-bound dates arrive as <see cref="DateTimeKind.Unspecified"/> (treated as UTC) or
/// <see cref="DateTimeKind.Local"/> (converted to UTC).
/// </summary>
internal static class DateTimeNormalization
{
    public static DateTime ToUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? ToUtc(this DateTime? value) => value.HasValue ? value.Value.ToUtc() : null;
}
