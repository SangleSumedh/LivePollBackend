namespace live_poll_backend.Models.Entities;

public class Vote
{
    public int Id { get; set; }
    public string PollId { get; set; } = string.Empty;
    public int QuestionIndex { get; set; }
    public int? OptionIndex { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? SubmittedText { get; set; }
    public DateTime VotedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Poll Poll { get; set; } = null!;
}
