namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for Google Places (New) address autocomplete. Bound from the "GooglePlaces"
/// section. The key is read from .NET configuration (user-secrets in Development, container env in
/// production) like every other third-party key — it is never exposed to the browser; the web app
/// calls the API's <c>/api/v1/places/*</c> endpoints, which hold the key server-side.
///
/// Set in dev:  <c>dotnet user-secrets set "GooglePlaces:ApiKey" "AIza…" --project RentalCommand.Api</c>
/// Leave unset to keep address fields as plain manual-entry inputs (the feature self-gates off).
/// </summary>
public class GooglePlacesConfig
{
    public const string SectionName = "GooglePlaces";

    /// <summary>Google Maps Platform API key with the "Places API (New)" enabled. Optional.</summary>
    public string? ApiKey { get; set; }
}
