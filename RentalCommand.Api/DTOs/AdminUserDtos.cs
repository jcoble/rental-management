using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Read projection of a team member for admin list/create/update responses.</summary>
public class TeamMemberDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int? OwnerId { get; set; }
    public int? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response wrapper for the create endpoint. Includes the member DTO plus, when a password
/// was auto-generated, the temporary password so the admin can share it with the new user.
/// </summary>
public class CreateTeamMemberResponse
{
    public TeamMemberDto Member { get; set; } = new();

    /// <summary>Non-null only when the caller did not supply a <c>temporaryPassword</c> in the request.</summary>
    public string? GeneratedPassword { get; set; }
}

public class CreateTeamMemberRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [Required]
    public UserRole Role { get; set; } = UserRole.Manager;

    /// <summary>Required when creating a tenant portal user; becomes the JWT tenantId claim.</summary>
    public int? TenantId { get; set; }

    /// <summary>
    /// Optional. If omitted, a strong temporary password is generated and returned once in the response.
    /// </summary>
    [MaxLength(200)]
    public string? TemporaryPassword { get; set; }
}

public class ChangeRoleRequest
{
    [Required]
    public UserRole Role { get; set; }
}

public class SetActiveRequest
{
    [Required]
    public bool IsActive { get; set; }
}
