using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class SelectWorkspaceExperienceRequest
{
    [EnumDataType(typeof(WorkspaceExperience))]
    public WorkspaceExperience Experience { get; set; }
}
