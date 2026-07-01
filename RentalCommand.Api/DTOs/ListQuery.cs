using Microsoft.AspNetCore.Mvc;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// Common list query parameters bound from the query string for paginated, searchable, sortable list
/// endpoints: <c>?skip=0&amp;take=50&amp;search=foo&amp;sort=-createdAt</c>. Take is clamped to
/// <see cref="MaxTake"/> so a client can never request an unbounded page.
/// </summary>
public class ListQuery
{
    public const int MaxTake = 200;
    public const int DefaultTake = 50;

    [FromQuery(Name = "skip")]
    public int Skip { get; set; }

    [FromQuery(Name = "take")]
    public int Take { get; set; } = DefaultTake;

    /// <summary>Free-text search applied to entity-specific text fields by each service.</summary>
    [FromQuery(Name = "search")]
    public string? Search { get; set; }

    /// <summary>Sort field; prefix with '-' for descending (e.g. <c>-createdAt</c>).</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    /// <summary>
    /// Inclusive start of the grid date-range filter (the RangeDatePicker's "from"). Each list service
    /// applies it to that entity's designated date column, half-open <c>[from, to)</c>, DB-side. Null = unbounded start.
    /// </summary>
    [FromQuery(Name = "from")]
    public DateTime? From { get; set; }

    /// <summary>
    /// Inclusive end DAY of the grid date-range filter (the RangeDatePicker's "to"). Applied as the
    /// exclusive upper bound <c>&lt; to + 1 day</c> so the whole "to" day is included. Null = unbounded end.
    /// </summary>
    [FromQuery(Name = "to")]
    public DateTime? To { get; set; }

    /// <summary>Skip clamped to be non-negative.</summary>
    public int NormalizedSkip => Skip < 0 ? 0 : Skip;

    /// <summary>Take clamped to (0, <see cref="MaxTake"/>], defaulting to <see cref="DefaultTake"/>.</summary>
    public int NormalizedTake => Take <= 0 ? DefaultTake : Math.Min(Take, MaxTake);

    /// <summary>True when the sort spec requests descending order (leading '-').</summary>
    public bool SortDescending => !string.IsNullOrEmpty(Sort) && Sort.StartsWith('-');

    /// <summary>Sort field name with any leading '-' stripped, lower-cased; null when unset.</summary>
    public string? SortField =>
        string.IsNullOrWhiteSpace(Sort) ? null : Sort.TrimStart('-').Trim().ToLowerInvariant();
}
