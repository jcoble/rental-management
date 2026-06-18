using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Maps the <see cref="AccountingProvider"/> enum to the registered
/// <see cref="IAccountingProvider"/> implementation. This is the entire
/// multi-provider seam — a port of EdiPlatform's <c>ErpProviderResolver</c>.
///
/// <para>
/// Registered as a singleton over the DI-collected
/// <c>IEnumerable&lt;IAccountingProvider&gt;</c>. Adding a provider is a new
/// implementation + an enum value + one registration line; this resolver, the
/// connection service, the entities, and the controller never change (AC-1).
/// </para>
/// </summary>
public class AccountingProviderResolver
{
    private readonly Dictionary<AccountingProvider, IAccountingProvider> _byProvider;

    public AccountingProviderResolver(IEnumerable<IAccountingProvider> providers)
    {
        // ToDictionary throws on a duplicate key, which is the right failure mode if two
        // implementations ever claim the same provider.
        _byProvider = providers.ToDictionary(p => p.Provider);
    }

    /// <summary>True when an implementation is registered for <paramref name="provider"/>.</summary>
    public bool IsRegistered(AccountingProvider provider) => _byProvider.ContainsKey(provider);

    /// <summary>
    /// Resolve the implementation for <paramref name="provider"/>. Throws a clean,
    /// explicit error when none is registered (expected in Phase 1, where no provider
    /// implementation exists yet) so the failure is never a silent fall-through.
    /// </summary>
    public IAccountingProvider Resolve(AccountingProvider provider)
    {
        if (!_byProvider.TryGetValue(provider, out var impl))
        {
            throw new InvalidOperationException(
                $"No accounting provider registered for {provider}");
        }

        return impl;
    }
}
