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
    private const int MaxSessionTokenLength = 128;
    private readonly GooglePlacesService _places;

    public PlacesController(GooglePlacesService places) => _places = places;

    /// <summary>GET /api/v1/places/autocomplete?q=&amp;session= → { enabled, suggestions[] }.</summary>
    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string? q, [FromQuery] string? session, CancellationToken ct)
    {
        if (ValidateSessionToken(session) is { } invalid)
            return invalid;
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
        if (ValidateSessionToken(session) is { } invalid)
            return invalid;
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
    /// Returns a 429 result if the caller is over either the IP-wide cost ceiling or the narrower
    /// autocomplete-session budget. The IP bucket is mandatory: a client-controlled session token must
    /// never be able to mint a fresh billed-request budget.
    /// </summary>
    private IActionResult? TooManyRequests(string? session)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!PlacesRateLimiter.Allow($"ip:{ip}", PlacesRateLimiter.IpLimit, out var retryAfter))
            return RateLimited(retryAfter);

        if (!string.IsNullOrWhiteSpace(session) &&
            !PlacesRateLimiter.Allow(
                $"session:{ip}:{session}",
                PlacesRateLimiter.SessionLimit,
                out retryAfter))
            return RateLimited(retryAfter);

        return null;
    }

    private IActionResult? ValidateSessionToken(string? session)
    {
        if (session is null || session.Length <= MaxSessionTokenLength)
            return null;

        return BadRequest(new { error = $"session cannot exceed {MaxSessionTokenLength} characters." });
    }

    private IActionResult RateLimited(TimeSpan retryAfter)
    {
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
    // A page session gets the normal interactive budget. The mandatory IP ceiling is deliberately
    // higher so normal parallel forms are unaffected, while token rotation still cannot make the
    // upstream billing exposure unbounded.
    internal const int SessionLimit = 30;
    internal const int IpLimit = 60;
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
    public static bool Allow(string key, int limit, out TimeSpan retryAfter)
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
            if (counter.Count <= limit)
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
    internal static void Reset()
    {
        Counters.Clear();
        _lastSweep = DateTime.UtcNow;
    }

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
