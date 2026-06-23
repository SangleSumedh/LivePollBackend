using System;

namespace live_poll_backend.Models.Entities;

public class BiddingSkill
{
    public int Id { get; set; }
    public int BiddingQuestionId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public BiddingQuestion? BiddingQuestion { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Index { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
