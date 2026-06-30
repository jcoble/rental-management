using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortfolioService"/>
public class PortfolioService : IPortfolioService
{
    private const string EntityType = "Portfolio";

    private readonly RentalCommandDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDataUpdateService _dataUpdate;
    private readonly ISelfOwnerProvisioner _selfOwnerProvisioner;

    public PortfolioService(
        RentalCommandDbContext db,
        UserManager<ApplicationUser> userManager,
        IDataUpdateService dataUpdate,
        ISelfOwnerProvisioner selfOwnerProvisioner)
    {
        _db = db;
        _userManager = userManager;
        _dataUpdate = dataUpdate;
        _selfOwnerProvisioner = selfOwnerProvisioner;
    }

    public async Task<IReadOnlyList<PortfolioResponse>> ListForUserAsync(int portfolioId, CancellationToken ct = default)
    {
        if (portfolioId <= 0)
        {
            return Array.Empty<PortfolioResponse>();
        }

        var items = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .ToListAsync(ct);

        return items.Select(PortfolioResponse.FromEntity).ToList();
    }

    public async Task<PortfolioResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        // A user can only view their own portfolio.
        if (id != portfolioId)
        {
            return null;
        }

        var entity = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        return entity == null ? null : PortfolioResponse.FromEntity(entity);
    }

    public async Task<GettingStartedSignalsResponse?> GetGettingStartedSignalsAsync(
        int portfolioId,
        CancellationToken ct = default)
    {
        if (portfolioId <= 0)
        {
            return null;
        }

        var summary = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Settings,
                p.IsSandbox,
                OwnerCount = _db.OwnerEntities.Count(o => o.PortfolioId == p.Id),
                PropertyCount = _db.Properties.Count(property => property.PortfolioId == p.Id),
                UnitCount = _db.Units.Count(unit => unit.Property != null && unit.Property.PortfolioId == p.Id),
                TenantCount = _db.Tenants.Count(tenant => tenant.PortfolioId == p.Id),
                LeaseCount = _db.Leases.Count(lease => lease.PortfolioId == p.Id),
            })
            .FirstOrDefaultAsync(ct);

        if (summary is null)
        {
            return null;
        }

        var notificationSettings = await _db.NotificationSettings
            .AsNoTracking()
            .Where(settings => settings.PortfolioId == portfolioId)
            .Select(settings => new
            {
                settings.EnableRentCharges,
                settings.EnableLateFees,
                HasDailyBriefingEmail = settings.DailyBriefingEmailRecipientsCipherText != null
                    && settings.DailyBriefingEmailRecipientsCipherText != string.Empty,
                HasSmsCredentialA = (settings.SmsCredentialACipherText != null
                    && settings.SmsCredentialACipherText != string.Empty)
                    || (settings.SignalWireProjectIdCipherText != null
                    && settings.SignalWireProjectIdCipherText != string.Empty),
                HasSmsCredentialB = (settings.SmsCredentialBCipherText != null
                    && settings.SmsCredentialBCipherText != string.Empty)
                    || (settings.SignalWireTokenCipherText != null
                    && settings.SignalWireTokenCipherText != string.Empty),
            })
            .FirstOrDefaultAsync(ct);

        var hasEmailPreference = await _db.NotificationPreferences
            .AsNoTracking()
            .AnyAsync(pref => pref.PortfolioId == portfolioId && pref.EnableEmail, ct);

        return new GettingStartedSignalsResponse
        {
            PortfolioId = summary.Id,
            PortfolioNamed = !string.IsNullOrWhiteSpace(summary.Name),
            OwnerCount = summary.OwnerCount,
            PropertyCount = summary.PropertyCount,
            UnitCount = summary.UnitCount,
            TenantCount = summary.TenantCount,
            LeaseCount = summary.LeaseCount,
            HasNotificationEmail = ReadNotificationEmail(summary.Settings) != null
                || hasEmailPreference
                || notificationSettings?.HasDailyBriefingEmail == true,
            HasTexting = notificationSettings?.HasSmsCredentialA == true
                || notificationSettings?.HasSmsCredentialB == true,
            HasAutomations = notificationSettings?.EnableRentCharges == true
                || notificationSettings?.EnableLateFees == true,
            IsSandbox = summary.IsSandbox,
        };
    }

    public async Task<PortfolioResponse> CreateAsync(int userId, CreatePortfolioRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = new Portfolio
        {
            Name = request.Name,
            Description = request.Description,
            ManagementCompanyName = request.ManagementCompanyName,
            TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "America/New_York" : request.TimeZone,
            Status = PortfolioStatus.Active,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency,
            Settings = request.Settings,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Portfolios.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Scope the creating user to the new portfolio so their subsequent JWT carries this portfolioId.
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user != null)
        {
            user.PortfolioId = entity.Id;
            await _userManager.UpdateAsync(user);

            // The landlord IS the first owner — auto-create a primary self-owner so the new portfolio is
            // never owner-less and onboarding skips the manual "add an owner" step. Idempotent.
            await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, entity.Id, ct);
        }

        var response = PortfolioResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(entity.Id, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PortfolioResponse?> UpdateAsync(int portfolioId, int id, UpdatePortfolioRequest request, CancellationToken ct = default)
    {
        if (id != portfolioId)
        {
            return null;
        }

        var entity = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.Name != null) entity.Name = request.Name;
        if (request.Description != null) entity.Description = request.Description;
        if (request.ManagementCompanyName != null) entity.ManagementCompanyName = request.ManagementCompanyName;
        if (!string.IsNullOrWhiteSpace(request.TimeZone)) entity.TimeZone = request.TimeZone;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (!string.IsNullOrWhiteSpace(request.Currency)) entity.Currency = request.Currency;
        if (request.Settings != null) entity.Settings = request.Settings;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = PortfolioResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        if (id != portfolioId)
        {
            return false;
        }

        var entity = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private static string? ReadNotificationEmail(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("notifications", out var notifications) &&
                notifications.ValueKind == JsonValueKind.Object &&
                notifications.TryGetProperty("email", out var email) &&
                email.ValueKind == JsonValueKind.String)
            {
                return string.IsNullOrWhiteSpace(email.GetString()) ? null : email.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
