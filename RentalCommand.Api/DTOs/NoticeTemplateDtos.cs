using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public class NoticeTemplateResponse
{
    public string NoticeType { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool HasTemplate { get; set; }
    public IReadOnlyList<string> AvailableFields { get; set; } = [];
    public DateTime? UpdatedAt { get; set; }
}

public class UpsertNoticeTemplateRequest
{
    [Required, MinLength(1)]
    public string Subject { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Body { get; set; } = string.Empty;
}
