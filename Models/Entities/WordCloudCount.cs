namespace live_poll_backend.Models.Entities;

public class WordCloudCount
{
    public int Id { get; set; }
    public string PollId { get; set; } = string.Empty;
    public int QuestionIndex { get; set; }
    public string Word { get; set; } = string.Empty;
    public int Count { get; set; }

    // Navigation property
    public Poll Poll { get; set; } = null!;
}
