using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Services.Places;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Address autocomplete proxy backed by Google Places (New). The API key stays server-side
/// (<see cref="GooglePlacesService"/> reads it from configuration); the browser only ever calls
/// these same-origin endpoints. Anonymous because address fields appear on public pages too
/// (e.g. the applicant intake form). Returns <c>enabled:false</c> when no key is configured so the
/// web client falls back to plain manual entry.
///
/// Because these endpoints are <c>[AllowAnonymous]</c> and proxy to a <b>billed</b> upstream, they are
/// protected by a lightweight per-IP fixed-window rate limiter (<see cref="PlacesRateLimiter"/>) so a
/// scripted client cannot exhaust the Google Places quota / run up cost (financial DoS). Over the limit
/// returns <c>429 Too Many Requests</c> with a <c>Retry-After</c> header.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/places")]
public sealed class PlacesController : ControllerBase
{
    private readonly GooglePlacesService _places;

    public PlacesController(GooglePlacesService places) => _places = places;

    /// <summary>GET /api/v1/places/autocomplete?q=&amp;session= → { enabled, suggestions[] }.</summary>
    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string? q, [FromQuery] string? session, CancellationToken ct)
    {
        if (TooManyRequests(session) is { } limited)
            return limited;

        if (!_places.Enabled)
            return Ok(new { enabled = false, suggestions = Array.Empty<PlaceSuggestion>() });

        var suggestions = await _places.AutocompleteAsync(q ?? string.Empty, session, ct);
        return Ok(new { enabled = true, suggestions });
    }

    /// <summary>GET /api/v1/places/details?placeId=&amp;session= → { line1, city, state, zip } | 404.</summary>
    [HttpGet("details")]
    public async Task<IActionResult> Details(
        [FromQuery] string? placeId, [FromQuery] string? session, CancellationToken ct)
    {
        if (TooManyRequests(session) is { } limited)
            return limited;

        if (!_places.Enabled)
            return NotFound(new { error = "Address lookup is not enabled" });
        if (string.IsNullOrWhiteSpace(placeId))
            return BadRequest(new { error = "Missing placeId" });

        var resolved = await _places.DetailsAsync(placeId, session, ct);
        if (resolved is null)
            return NotFound(new { error = "Could not resolve address" });

        return Ok(resolved);
    }

    /// <summary>
    /// Returns a 429 result if the caller is over the rate limit, otherwise <c>null</c>. Keyed by the
    /// caller's remote IP plus the autocomplete session token when present, so a single page's typing
    /// session (which fires many keystroke requests) and abusive clients are both bounded, while distinct
    /// legitimate users on different IPs are independent.
    /// </summary>
    private IActionResult? TooManyRequests(string? session)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = string.IsNullOrWhiteSpace(session) ? ip : $"{ip}|{session}";

        if (PlacesRateLimiter.Allow(key, out var retryAfter))
            return null;

        Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new { error = "Too many address lookup requests. Please slow down and try again shortly." });
    }
}

/// <summary>
/// A tiny, dependency-free per-key fixed-window rate limiter for the anonymous Places proxy. Process-local
/// (good enough as an abuse/cost backstop on a single instance; behind multiple replicas each instance
/// enforces its own window, which only makes the effective ceiling more generous, never less safe). Kept in
/// this file so the limiter lives with the only endpoint it guards.
/// </summary>
internal static class PlacesRateLimiter
{
    // Allow up to <c>Limit</c> requests per <c>Window</c> for a given key. Sized for interactive
    // address autocomplete (a fast typist fires a handful of keystroke requests per second, debounced
    // client-side) while cutting off scripted abuse of the billed upstream.
    private static readonly int Limit = 30;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static readonly ConcurrentDictionary<string, Counter> Counters = new();
    private static DateTime _lastSweep = DateTime.UtcNow;

    private sealed class Counter
    {
        public DateTime WindowStart;
        public int Count;
    }

    /// <summary>
    /// Records a hit for <paramref name="key"/> and returns true if it is within the allowed budget. When
    /// false, <paramref name="retryAfter"/> is the time until the current window resets.
    /// </summary>
    public static bool Allow(string key, out TimeSpan retryAfter)
    {
        var now = DateTime.UtcNow;
        MaybeSweep(now);

        var counter = Counters.GetOrAdd(key, _ => new Counter { WindowStart = now, Count = 0 });
        lock (counter)
        {
            if (now - counter.WindowStart >= Window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
            if (counter.Count <= Limit)
            {
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = counter.WindowStart + Window - now;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
            return false;
        }
    }

    // Test seam: reset all state so rate-limit tests don't bleed into one another.
    internal static void Reset() => Counters.Clear();

    // Opportunistically drop counters whose window has long elapsed so the map can't grow unbounded
    // from a flood of distinct IP/session keys. Cheap: at most once per window.
    private static void MaybeSweep(DateTime now)
    {
        if (now - _lastSweep < Window)
        {
            return;
        }
        _lastSweep = now;

        foreach (var entry in Counters)
        {
            if (now - entry.Value.WindowStart >= Window + Window)
            {
                Counters.TryRemove(entry.Key, out _);
            }
        }
    }
}
