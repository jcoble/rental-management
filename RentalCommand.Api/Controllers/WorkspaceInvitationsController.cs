using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

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

    public WorkspaceInvitationsController(
        RentalCommandDbContext db,
        UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
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
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        var locked = await _db.Database.SqlQuery<LockedInvitationRow>($"""
                SELECT "Id", clock_timestamp() AS "WallClockUtc"
                FROM "WorkspaceInvitations"
                WHERE "TokenHash" = {tokenHash}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);
        if (locked is null)
        {
            return InvalidInvitation();
        }

        var invitation = await _db.WorkspaceInvitations
            .IgnoreQueryFilters()
            .Include(row => row.InvitedUser)
            .Include(row => row.WorkspaceMembership)
                .ThenInclude(membership => membership!.AccessContext)
            .SingleAsync(row => row.Id == locked.Id, ct);
        if (invitation.AcceptedAtUtc is not null ||
            invitation.RevokedAtUtc is not null ||
            invitation.ExpiresAtUtc <= locked.WallClockUtc ||
            invitation.WorkspaceMembership?.Status != WorkspaceMembershipStatus.Active ||
            invitation.WorkspaceMembership.AccessContext?.Status != WorkspaceAccessContextStatus.Active ||
            invitation.InvitedUser is null ||
            !string.IsNullOrEmpty(invitation.InvitedUser.PasswordHash))
        {
            return InvalidInvitation();
        }

        var passwordResult = await _users.AddPasswordAsync(
            invitation.InvitedUser, request.Password);
        if (!passwordResult.Succeeded)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    [nameof(request.Password)] = passwordResult.Errors
                        .Select(error => error.Description)
                        .ToArray(),
                })
            {
                Title = "Choose a stronger password.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        invitation.InvitedUser.EmailConfirmed = true;
        invitation.AcceptedAtUtc = locked.WallClockUtc;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Ok(new { activated = true });
    }

    private IActionResult InvalidInvitation() => BadRequest(new
    {
        error = "This activation link is invalid, expired, or has already been used.",
    });

    public sealed class LockedInvitationRow
    {
        public long Id { get; set; }
        public DateTime WallClockUtc { get; set; }
    }
}
