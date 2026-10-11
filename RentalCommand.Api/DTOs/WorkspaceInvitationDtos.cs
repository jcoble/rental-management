using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public sealed class ActivateWorkspaceInvitationRequest
{
    [Required, MaxLength(500)] public string Token { get; set; } = string.Empty;
    [Required, MinLength(8), MaxLength(200)] public string Password { get; set; } = string.Empty;
}
