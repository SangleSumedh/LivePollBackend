namespace live_poll_backend.Models.Entities;

public class Question
{
    public int Id { get; set; }
    public string PollId { get; set; } = string.Empty;
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;

    // Navigation properties
    public Poll Poll { get; set; } = null!;
    public ICollection<Option> Options { get; set; } = new List<Option>();
}
