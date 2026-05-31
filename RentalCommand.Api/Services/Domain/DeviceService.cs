using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDeviceService"/>
public class DeviceService : IDeviceService
{
    private readonly RentalCommandDbContext _db;

    public DeviceService(RentalCommandDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task RegisterAsync(int portfolioId, int userId, string token, string platform, CancellationToken ct = default)
    {
        var existing = await _db.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token, ct);

        var now = DateTime.UtcNow;

        if (existing is not null)
        {
            existing.UserId = userId;
            existing.PortfolioId = portfolioId;
            existing.Platform = platform;
            existing.LastSeenAt = now;
        }
        else
        {
            _db.DeviceTokens.Add(new DeviceToken
            {
                PortfolioId = portfolioId,
                UserId      = userId,
                Token       = token,
                Platform    = platform,
                CreatedAt   = now,
                LastSeenAt  = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> UnregisterAsync(int userId, string token, CancellationToken ct = default)
    {
        var row = await _db.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.UserId == userId, ct);

        if (row is null)
            return false;

        _db.DeviceTokens.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
