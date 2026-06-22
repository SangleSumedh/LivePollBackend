using System;

namespace live_poll_backend.Models.Entities;

public class SkillBid
{
    public long Id { get; set; }
    public string BiddingPollId { get; set; } = string.Empty;
    public BiddingPoll? BiddingPoll { get; set; }
    
    public int SkillId { get; set; }
    public Skill? Skill { get; set; }
    
    public string SessionId { get; set; } = string.Empty;
    public string Cohort { get; set; } = string.Empty; // "HR" or "ACADEMIA"
    public int CoinsSpent { get; set; }
    public bool IsCommitted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
