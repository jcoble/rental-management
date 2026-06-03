namespace RentalCommand.Api.DTOs;

public class LeaseQuestionRequest
{
    public string Question { get; set; } = string.Empty;
}

public class LeaseQuestionResponse
{
    public string Answer { get; set; } = string.Empty;
    public bool LlmEnhanced { get; set; }
    public IReadOnlyList<string> Sources { get; set; } = [];
}
