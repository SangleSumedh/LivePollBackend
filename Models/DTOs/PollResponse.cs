namespace live_poll_backend.Models.DTOs;

public class PollResponse
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedByEmail { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ActiveQuestionIndex { get; set; }
    public bool CurrentQuestionActive { get; set; }
    public List<QuestionResponse> Questions { get; set; } = new();
    public Dictionary<string, int> VoteCounts { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
