using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Services.Auth;

public sealed record CanonicalAccountBootstrapResult(
    ApplicationUser? User,
    int? PortfolioId,
    int? AccessContextId,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => User is not null;
}

public interface ICanonicalAccountBootstrapService
{
    Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
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
    private readonly IRlsExecutionContext _rls;
    private readonly TimeProvider _timeProvider;
    private readonly IInitialWorkspaceAuthorityProvisioner _workspaceAuthority;
    private readonly INotificationFoundationService _notificationFoundation;

    public CanonicalAccountBootstrapService(
        UserManager<ApplicationUser> users,
        RentalCommandDbContext db,
        IRlsExecutionContext rls,
        TimeProvider timeProvider,
        IInitialWorkspaceAuthorityProvisioner workspaceAuthority,
        INotificationFoundationService notificationFoundation)
    {
        _users = users;
        _db = db;
        _rls = rls;
        _timeProvider = timeProvider;
        _workspaceAuthority = workspaceAuthority;
        _notificationFoundation = notificationFoundation;
    }

    public async Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
        CancellationToken ct = default)
    {
        using var rlsBypass = _rls.BeginBypass(RlsBypassReason.RegistrationBootstrap);
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

        var portfolio = new Portfolio
        {
            Name = string.IsNullOrWhiteSpace(user.DisplayName)
                ? "My Portfolio"
                : $"{user.DisplayName}'s Portfolio",
            ManagementCompanyName = string.IsNullOrWhiteSpace(user.DisplayName)
                ? "My Company"
                : user.DisplayName,
            Status = PortfolioStatus.Active,
            Currency = "USD",
            IsSandbox = false,
            SandboxSeededAtUtc = null,
            Settings = Domain.PortfolioOnboarding.WriteChoice(null, Domain.OnboardingChoice.Pending),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Portfolios.Add(portfolio);
        await _db.SaveChangesAsync(ct);

        var context = await _workspaceAuthority.ProvisionAsync(user, portfolio, ct);

        await _notificationFoundation.SeedSuppliedTemplatesAsync(portfolio.Id, user.Id, ct);
        await transaction.CommitAsync(ct);
        return new CanonicalAccountBootstrapResult(user, portfolio.Id, context.Id, []);
    }
}
