using live_poll_backend.Models.Enums;

namespace live_poll_backend.Models.Entities;

public class Poll
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedByEmail { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = "Anonymous";
    public PollStatus Status { get; set; } = PollStatus.Draft;
    public int ActiveQuestionIndex { get; set; } = -1;
    public bool CurrentQuestionActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<VoteCount> VoteCounts { get; set; } = new List<VoteCount>();
    public ICollection<Vote> Votes { get; set; } = new List<Vote>();
}
