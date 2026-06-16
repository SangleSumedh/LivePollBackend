namespace live_poll_backend.Models.DTOs;

public class QuestionDto
{
    public string Text { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}
