using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Extensions;

/// <summary>
/// Registers every <see cref="IAccountingProvider"/> implementation + binds each provider's
/// <c>*Options</c> POCO. Shared by the API (import-on-connect + manual import endpoints) and the
/// Engine (the scheduled <c>AccountingPullWorker</c>) so both resolve the SAME provider set.
///
/// <para>
/// Provider-agnostic by construction: adding a 2nd accounting provider is ONE
/// <c>AddHttpClient&lt;IAccountingProvider, TProvider&gt;</c> line here + its options binding +
/// an <see cref="Core.Enums.AccountingProvider"/> value — nothing else in the backbone changes
/// (AC-1). The resolver collects all registered providers via <c>IEnumerable&lt;IAccountingProvider&gt;</c>.
/// </para>
/// </summary>
public static class AccountingProviderExtensions
{
    public static IServiceCollection AddAccountingProviders(this IServiceCollection services)
    {
        // Bind QuickBooks creds/environment from config (idempotent — the API also Configure()s this
        // in Program.cs; binding here too means the Engine, which does not call that block, still gets it).
        services.AddOptions<QuickBooksOptions>()
            .BindConfiguration(QuickBooksOptions.SectionName);

        // QuickBooks (provider #1). Every accounting HttpClient carries a Timeout (resilience rule).
        services.AddHttpClient<IAccountingProvider, QuickBooksAccountingProvider>(c =>
            c.Timeout = TimeSpan.FromSeconds(60));

        // Future providers (Xero, FreshBooks, …) add one AddHttpClient line each — and nothing else.

        return services;
    }
}
