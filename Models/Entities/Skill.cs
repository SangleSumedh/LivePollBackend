using System;

namespace live_poll_backend.Models.Entities;

public class Skill
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public ICollection<BiddingPoll> BiddingPolls { get; set; } = new List<BiddingPoll>();
}
