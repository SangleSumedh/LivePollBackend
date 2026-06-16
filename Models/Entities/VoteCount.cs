namespace live_poll_backend.Models.Entities;

public class VoteCount
{
    public int Id { get; set; }
    public string PollId { get; set; } = string.Empty;
    public int QuestionIndex { get; set; }
    public int OptionIndex { get; set; }
    public int Count { get; set; }

    // Navigation properties
    public Poll Poll { get; set; } = null!;
}
