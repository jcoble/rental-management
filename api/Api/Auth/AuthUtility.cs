using System.Security.Cryptography;
using RentalCommand.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Api.Auth;

public static class AuthUtility
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private static readonly TimeSpan SessionTouchInterval = TimeSpan.FromMinutes(5);

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public static async Task<UserAccount?> GetCurrentUser(HttpContext context, RentalCommandDbContext db, bool touchSession = true)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[7..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var tokenHash = HashToken(token);
        var now = DateTime.UtcNow;

        var session = await db.AuthSessions
            .Include(s => s.UserAccount)
                .ThenInclude(u => u!.Owner)
            .Include(s => s.UserAccount)
                .ThenInclude(u => u!.Tenant)
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

        if (session is null) return null;
        if (session.ExpiresAt <= now) return null;
        if (session.UserAccount is null || !session.UserAccount.IsActive) return null;

        if (touchSession && now - session.LastSeenAt >= SessionTouchInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync();
        }

        return session.UserAccount;
    }

    public static bool IsAnyRole(UserAccount user, params UserRole[] roles)
    {
        return roles.Contains(user.Role);
    }
}
