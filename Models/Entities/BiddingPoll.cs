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
    public string Theme { get; set; } = "synergy_sphere";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int ActiveQuestionIndex { get; set; } = -1;
    public string CurrentCohort { get; set; } = string.Empty;

    // Navigation properties
    public ICollection<BiddingQuestion> Questions { get; set; } = new List<BiddingQuestion>();
}
