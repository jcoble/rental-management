using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortfolioService"/>
public class PortfolioService : IPortfolioService
{
    private const string EntityType = "Portfolio";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public PortfolioService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
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
                LeaseCount = _db.LeaseManagements.Count(management => management.PortfolioId == p.Id),
                HasNotificationEmail = _db.UserAlertPreferences.Any(pref =>
                    pref.PortfolioId == p.Id && pref.EnableEmail),
                HasTexting = _db.MessagingProviderSettings.Any(settings =>
                    settings.PortfolioId == p.Id &&
                    settings.SmsProvider != null && settings.SmsProvider != string.Empty),
                HasAutomations = _db.AutomationSettings.Any(settings =>
                    settings.PortfolioId == p.Id &&
                    (settings.EnableRentCharges || settings.EnableLateFees ||
                     settings.EnableRecurringMaintenance || settings.EnableMorningBriefing)),
            })
            .FirstOrDefaultAsync(ct);

        if (summary is null)
        {
            return null;
        }

        return new GettingStartedSignalsResponse
        {
            PortfolioId = summary.Id,
            PortfolioNamed = !string.IsNullOrWhiteSpace(summary.Name),
            OwnerCount = summary.OwnerCount,
            PropertyCount = summary.PropertyCount,
            UnitCount = summary.UnitCount,
            TenantCount = summary.TenantCount,
            LeaseCount = summary.LeaseCount,
            HasNotificationEmail = summary.HasNotificationEmail,
            HasTexting = summary.HasTexting,
            HasAutomations = summary.HasAutomations,
            IsSandbox = summary.IsSandbox,
        };
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
        entity.UpdatedAt = _timeProvider.UtcNow();

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

        entity.DeletedAt = _timeProvider.UtcNow();
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
