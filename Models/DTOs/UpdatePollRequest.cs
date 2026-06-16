namespace live_poll_backend.Models.DTOs;

public class UpdatePollRequest
{
    public string Title { get; set; } = string.Empty;
    public List<QuestionDto> Questions { get; set; } = new();
}
