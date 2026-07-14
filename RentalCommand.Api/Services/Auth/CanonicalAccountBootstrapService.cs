using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

public sealed record CanonicalAccountBootstrapResult(
    ApplicationUser? User,
    int? PortfolioId,
    int? AccessContextId,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => User is not null;
}

public sealed record CanonicalWorkspaceBootstrapOptions(
    string PortfolioName,
    string ManagementCompanyName);

public interface ICanonicalAccountBootstrapService
{
    Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
        CanonicalWorkspaceBootstrapOptions? workspace = null,
        CancellationToken ct = default);
}

/// <summary>
/// The only fresh-account bootstrap. Password and Google registration both create the same complete
/// workspace authority graph in one explicit transaction; no Identity role or UserAccount is written.
/// </summary>
public sealed class CanonicalAccountBootstrapService : ICanonicalAccountBootstrapService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public CanonicalAccountBootstrapService(
        UserManager<ApplicationUser> users,
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _users = users;
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
        CanonicalWorkspaceBootstrapOptions? workspace = null,
        CancellationToken ct = default)
    {
        var normalizedEmail = _users.NormalizeEmail(email);
        if (await _db.Users.AsNoTracking()
                .AnyAsync(user => user.NormalizedEmail == normalizedEmail, ct))
        {
            return new CanonicalAccountBootstrapResult(null, null, null, ["Email is already registered"]);
        }

        var now = _timeProvider.UtcNow();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            DisplayName = displayName.Trim(),
            CreatedAt = now,
        };

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var createResult = password is null
            ? await _users.CreateAsync(user)
            : await _users.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return new CanonicalAccountBootstrapResult(
                null,
                null,
                null,
                createResult.Errors.Select(error => error.Description).ToArray());
        }

        var defaultPortfolioName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? "My Portfolio"
            : $"{user.DisplayName}'s Portfolio";
        var defaultManagementCompanyName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? "My Company"
            : user.DisplayName;
        var portfolioName = string.IsNullOrWhiteSpace(workspace?.PortfolioName)
            ? defaultPortfolioName
            : workspace.PortfolioName.Trim();
        var managementCompanyName = string.IsNullOrWhiteSpace(workspace?.ManagementCompanyName)
            ? defaultManagementCompanyName
            : workspace.ManagementCompanyName.Trim();
        var ownerName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? (user.Email?.Split('@')[0] ?? "Me (primary owner)")
            : user.DisplayName;
        var bootstrap = await _db.Database.SqlQuery<InitialWorkspaceBootstrapRow>($"""
                SELECT * FROM rc_bootstrap_initial_workspace(
                    {user.Id}, {portfolioName}, {managementCompanyName}, {ownerName},
                    {user.Email ?? string.Empty}, {now})
                """)
            .SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return new CanonicalAccountBootstrapResult(
            user, bootstrap.PortfolioId, bootstrap.AccessContextId, []);
    }

    private sealed class InitialWorkspaceBootstrapRow
    {
        public int PortfolioId { get; set; }
        public int AccessContextId { get; set; }
    }
}
