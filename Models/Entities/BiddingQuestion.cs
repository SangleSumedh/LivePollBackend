using System;
using System.Collections.Generic;

namespace live_poll_backend.Models.Entities;

public class BiddingQuestion
{
    public int Id { get; set; }
    public string BiddingPollId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public BiddingPoll? BiddingPoll { get; set; }

    public string Text { get; set; } = string.Empty;
    public int Index { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BiddingSkill> Skills { get; set; } = new List<BiddingSkill>();
}
