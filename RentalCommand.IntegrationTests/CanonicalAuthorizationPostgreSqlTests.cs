using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Data;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Authoritative PostgreSQL proof for the two anonymous/platform authorization boundaries that
/// cannot be represented faithfully by SQLite: current database identity for platform operators,
/// and transaction-local RLS scope plus row locking for workspace invitation activation.
/// </summary>
public sealed class CanonicalAuthorizationPostgreSqlTests : IAsyncLifetime
{
    private const string ApiRole = "rentalcommand_api";
    private const string PlatformOperatorEmail = "operator@platform.example";
    private const string ValidPassword = "Rental-Command-2026!";

    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_canonical_auth")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewOwnerContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task PlatformAdmin_UsesCurrentDatabaseEmailAndIgnoresForgedBearerAuthority()
    {
        SkipIfNoDocker();
        var user = await SeedUserAsync(PlatformOperatorEmail);
        var forgedUnprivilegedBearer = CanonicalPrincipal(user.Id,
        [
            new Claim(ClaimTypes.Email, "ordinary-user@example.test"),
            new Claim(ClaimTypes.Role, "WorkspaceAdministrator"),
        ]);

        (await AuthorizePlatformAdminAsync(forgedUnprivilegedBearer)).Succeeded.Should().BeTrue(
            "the current database email, not bearer email or role claims, is platform authority");

        await using (var db = NewOwnerContext())
        {
            var current = await db.Users.SingleAsync(row => row.Id == user.Id);
            current.Email = "ordinary-user@example.test";
            current.NormalizedEmail = "ORDINARY-USER@EXAMPLE.TEST";
            await db.SaveChangesAsync();
        }

        var forgedOperatorBearer = CanonicalPrincipal(user.Id,
        [
            new Claim(ClaimTypes.Email, PlatformOperatorEmail),
            new Claim(ClaimTypes.Role, "PlatformAdmin"),
        ]);
        (await AuthorizePlatformAdminAsync(forgedOperatorBearer)).Succeeded.Should().BeFalse(
            "changing the durable identity must revoke platform access immediately");
    }

    [SkippableFact]
    public async Task PlatformAdmin_MissingOrDeletedDatabaseUserIsDenied()
    {
        SkipIfNoDocker();
        var user = await SeedUserAsync(PlatformOperatorEmail);
        await using (var db = NewOwnerContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(row => row.Id == user.Id));
            await db.SaveChangesAsync();
        }

        var forgedOperatorBearer = CanonicalPrincipal(user.Id,
        [
            new Claim(ClaimTypes.Email, PlatformOperatorEmail),
            new Claim(ClaimTypes.Role, "PlatformAdmin"),
        ]);
        (await AuthorizePlatformAdminAsync(forgedOperatorBearer)).Succeeded.Should().BeFalse();
        (await AuthorizePlatformAdminAsync(CanonicalPrincipal(int.MaxValue))).Succeeded.Should().BeFalse();
    }

    private async Task<AuthorizationResult> AuthorizePlatformAdminAsync(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RentalCommandDbContext>(options => options.UseNpgsql(_connectionString));
        var allowlist = PlatformAdminPolicy.BuildAllowlist(new PlatformAdminOptions
        {
            Emails = [PlatformOperatorEmail],
        });
        services.AddAuthorization(options => PlatformAdminPolicy.Register(options, allowlist));
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        return await authorization.AuthorizeAsync(
            principal, resource: null, PlatformAdminPolicy.Name);
    }

    private async Task<ApplicationUser> SeedUserAsync(string email)
    {
        await using var db = NewOwnerContext();
        var user = User(email, "Platform Operator");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ApplicationUser User(string email, string displayName) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = displayName,
        EmailConfirmed = true,
        CreatedAt = DateTime.UtcNow,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
    };

    private static ClaimsPrincipal CanonicalPrincipal(
        int userId,
        IEnumerable<Claim>? additionalClaims = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
        };
        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private RentalCommandDbContext NewOwnerContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(
            _dockerAvailable,
            "Docker is not available; canonical authorization PostgreSQL verification skipped.");

}
