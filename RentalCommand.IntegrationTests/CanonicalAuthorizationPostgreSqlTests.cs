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
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        foreach (var statement in FoundationBaselinePostgreSql.CreateStatements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }
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

    [SkippableFact]
    public async Task InvitationScope_IsTransactionLocalAndRejectsCrossWorkspaceAccess()
    {
        SkipIfNoDocker();
        var first = await SeedInvitationAsync("scope-a");
        var second = await SeedInvitationAsync("scope-b");
        await using var connection = await OpenAsApiRoleAsync();
        await using var db = NewContext(connection);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await WorkspaceInvitationActivationRlsScope.ApplyAsync(
                db.Database, first.PortfolioId, CancellationToken.None);

            var visible = await db.WorkspaceMemberships
                .IgnoreQueryFilters()
                .OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.PortfolioId })
                .ToListAsync();

            visible.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new
                {
                    Id = first.WorkspaceMembershipId,
                    first.PortfolioId,
                });
            visible.Should().NotContain(row => row.PortfolioId == second.PortfolioId);
            (await ReadSettingAsync(connection, "app.current_portfolio_id"))
                .Should().Be(first.PortfolioId.ToString());
            (await ReadSettingAsync(connection, "app.is_admin")).Should().Be("false");

            await transaction.CommitAsync();
        }

        (await ReadSettingAsync(connection, "app.current_portfolio_id")).Should().BeNullOrEmpty(
            "set_config(..., true) must not leak the anonymous activation scope after commit");
        (await ReadSettingAsync(connection, "app.is_admin")).Should().BeNullOrEmpty();

        await using var unscopedTransaction = await db.Database.BeginTransactionAsync();
        var afterCommit = await db.WorkspaceMemberships
            .IgnoreQueryFilters()
            .Select(row => row.Id)
            .ToListAsync();
        afterCommit.Should().BeEmpty(
            "a reused connection must return to fail-closed RLS state after activation");
        await unscopedTransaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task InvitationActivation_BlocksOnTheTokenRowBeforeMutatingAuthority()
    {
        SkipIfNoDocker();
        var seed = await SeedInvitationAsync("locked");
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var command = lockConnection.CreateCommand())
        {
            command.Transaction = lockTransaction;
            command.CommandText = """
                SELECT "Id"
                FROM "WorkspaceInvitations"
                WHERE "TokenHash" = @token_hash
                FOR UPDATE
                """;
            command.Parameters.AddWithValue("token_hash", seed.TokenHash);
            (await command.ExecuteScalarAsync()).Should().Be(seed.InvitationId);
        }

        var activation = ActivateAsync(seed.Token);
        var observedBlockedLock = await WaitForBlockedInvitationLockAsync();
        var activationWasPending = !activation.IsCompleted;

        await lockTransaction.CommitAsync();
        (await activation).Should().BeOfType<OkObjectResult>();
        activationWasPending.Should().BeTrue();
        observedBlockedLock.Should().BeTrue(
            "PostgreSQL must report the activation command waiting on the held token-row lock");
    }

    [SkippableFact]
    public async Task InvitationActivation_ConcurrentDuplicateAndReplayAllowExactlyOneCommit()
    {
        SkipIfNoDocker();
        var seed = await SeedInvitationAsync("concurrent");

        var results = await Task.WhenAll(
            ActivateAsync(seed.Token),
            ActivateAsync(seed.Token));

        results.Count(result => result is OkObjectResult).Should().Be(1);
        results.Count(result => result is BadRequestObjectResult).Should().Be(1);
        (await ActivateAsync(seed.Token)).Should().BeOfType<BadRequestObjectResult>(
            "a committed activation token is terminal and replay-safe");

        await using var db = NewOwnerContext();
        var durable = await db.WorkspaceInvitations
            .AsNoTracking()
            .Where(row => row.Id == seed.InvitationId)
            .Select(row => new
            {
                row.AcceptedAtUtc,
                PasswordHash = row.InvitedUser!.PasswordHash,
            })
            .SingleAsync();
        durable.AcceptedAtUtc.Should().NotBeNull();
        durable.PasswordHash.Should().NotBeNullOrEmpty();
        (await db.WorkspaceInvitations.CountAsync(row => row.TokenHash == seed.TokenHash))
            .Should().Be(1);
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

    private async Task<IActionResult> ActivateAsync(string token)
    {
        var connection = await OpenAsApiRoleAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(connection);
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlConnection>()));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddEntityFrameworkStores<RentalCommandDbContext>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var controller = new WorkspaceInvitationsController(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
        return await controller.Activate(
            new ActivateWorkspaceInvitationRequest
            {
                Token = token,
                Password = ValidPassword,
            },
            CancellationToken.None);
    }

    private async Task<ApplicationUser> SeedUserAsync(string email)
    {
        await using var db = NewOwnerContext();
        var user = User(email, "Platform Operator");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<InvitationSeed> SeedInvitationAsync(string tag)
    {
        var now = DateTime.UtcNow;
        var token = $"activation-{tag}-{Guid.NewGuid():N}";
        var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
        await using var db = NewOwnerContext();
        var portfolio = new Portfolio
        {
            Name = $"Authorization {tag}",
            ManagementCompanyName = $"Authorization {tag}",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var invited = User($"invited-{tag}-{Guid.NewGuid():N}@example.test", "Invited User");
        var inviter = User($"inviter-{tag}-{Guid.NewGuid():N}@example.test", "Inviting Admin");
        db.AddRange(portfolio, invited, inviter);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = invited.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceAccessContexts.Add(accessContext);
        await db.SaveChangesAsync();
        var membership = new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();
        var invitation = new WorkspaceInvitation
        {
            PortfolioId = portfolio.Id,
            WorkspaceMembershipId = membership.Id,
            InvitedUserId = invited.Id,
            InvitedByUserId = inviter.Id,
            TokenHash = tokenHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
        };
        db.WorkspaceInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return new InvitationSeed(
            token,
            tokenHash,
            invitation.Id,
            portfolio.Id,
            membership.Id);
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

    private static RentalCommandDbContext NewContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connection)
            .Options);

    private async Task<NpgsqlConnection> OpenAsApiRoleAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET ROLE {ApiRole};";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task<bool> WaitForBlockedInvitationLockAsync()
    {
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                  SELECT 1
                  FROM pg_stat_activity
                  WHERE datname = current_database()
                    AND wait_event_type = 'Lock'
                    AND query LIKE '%FROM "WorkspaceInvitations"%FOR UPDATE%')
                """;
            if (await command.ExecuteScalarAsync() is true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    private static async Task<string?> ReadSettingAsync(
        NpgsqlConnection connection,
        string setting)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting(@setting, true)";
        command.Parameters.AddWithValue("setting", setting);
        return await command.ExecuteScalarAsync() as string;
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(
            _dockerAvailable,
            "Docker is not available; canonical authorization PostgreSQL verification skipped.");

    private sealed record InvitationSeed(
        string Token,
        string TokenHash,
        long InvitationId,
        int PortfolioId,
        int WorkspaceMembershipId);
}
