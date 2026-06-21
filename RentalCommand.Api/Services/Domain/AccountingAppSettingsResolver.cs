using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Maps an <see cref="AccountingProvider"/> to the provider-neutral
/// <see cref="AccountingAppSettings"/>, reading the right per-provider
/// <c>*Options</c> POCO. This is the single place the backbone learns a
/// provider's OAuth app creds + environment, so the connection service / workers
/// never reference <see cref="QuickBooksOptions"/> (or any future provider's
/// options) directly — they stay provider-agnostic (AC-1).
///
/// <para>
/// Adding a provider adds one switch arm here (and injects that provider's options
/// POCO); everything downstream consumes the neutral struct unchanged.
/// </para>
/// </summary>
public class AccountingAppSettingsResolver
{
    private readonly IOptionsMonitor<QuickBooksOptions> _quickBooks;

    public AccountingAppSettingsResolver(IOptionsMonitor<QuickBooksOptions> quickBooks)
    {
        _quickBooks = quickBooks;
    }

    /// <summary>
    /// Resolve the neutral app settings for <paramref name="provider"/>.
    /// <paramref name="requestRedirectUri"/> (when supplied) overrides the configured
    /// redirect URI for this request — the value must still byte-match a URI registered
    /// on the provider's app.
    /// </summary>
    public AccountingAppSettings Resolve(AccountingProvider provider, string? requestRedirectUri = null)
    {
        switch (provider)
        {
            case AccountingProvider.QuickBooks:
            {
                var o = _quickBooks.CurrentValue;
                return new AccountingAppSettings(
                    Provider: AccountingProvider.QuickBooks,
                    ClientId: o.ClientId,
                    ClientSecret: o.ClientSecret,
                    UseSandbox: o.UseSandbox,
                    RedirectUri: requestRedirectUri ?? o.RedirectUri);
            }
            default:
                // Unknown provider with no options binding — fail closed rather than
                // silently returning an unconfigured struct under the wrong identity.
                throw new InvalidOperationException(
                    $"No accounting app settings configured for {provider}");
        }
    }
}
