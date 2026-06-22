using System;
using System.Collections.Generic;
using live_poll_backend.Models.Entities;

namespace live_poll_backend.Models.DTOs;

public class CreateBiddingPollRequest
{
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "synergy_sphere";
    public int SkillCost { get; set; } = 20;
    public List<int> SkillIds { get; set; } = new();
}

public class UpdateBiddingPollRequest
{
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "synergy_sphere";
    public int SkillCost { get; set; } = 20;
    public List<int> SkillIds { get; set; } = new();
}

public class BiddingPollResponse
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedByEmail { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = string.Empty;
    public bool IsBiddingActive { get; set; }
    public bool BiddingClosed { get; set; }
    public int SkillCost { get; set; }
    public string Theme { get; set; } = string.Empty;
    public List<Skill> Skills { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
