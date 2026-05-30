using RentalCommand.Api.Auth;
using RentalCommand.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Api;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (LoginRequest req, RentalCommandDbContext db) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.UserAccounts
                .FirstOrDefaultAsync(u => u.PortfolioId == req.PortfolioId && u.Email == email);

            if (user is null || !user.IsActive || !AuthUtility.VerifyPassword(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            var rawToken = AuthUtility.GenerateToken();
            var tokenHash = AuthUtility.HashToken(rawToken);
            var now = DateTime.UtcNow;
            var expiresAt = now.AddDays(14);

            db.AuthSessions.Add(new AuthSession
            {
                UserAccountId = user.Id,
                TokenHash = tokenHash,
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = expiresAt
            });

            user.LastLoginAt = now;
            user.UpdatedAt = now;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                Token = rawToken,
                ExpiresAt = expiresAt,
                User = new
                {
                    user.Id,
                    user.PortfolioId,
                    user.DisplayName,
                    user.Email,
                    Role = user.Role.ToString(),
                    user.OwnerId,
                    user.TenantId
                }
            });
        });

        group.MapGet("/me", async (HttpContext context, RentalCommandDbContext db) =>
        {
            var user = await AuthUtility.GetCurrentUser(context, db);
            if (user is null) return Results.Unauthorized();

            return Results.Ok(new
            {
                user.Id,
                user.PortfolioId,
                user.DisplayName,
                user.Email,
                Role = user.Role.ToString(),
                user.OwnerId,
                user.TenantId,
                user.LastLoginAt
            });
        });

        group.MapPost("/logout", async (HttpContext context, RentalCommandDbContext db) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.NoContent();

            var tokenHash = AuthUtility.HashToken(header[7..].Trim());
            var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.TokenHash == tokenHash);
            if (session is not null)
            {
                db.AuthSessions.Remove(session);
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });

        group.MapGet("/users", async (HttpContext context, int portfolioId, RentalCommandDbContext db) =>
        {
            var current = await AuthUtility.GetCurrentUser(context, db);
            if (current is null) return Results.Unauthorized();
            if (current.PortfolioId != portfolioId) return Results.Forbid();
            if (!AuthUtility.IsAnyRole(current, UserRole.Admin, UserRole.Manager)) return Results.Forbid();

            var users = await db.UserAccounts
                .Where(u => u.PortfolioId == portfolioId)
                .OrderBy(u => u.Role)
                .ThenBy(u => u.DisplayName)
                .ToListAsync();

            return Results.Ok(users.Select(u => new
            {
                u.Id,
                u.PortfolioId,
                u.DisplayName,
                u.Email,
                Role = u.Role.ToString(),
                u.OwnerId,
                u.TenantId,
                u.IsActive,
                u.LastLoginAt,
                u.CreatedAt,
                u.UpdatedAt
            }));
        });

        group.MapPost("/users", async (HttpContext context, CreateUserRequest req, RentalCommandDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var current = await AuthUtility.GetCurrentUser(context, db);

            if (current is null)
            {
                var existingInPortfolio = await db.UserAccounts.AnyAsync(u => u.PortfolioId == req.PortfolioId);
                if (existingInPortfolio) return Results.Unauthorized();
            }
            else
            {
                if (current.PortfolioId != req.PortfolioId) return Results.Forbid();
                if (!AuthUtility.IsAnyRole(current, UserRole.Admin, UserRole.Manager)) return Results.Forbid();
                if (current.Role == UserRole.Manager && req.Role == UserRole.Admin) return Results.Forbid();
            }

            var email = req.Email.Trim().ToLowerInvariant();
            var exists = await db.UserAccounts.AnyAsync(u => u.PortfolioId == req.PortfolioId && u.Email == email);
            if (exists) return Results.BadRequest(new { error = "Email already in use for this portfolio" });

            int? ownerId = req.OwnerId.GetValueOrDefault() > 0 ? req.OwnerId : null;
            int? tenantId = req.TenantId.GetValueOrDefault() > 0 ? req.TenantId : null;
            if (ownerId.HasValue && tenantId.HasValue)
                return Results.BadRequest(new { error = "User account cannot be linked to both owner and tenant." });

            if (req.Role == UserRole.Owner)
            {
                if (!ownerId.HasValue)
                    return Results.BadRequest(new { error = "Owner users must be linked to an owner record." });
                tenantId = null;
            }
            else if (req.Role == UserRole.Tenant)
            {
                if (!tenantId.HasValue)
                    return Results.BadRequest(new { error = "Tenant users must be linked to a tenant record." });
                ownerId = null;
            }
            else
            {
                ownerId = null;
                tenantId = null;
            }

            if (ownerId.HasValue)
            {
                var ownerExists = await db.Owners.AnyAsync(o => o.Id == ownerId.Value && o.PortfolioId == req.PortfolioId);
                if (!ownerExists)
                    return Results.BadRequest(new { error = "Owner link is invalid for this portfolio." });
            }

            if (tenantId.HasValue)
            {
                var tenantExists = await db.Tenants.AnyAsync(t => t.Id == tenantId.Value && t.PortfolioId == req.PortfolioId);
                if (!tenantExists)
                    return Results.BadRequest(new { error = "Tenant link is invalid for this portfolio." });
            }

            var user = new UserAccount
            {
                PortfolioId = req.PortfolioId,
                OwnerId = ownerId,
                TenantId = tenantId,
                Email = email,
                DisplayName = req.DisplayName,
                PasswordHash = AuthUtility.HashPassword(req.Password),
                Role = req.Role,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.UserAccounts.Add(user);
            await db.SaveChangesAsync();

            return Results.Created($"/api/auth/users/{user.Id}", new
            {
                user.Id,
                user.PortfolioId,
                user.DisplayName,
                user.Email,
                Role = user.Role.ToString(),
                user.OwnerId,
                user.TenantId,
                user.IsActive,
                user.CreatedAt
            });
        });

        group.MapPatch("/users/{id:int}", async (HttpContext context, int id, UpdateUserRequest req, RentalCommandDbContext db) =>
        {
            var current = await AuthUtility.GetCurrentUser(context, db);
            if (current is null) return Results.Unauthorized();
            if (!AuthUtility.IsAnyRole(current, UserRole.Admin, UserRole.Manager)) return Results.Forbid();

            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (user.PortfolioId != current.PortfolioId) return Results.Forbid();

            if (req.DisplayName is not null) user.DisplayName = req.DisplayName;
            if (req.IsActive.HasValue) user.IsActive = req.IsActive.Value;
            if (req.Role.HasValue)
            {
                if (current.Role == UserRole.Manager && req.Role == UserRole.Admin)
                    return Results.Forbid();
                user.Role = req.Role.Value;
            }
            if (!string.IsNullOrWhiteSpace(req.Password))
                user.PasswordHash = AuthUtility.HashPassword(req.Password);

            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.Email,
                Role = user.Role.ToString(),
                user.IsActive,
                user.OwnerId,
                user.TenantId,
                user.UpdatedAt
            });
        });

        return app;
    }
}

public record LoginRequest(int PortfolioId, string Email, string Password);
public record CreateUserRequest(
    int PortfolioId,
    string Email,
    string DisplayName,
    string Password,
    UserRole Role,
    int? OwnerId = null,
    int? TenantId = null);

public record UpdateUserRequest(
    string? DisplayName = null,
    string? Password = null,
    UserRole? Role = null,
    bool? IsActive = null);
