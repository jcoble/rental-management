using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Data;
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

        ct.ThrowIfCancellationRequested();
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "Workspace invitation activation is temporarily unavailable while token admission is moved to a database-validated command.",
        });
    }

    private IActionResult InvalidInvitation() => BadRequest(new
    {
        error = "This activation link is invalid, expired, or has already been used.",
    });

    public sealed class LockedInvitationRow
    {
        public long Id { get; set; }
        public int PortfolioId { get; set; }
        public DateTime WallClockUtc { get; set; }
    }
}
