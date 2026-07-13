using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Sms;

public interface IMessagingProviderSettingsResolver
{
    Task<SmsCredentials?> ResolvePortfolioSmsAsync(int portfolioId, CancellationToken ct = default);
}
