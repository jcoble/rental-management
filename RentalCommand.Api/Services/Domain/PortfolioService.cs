using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortfolioService"/>
public class PortfolioService : IPortfolioService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;

    public PortfolioService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _atomic = atomic;
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
                    (settings.EnableLateFees || settings.EnableRecurringMaintenance ||
                     settings.EnableMorningBriefing)),
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

    public async Task<PortfolioResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdatePortfolioRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        if (id != scope.PortfolioId)
        {
            return null;
        }
        var command = AtomicWorkspaceCoreMutation.Command(
            scope, AtomicWorkspaceCoreMutationOperation.UpdatePortfolio, operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicWorkspaceCoreMutation.Identity(command), command, AtomicWorkspaceCoreMutation.Codec, ct);
        return outcome.Value.Found && outcome.Value.ResponseJson is not null
            ? JsonSerializer.Deserialize<PortfolioResponse>(outcome.Value.ResponseJson)
            : null;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        if (id != scope.PortfolioId)
        {
            return false;
        }
        var command = AtomicWorkspaceCoreMutation.Command(
            scope, AtomicWorkspaceCoreMutationOperation.DeletePortfolio, operationKey, new { });
        var outcome = await _atomic.ExecuteAsync(
            AtomicWorkspaceCoreMutation.Identity(command), command, AtomicWorkspaceCoreMutation.Codec, ct);
        return outcome.Value.Found;
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
