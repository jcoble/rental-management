using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public class AiChatRequest
{
    [Required]
    [MaxLength(8000)]
    public string Message { get; set; } = string.Empty;
}

public class AiChatResponse
{
    public string Reply { get; set; } = string.Empty;
}
