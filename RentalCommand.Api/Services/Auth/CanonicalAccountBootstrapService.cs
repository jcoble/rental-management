using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Data;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

public sealed record CanonicalAccountBootstrapResult(
    ApplicationUser? User,
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

    public CanonicalAccountBootstrapService(
        UserManager<ApplicationUser> users,
        RentalCommandDbContext db,
        IRlsExecutionContext rls,
        TimeProvider timeProvider)
    {
        _users = users;
        _db = db;
        _rls = rls;
        _timeProvider = timeProvider;
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
            return new CanonicalAccountBootstrapResult(null, ["Email is already registered"]);
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

        var owner = new OwnerEntity
        {
            PortfolioId = portfolio.Id,
            OwnerEntityType = OwnerEntityType.Person,
            Name = string.IsNullOrWhiteSpace(user.DisplayName) ? email.Split('@')[0] : user.DisplayName,
            Email = email,
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var administratorAssignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.AddRange(owner, administratorAssignment);
        await _db.SaveChangesAsync(ct);

        // Convenience identity links only. No canonical Owner-user access relationship exists yet.
        user.PortfolioId = portfolio.Id;
        user.OwnerEntityId = owner.Id;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CanonicalAccountBootstrapResult(user, []);
    }
}
