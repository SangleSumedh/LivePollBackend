namespace live_poll_backend.Models.DTOs;

public class QuestionResponse
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<OptionResponse> Options { get; set; } = new();
}

public class OptionResponse
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
}
