using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class NotificationSettingsService : INotificationSettingsService
{
    // Every NotificationType the landlord controls, in display order. Drives the full matrix
    // returned to the client and the defaults applied when no row is stored.
    private static readonly NotificationType[] AllTypes = Enum.GetValues<NotificationType>();

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IReadOnlyDictionary<SmsProviderKey, ISmsProvider> _smsProviders;
    private readonly TimeProvider _timeProvider;

    public NotificationSettingsService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        IEnumerable<ISmsProvider> smsProviders,
        TimeProvider timeProvider)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.NotificationSettings.v1");
        _smsProviders = smsProviders.ToDictionary(p => p.Key, p => p);
        _timeProvider = timeProvider;
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
        var now = _timeProvider.UtcNow();

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
        row.AutoSendRentReminder = request.AutoSendRentReminder;
        row.AutoSendLateRent = request.AutoSendLateRent;
        row.LeaseEndAutoAction = request.LeaseEndAutoAction;

        // SMS provider (pluggable, BYO creds). Provider is a plain string-enum name; credential slots
        // are write-only secrets — null/omitted keeps the saved value, empty string clears it. The
        // from-number is not a secret. Provider-agnostic: storage never special-cases a vendor.
        row.SmsProvider = ParseProvider(request.SmsProvider).ToString();
        row.SmsFromNumberCipherText = ProtectNullable(Normalize(request.SmsFromNumber));
        row.SmsCredentialACipherText = ApplySecret(row.SmsCredentialACipherText, request.SmsCredentialA);
        row.SmsCredentialBCipherText = ApplySecret(row.SmsCredentialBCipherText, request.SmsCredentialB);
        row.SmsCredentialCCipherText = ApplySecret(row.SmsCredentialCCipherText, request.SmsCredentialC);
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
            ChannelPreferences = AllTypes.ToDictionary(
                t => t,
                t => ToChannelPreference(t, prefs.GetValueOrDefault(t))),
        };
    }

    public async Task<SmsCredentials?> GetSmsCredentialsAsync(int portfolioId, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(portfolioId, ct);
        var provider = ParseProvider(row.SmsProvider);

        // Backward compat: rows saved before this migration may carry only the legacy SignalWire
        // columns (no SmsProvider set). Treat those as a configured SignalWire provider so existing
        // installs keep sending without a re-save.
        if (provider == SmsProviderKey.None &&
            !string.IsNullOrWhiteSpace(row.SignalWireProjectIdCipherText))
        {
            provider = SmsProviderKey.SignalWire;
            var legacy = new SmsCredentials(
                provider,
                UnprotectNullable(row.SignalWireProjectIdCipherText),
                UnprotectNullable(row.SignalWireTokenCipherText),
                UnprotectNullable(row.SignalWireSpaceUrlCipherText),
                UnprotectNullable(row.SignalWireFromNumberCipherText));
            return legacy.IsComplete ? legacy : null;
        }

        if (provider == SmsProviderKey.None)
            return null;

        var creds = new SmsCredentials(
            provider,
            UnprotectNullable(row.SmsCredentialACipherText),
            UnprotectNullable(row.SmsCredentialBCipherText),
            UnprotectNullable(row.SmsCredentialCCipherText),
            UnprotectNullable(row.SmsFromNumberCipherText));

        // Incomplete config → null so the dispatcher falls back to platform env (fail-soft, never crash).
        return creds.IsComplete ? creds : null;
    }

    private static SmsProviderKey ParseProvider(string? value) =>
        Enum.TryParse<SmsProviderKey>(value, ignoreCase: true, out var key) ? key : SmsProviderKey.None;

    public async Task<TestSmsResponse> SendTestSmsAsync(int portfolioId, TestSmsRequest request, CancellationToken ct = default)
    {
        var to = Normalize(request.ToPhoneNumber);
        if (string.IsNullOrWhiteSpace(to))
            return new TestSmsResponse { Success = false, Message = "Enter a phone number to send the test to." };

        var provider = ParseProvider(request.SmsProvider);
        if (provider == SmsProviderKey.None)
            return new TestSmsResponse { Success = false, Message = "Choose an SMS provider first." };

        // Use the saved row to fill any blank credential slot, so the landlord can re-test a saved
        // provider without re-typing secrets. Slots already store ciphertext; decrypt for the blanks.
        var row = await GetOrCreateAsync(portfolioId, ct);
        var creds = new SmsCredentials(
            provider,
            FirstNonBlank(request.SmsCredentialA, UnprotectNullable(row.SmsCredentialACipherText), UnprotectNullable(row.SignalWireProjectIdCipherText)),
            FirstNonBlank(request.SmsCredentialB, UnprotectNullable(row.SmsCredentialBCipherText), UnprotectNullable(row.SignalWireTokenCipherText)),
            FirstNonBlank(request.SmsCredentialC, UnprotectNullable(row.SmsCredentialCCipherText), UnprotectNullable(row.SignalWireSpaceUrlCipherText)),
            FirstNonBlank(request.SmsFromNumber, UnprotectNullable(row.SmsFromNumberCipherText), UnprotectNullable(row.SignalWireFromNumberCipherText)));

        if (!creds.IsComplete)
            return new TestSmsResponse { Success = false, Message = $"Missing required credentials for {provider}." };

        if (!_smsProviders.TryGetValue(provider, out var impl))
            return new TestSmsResponse { Success = false, Message = $"No implementation registered for {provider}." };

        var normalizedTo = Sms.SmsDispatcher.NormalizeSmsNumber(to);
        try
        {
            await impl.SendAsync(creds, normalizedTo, "Rental Command test message — your SMS provider is configured correctly.", ct);
            return new TestSmsResponse { Success = true, Message = $"Test SMS sent to {normalizedTo} via {provider}." };
        }
        catch (Exception ex)
        {
            // Fail-soft: surface the provider's error to the UI, never throw out of the endpoint.
            return new TestSmsResponse { Success = false, Message = $"{provider} rejected the send: {ex.Message}" };
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(Normalize).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // -------------------------------------------------------------------------------------------
    // Per-portfolio settings row
    // -------------------------------------------------------------------------------------------

    private async Task<NotificationSettings> GetOrCreateAsync(int portfolioId, CancellationToken ct)
    {
        var row = await _db.NotificationSettings.SingleOrDefaultAsync(s => s.PortfolioId == portfolioId, ct);
        if (row is not null)
            return row;

        var now = _timeProvider.UtcNow();

        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "NotificationSettings" (
                    "PortfolioId",
                    "EnableRentCharges",
                    "EnableLateFees",
                    "EnableLeaseExpiryReminders",
                    "NotifyTenants",
                    "RentChargeLeadDays",
                    "LateFeeGraceDays",
                    "LeaseExpiryReminderDays",
                    "EnableDailyBriefingMessages",
                    "DailyBriefingSendHourLocal",
                    "DailyBriefingIncludeEmpty",
                    "CreatedAt",
                    "UpdatedAt")
                VALUES (
                    {portfolioId},
                    {false},
                    {false},
                    {true},
                    {false},
                    {5},
                    {5},
                    {60},
                    {false},
                    {8},
                    {false},
                    {now},
                    {now})
                ON CONFLICT ("PortfolioId") DO NOTHING;
                """,
                ct);

            return await _db.NotificationSettings.SingleAsync(s => s.PortfolioId == portfolioId, ct);
        }

        row = new NotificationSettings
        {
            PortfolioId = portfolioId,
            EnableLeaseExpiryReminders = true,
            RentChargeLeadDays = 5,
            LateFeeGraceDays = 5,
            LeaseExpiryReminderDays = 60,
            DailyBriefingSendHourLocal = 8,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.NotificationSettings.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return row;
        }
        catch (DbUpdateException)
        {
            // Get-or-create race: the Engine boots ~6 workers that each call this for the same
            // portfolio in parallel (separate scoped DbContexts), so several SELECT no row, then all
            // INSERT — the unique IX_NotificationSettings_PortfolioId rejects every loser with 23505.
            // Detach our failed insert and return the row the winner committed. If no row turns up the
            // failure wasn't this race, so rethrow rather than swallow a real error.
            _db.Entry(row).State = EntityState.Detached;
            var winner = await _db.NotificationSettings
                .SingleOrDefaultAsync(s => s.PortfolioId == portfolioId, ct);
            if (winner is not null)
                return winner;
            throw;
        }
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
            row.EnablePush = dto.EnablePush;
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
            EnablePush = row.EnablePush,
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
        AutoSendRentReminder = row.AutoSendRentReminder,
        AutoSendLateRent = row.AutoSendLateRent,
        LeaseEndAutoAction = row.LeaseEndAutoAction,
        SmsProvider = EffectiveProvider(row).ToString(),
        SmsFromNumber = EffectiveFromNumber(row),
        // Secrets: report only whether each slot is set, never the value. Legacy SignalWire rows map
        // their old columns onto the generic slots so the UI shows them as configured.
        SmsCredentialASet = HasSecret(row.SmsCredentialACipherText) || HasSecret(row.SignalWireProjectIdCipherText),
        SmsCredentialBSet = HasSecret(row.SmsCredentialBCipherText) || HasSecret(row.SignalWireTokenCipherText),
        SmsCredentialCSet = HasSecret(row.SmsCredentialCCipherText) || HasSecret(row.SignalWireSpaceUrlCipherText),
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
                EnablePush = pref.EnablePush,
            };
        }).ToList(),
    };

    private string? ProtectNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : _protector.Protect(value);

    /// <summary>
    /// Applies a write-only secret update: <c>null</c> input keeps the saved ciphertext (the UI sends
    /// null to mean "leave unchanged"); empty/blank clears it; a value re-encrypts it.
    /// </summary>
    private string? ApplySecret(string? current, string? incoming)
    {
        if (incoming is null)
            return current;
        return ProtectNullable(Normalize(incoming));
    }

    private static bool HasSecret(string? cipherText) => !string.IsNullOrWhiteSpace(cipherText);

    /// <summary>The provider as stored, with a legacy fall-through: a row that only has the old
    /// SignalWire columns reports as SignalWire so the UI shows it configured.</summary>
    private static SmsProviderKey EffectiveProvider(NotificationSettings row)
    {
        var provider = ParseProvider(row.SmsProvider);
        if (provider == SmsProviderKey.None && HasSecret(row.SignalWireProjectIdCipherText))
            return SmsProviderKey.SignalWire;
        return provider;
    }

    private string? EffectiveFromNumber(NotificationSettings row) =>
        UnprotectNullable(row.SmsFromNumberCipherText) ?? UnprotectNullable(row.SignalWireFromNumberCipherText);

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
