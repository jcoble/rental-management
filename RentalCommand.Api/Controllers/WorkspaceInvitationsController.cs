using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Data;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>Anonymous first-password activation for a secure Team invitation.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/auth/workspace-invitations")]
[Produces("application/json")]
public sealed class WorkspaceInvitationsController : ControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IRequestWriteExecutor _writes;

    public WorkspaceInvitationsController(
        RentalCommandDbContext db,
        UserManager<ApplicationUser> users,
        IRequestWriteExecutor writes)
    {
        _db = db;
        _users = users;
        _writes = writes;
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate(
        [FromBody] ActivateWorkspaceInvitationRequest request,
        CancellationToken ct)
    {
        var token = request.Token.Trim();
        if (token.Length is 0 or > 500)
        {
            return InvalidInvitation();
        }

        var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
        var invitation = await _db.WorkspaceInvitations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(candidate => candidate.InvitedUser)
            .SingleOrDefaultAsync(candidate =>
                candidate.TokenHash == tokenHash &&
                candidate.AcceptedAtUtc == null &&
                candidate.RevokedAtUtc == null &&
                candidate.ExpiresAtUtc > DateTime.UtcNow &&
                candidate.InvitedUser!.PasswordHash == null,
                ct);
        if (invitation?.InvitedUser is null)
        {
            return InvalidInvitation();
        }

        var passwordErrors = new List<IdentityError>();
        foreach (var validator in _users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(
                _users, invitation.InvitedUser, request.Password);
            if (!validation.Succeeded)
            {
                passwordErrors.AddRange(validation.Errors);
            }
        }
        if (passwordErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    [nameof(request.Password)] = passwordErrors
                        .Select(error => error.Description)
                        .ToArray(),
                })
            {
                Title = "Choose a stronger password.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var command = new ActivateWorkspaceInvitationCommand(
            invitation.Id,
            invitation.InvitedUserId,
            tokenHash,
            _users.PasswordHasher.HashPassword(invitation.InvitedUser, request.Password),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        try
        {
            var result = (await _writes.ExecuteExactAsync(
                $"{invitation.InvitedUserId}:{tokenHash}",
                WorkspaceTeamWriteSupport.Write(_db, command), ct)).Value;
            return result.Outcome == ActivateWorkspaceInvitationOutcome.Activated
                ? Ok(new { activated = true })
                : InvalidInvitation();
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidInvitation();
        }
    }

    private IActionResult InvalidInvitation() => BadRequest(new
    {
        error = "This activation link is invalid, expired, or has already been used.",
    });
}
