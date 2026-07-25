using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Sms;

public sealed class MessagingProviderSettingsResolver : IMessagingProviderSettingsResolver
{
    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;

    public MessagingProviderSettingsResolver(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.MessagingProviderSettings.v1");
    }

    public async Task<SmsCredentials?> ResolvePortfolioSmsAsync(
        int portfolioId,
        CancellationToken ct = default)
    {
        var row = await _db.MessagingProviderSettings
            .AsNoTracking()
            .Where(settings => settings.PortfolioId == portfolioId)
            .Select(settings => new
            {
                settings.SmsProvider,
                settings.SmsCredentialACipherText,
                settings.SmsCredentialBCipherText,
                settings.SmsCredentialCCipherText,
                settings.SmsFromNumberCipherText,
            })
            .SingleOrDefaultAsync(ct);

        if (row is null ||
            !Enum.TryParse<SmsProviderKey>(row.SmsProvider, true, out var provider) ||
            provider == SmsProviderKey.None)
        {
            return null;
        }

        var credentials = new SmsCredentials(
            provider,
            Unprotect(row.SmsCredentialACipherText),
            Unprotect(row.SmsCredentialBCipherText),
            Unprotect(row.SmsCredentialCCipherText),
            Unprotect(row.SmsFromNumberCipherText));

        return credentials.IsComplete ? credentials : null;
    }

    private string? Unprotect(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : _protector.Unprotect(value);
}
