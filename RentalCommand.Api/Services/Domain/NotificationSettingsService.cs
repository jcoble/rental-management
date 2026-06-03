using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class NotificationSettingsService : INotificationSettingsService
{
    private const int SingletonId = 1;
    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;

    public NotificationSettingsService(RentalCommandDbContext db, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.NotificationSettings.v1");
    }

    public async Task<NotificationSettingsResponse> GetAdminAsync(CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(ct);
        return ToAdminResponse(row);
    }

    public async Task<NotificationSettingsResponse> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(ct);
        var now = DateTime.UtcNow;

        row.EnableDailyBriefingMessages = request.EnableDailyBriefingMessages;
        row.DailyBriefingSendHourLocal = Math.Clamp(request.DailyBriefingSendHourLocal, 0, 23);
        row.DailyBriefingIncludeEmpty = request.DailyBriefingIncludeEmpty;
        row.SignalWireProjectIdCipherText = ProtectNullable(Normalize(request.SignalWireProjectId));
        if (request.SignalWireToken is not null)
        {
            row.SignalWireTokenCipherText = ProtectNullable(Normalize(request.SignalWireToken));
        }
        row.SignalWireSpaceUrlCipherText = ProtectNullable(Normalize(request.SignalWireSpaceUrl));
        row.SignalWireFromNumberCipherText = ProtectNullable(Normalize(request.SignalWireFromNumber));
        row.DailyBriefingSmsRecipientsCipherText = ProtectArray(request.DailyBriefingSmsRecipients);
        row.DailyBriefingEmailRecipientsCipherText = ProtectArray(request.DailyBriefingEmailRecipients);
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ToAdminResponse(row);
    }

    public async Task<NotificationsConfig> GetRuntimeAsync(CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(ct);
        return new NotificationsConfig
        {
            EnableDailyBriefingMessages = row.EnableDailyBriefingMessages,
            DailyBriefing = new DailyBriefingOptions
            {
                SendHourLocal = row.DailyBriefingSendHourLocal,
                IncludeEmptyBriefing = row.DailyBriefingIncludeEmpty,
                SmsRecipients = UnprotectArray(row.DailyBriefingSmsRecipientsCipherText),
                EmailRecipients = UnprotectArray(row.DailyBriefingEmailRecipientsCipherText),
            },
            SignalWire = new SignalWireOptions
            {
                ProjectId = UnprotectNullable(row.SignalWireProjectIdCipherText),
                Token = UnprotectNullable(row.SignalWireTokenCipherText),
                SpaceUrl = UnprotectNullable(row.SignalWireSpaceUrlCipherText),
                FromNumber = UnprotectNullable(row.SignalWireFromNumberCipherText),
            },
        };
    }

    private async Task<NotificationSettings> GetOrCreateAsync(CancellationToken ct)
    {
        var row = await _db.NotificationSettings.SingleOrDefaultAsync(s => s.Id == SingletonId, ct);
        if (row is not null)
            return row;

        row = new NotificationSettings
        {
            Id = SingletonId,
            DailyBriefingSendHourLocal = 8,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.NotificationSettings.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    private NotificationSettingsResponse ToAdminResponse(NotificationSettings row) => new()
    {
        EnableDailyBriefingMessages = row.EnableDailyBriefingMessages,
        DailyBriefingSendHourLocal = row.DailyBriefingSendHourLocal,
        DailyBriefingIncludeEmpty = row.DailyBriefingIncludeEmpty,
        DailyBriefingSmsRecipients = UnprotectArray(row.DailyBriefingSmsRecipientsCipherText),
        DailyBriefingEmailRecipients = UnprotectArray(row.DailyBriefingEmailRecipientsCipherText),
        SignalWireProjectId = UnprotectNullable(row.SignalWireProjectIdCipherText),
        SignalWireTokenSet = !string.IsNullOrWhiteSpace(row.SignalWireTokenCipherText),
        SignalWireToken = null,
        SignalWireSpaceUrl = UnprotectNullable(row.SignalWireSpaceUrlCipherText),
        SignalWireFromNumber = UnprotectNullable(row.SignalWireFromNumberCipherText),
    };

    private string? ProtectNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : _protector.Protect(value);

    private string? UnprotectNullable(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
            return null;

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private string? ProtectArray(IEnumerable<string>? values)
    {
        var clean = Clean(values).ToArray();
        return clean.Length == 0 ? null : _protector.Protect(JsonSerializer.Serialize(clean));
    }

    private string[] UnprotectArray(string? cipherText)
    {
        var json = UnprotectNullable(cipherText);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
        .Select(Normalize)
        .OfType<string>()
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
