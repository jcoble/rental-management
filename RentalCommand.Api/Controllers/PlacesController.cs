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
        if (!_places.Enabled)
            return NotFound(new { error = "Address lookup is not enabled" });
        if (string.IsNullOrWhiteSpace(placeId))
            return BadRequest(new { error = "Missing placeId" });

        var resolved = await _places.DetailsAsync(placeId, session, ct);
        if (resolved is null)
            return NotFound(new { error = "Could not resolve address" });

        return Ok(resolved);
    }
}
