namespace RentalCommand.Core.Configuration;

/// <summary>
/// Absolute base URLs for the user-facing web app, used when a server-issued redirect must land on
/// the SvelteKit front-end rather than the API. In production the web app and API share an origin, so
/// <see cref="WebBaseUrl"/> is left empty and redirects stay relative (the browser resolves them
/// against the current host). In local dev the API (5666) and web (5667) are different origins, so a
/// relative <c>/settings/...</c> redirect would wrongly land on the API — set <see cref="WebBaseUrl"/>
/// to the web dev origin to prefix those redirects.
/// </summary>
public class AppUrls
{
    public const string SectionName = "AppUrls";

    /// <summary>
    /// Absolute base URL of the web front-end (e.g. <c>https://localhost:5667</c>), with no trailing
    /// slash. Empty (the default, and the production value) keeps server redirects relative.
    /// </summary>
    public string WebBaseUrl { get; set; } = string.Empty;
}
