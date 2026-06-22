using System;
using System.Collections.Generic;

namespace live_poll_backend.Models.Entities;

public class BiddingPoll
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedByEmail { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = "Anonymous";
    public bool IsBiddingActive { get; set; }
    public bool BiddingClosed { get; set; }
    public int SkillCost { get; set; } = 20;
    public string Theme { get; set; } = "synergy_sphere";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Skill> Skills { get; set; } = new List<Skill>();
}
