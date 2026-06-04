using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class NotificationSettingsService : INotificationSettingsService
{
    // Every NotificationType the landlord controls, in display order. Drives the full matrix
    // returned to the client and the defaults applied when no row is stored.
    private static readonly NotificationType[] AllTypes = Enum.GetValues<NotificationType>();

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;

    public NotificationSettingsService(RentalCommandDbContext db, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.NotificationSettings.v1");
    }

    public async Task<NotificationSettingsResponse> GetAdminAsync(int portfolioId, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(portfolioId, ct);
        var prefs = await GetPreferenceMapAsync(portfolioId, ct);
        return ToAdminResponse(row, prefs);
    }

    public async Task<NotificationSettingsResponse> UpdateAsync(int portfolioId, UpdateNotificationSettingsRequest request, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(portfolioId, ct);
        var now = DateTime.UtcNow;

        row.EnableRentCharges = request.EnableRentCharges;
        row.EnableLateFees = request.EnableLateFees;
        row.EnableLeaseExpiryReminders = request.EnableLeaseExpiryReminders;
        row.NotifyTenants = request.NotifyTenants;
        row.RentChargeLeadDays = Math.Clamp(request.RentChargeLeadDays, 0, 31);
        row.LateFeeGraceDays = Math.Clamp(request.LateFeeGraceDays, 0, 60);
        row.LeaseExpiryReminderDays = Math.Clamp(request.LeaseExpiryReminderDays, 1, 365);
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

        await ApplyChannelPreferencesAsync(portfolioId, request.ChannelPreferences, now, ct);

        await _db.SaveChangesAsync(ct);

        var prefs = await GetPreferenceMapAsync(portfolioId, ct);
        return ToAdminResponse(row, prefs);
    }

    public async Task<NotificationsConfig> GetRuntimeAsync(int portfolioId, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(portfolioId, ct);
        var prefs = await GetPreferenceMapAsync(portfolioId, ct);

        return new NotificationsConfig
        {
            EnableRentCharges = row.EnableRentCharges,
            EnableLateFees = row.EnableLateFees,
            EnableLeaseExpiryReminders = row.EnableLeaseExpiryReminders,
            NotifyTenants = row.NotifyTenants,
            RentChargeLeadDays = row.RentChargeLeadDays,
            LateFeeGraceDays = row.LateFeeGraceDays,
            LeaseExpiryReminderDays = row.LeaseExpiryReminderDays,
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
            ChannelPreferences = AllTypes.ToDictionary(
                t => t,
                t => ToChannelPreference(t, prefs.GetValueOrDefault(t))),
        };
    }

    // -------------------------------------------------------------------------------------------
    // Per-portfolio settings row
    // -------------------------------------------------------------------------------------------

    private async Task<NotificationSettings> GetOrCreateAsync(int portfolioId, CancellationToken ct)
    {
        var row = await _db.NotificationSettings.SingleOrDefaultAsync(s => s.PortfolioId == portfolioId, ct);
        if (row is not null)
            return row;

        row = new NotificationSettings
        {
            PortfolioId = portfolioId,
            EnableLeaseExpiryReminders = true,
            RentChargeLeadDays = 5,
            LateFeeGraceDays = 5,
            LeaseExpiryReminderDays = 60,
            DailyBriefingSendHourLocal = 8,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.NotificationSettings.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    // -------------------------------------------------------------------------------------------
    // Channel preferences
    // -------------------------------------------------------------------------------------------

    private async Task<Dictionary<NotificationType, NotificationPreference>> GetPreferenceMapAsync(int portfolioId, CancellationToken ct)
    {
        var rows = await _db.NotificationPreferences
            .Where(p => p.PortfolioId == portfolioId)
            .ToListAsync(ct);

        // A duplicate (PortfolioId, NotificationType) is impossible (unique index), but guard with
        // a last-wins reduce so a malformed legacy state never throws on the dashboard.
        var map = new Dictionary<NotificationType, NotificationPreference>();
        foreach (var r in rows)
            map[r.NotificationType] = r;
        return map;
    }

    private async Task ApplyChannelPreferencesAsync(
        int portfolioId,
        IEnumerable<NotificationChannelPreferenceDto>? requested,
        DateTime now,
        CancellationToken ct)
    {
        if (requested is null)
            return;

        var existing = await _db.NotificationPreferences
            .Where(p => p.PortfolioId == portfolioId)
            .ToListAsync(ct);

        // De-dupe the incoming list (last write wins) and ignore types outside the enum.
        var byType = new Dictionary<NotificationType, NotificationChannelPreferenceDto>();
        foreach (var dto in requested)
        {
            if (Enum.IsDefined(dto.NotificationType))
                byType[dto.NotificationType] = dto;
        }

        foreach (var (type, dto) in byType)
        {
            var row = existing.FirstOrDefault(p => p.NotificationType == type);
            if (row is null)
            {
                row = new NotificationPreference
                {
                    PortfolioId = portfolioId,
                    NotificationType = type,
                    CreatedAt = now,
                };
                _db.NotificationPreferences.Add(row);
            }

            row.EnableInApp = dto.EnableInApp;
            row.EnableEmail = dto.EnableEmail;
            row.EnableSms = dto.EnableSms;
            row.UpdatedAt = now;
        }
    }

    private static NotificationChannelPreference ToChannelPreference(NotificationType type, NotificationPreference? row)
    {
        if (row is null)
            return NotificationChannelPreference.Default(type);

        return new NotificationChannelPreference
        {
            EnableInApp = row.EnableInApp,
            EnableEmail = row.EnableEmail,
            EnableSms = row.EnableSms,
        };
    }

    // -------------------------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------------------------

    private NotificationSettingsResponse ToAdminResponse(
        NotificationSettings row,
        Dictionary<NotificationType, NotificationPreference> prefs) => new()
    {
        EnableRentCharges = row.EnableRentCharges,
        EnableLateFees = row.EnableLateFees,
        EnableLeaseExpiryReminders = row.EnableLeaseExpiryReminders,
        NotifyTenants = row.NotifyTenants,
        RentChargeLeadDays = row.RentChargeLeadDays,
        LateFeeGraceDays = row.LateFeeGraceDays,
        LeaseExpiryReminderDays = row.LeaseExpiryReminderDays,
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
        // Always emit every known type so the client renders the full matrix; fill gaps with defaults.
        ChannelPreferences = AllTypes.Select(type =>
        {
            var pref = ToChannelPreference(type, prefs.GetValueOrDefault(type));
            return new NotificationChannelPreferenceDto
            {
                NotificationType = type,
                EnableInApp = pref.EnableInApp,
                EnableEmail = pref.EnableEmail,
                EnableSms = pref.EnableSms,
            };
        }).ToList(),
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
