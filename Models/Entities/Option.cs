namespace live_poll_backend.Models.Entities;

public class Option
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;

    // Navigation properties
    public Question Question { get; set; } = null!;
}
