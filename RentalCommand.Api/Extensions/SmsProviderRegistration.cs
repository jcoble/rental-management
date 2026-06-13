using RentalCommand.Api.Services.Sms;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Extensions;

/// <summary>
/// Registers the pluggable SMS provider substrate (the four <see cref="ISmsProvider"/> implementations
/// + the <see cref="ISmsDispatcher"/> that resolves a portfolio's BYO provider with platform-env
/// fallback). Shared by the API (synchronous "send test SMS" endpoint) and the Engine (outbox
/// dispatch). Each provider gets its own typed <see cref="HttpClient"/>; the dispatcher injects them
/// all via <c>IEnumerable&lt;ISmsProvider&gt;</c> and keys on <see cref="ISmsProvider.Key"/>.
/// </summary>
public static class SmsProviderRegistration
{
    public static IServiceCollection AddSmsProviders(this IServiceCollection services)
    {
        services.AddHttpClient<ISmsProvider, SignalWireSmsProvider>();
        services.AddHttpClient<ISmsProvider, TwilioSmsProvider>();
        services.AddHttpClient<ISmsProvider, TelnyxSmsProvider>();
        services.AddHttpClient<ISmsProvider, VonageSmsProvider>();
        services.AddScoped<ISmsDispatcher, SmsDispatcher>();
        return services;
    }
}
