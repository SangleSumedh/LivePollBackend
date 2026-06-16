namespace live_poll_backend.Models.DTOs;

public class VoteRequest
{
    public int QuestionIndex { get; set; }
    public int OptionIndex { get; set; }
    public string SessionId { get; set; } = string.Empty;
}
