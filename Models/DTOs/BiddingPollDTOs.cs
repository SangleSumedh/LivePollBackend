using System;
using System.Collections.Generic;

namespace live_poll_backend.Models.DTOs;

public class CreateBiddingSkillRequest
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Index { get; set; }
}

public class CreateBiddingQuestionRequest
{
    public string Text { get; set; } = string.Empty;
    public int Index { get; set; }
    public List<CreateBiddingSkillRequest> Skills { get; set; } = new();
}

public class CreateBiddingPollRequest
{
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "synergy_sphere";
    public List<CreateBiddingQuestionRequest> Questions { get; set; } = new();
}

public class UpdateBiddingSkillRequest
{
    public int? Id { get; set; } // Null for new skills, present for existing
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Index { get; set; }
}

public class UpdateBiddingQuestionRequest
{
    public int? Id { get; set; } // Null for new questions, present for existing
    public string Text { get; set; } = string.Empty;
    public int Index { get; set; }
    public List<UpdateBiddingSkillRequest> Skills { get; set; } = new();
}

public class UpdateBiddingPollRequest
{
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "synergy_sphere";
    public List<UpdateBiddingQuestionRequest> Questions { get; set; } = new();
}

public class BiddingSkillResponse
{
    public int Id { get; set; }
    public int BiddingQuestionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Index { get; set; }
}

public class BiddingQuestionResponse
{
    public int Id { get; set; }
    public string BiddingPollId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int Index { get; set; }
    public List<BiddingSkillResponse> Skills { get; set; } = new();
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
    public string Theme { get; set; } = string.Empty;
    public int ActiveQuestionIndex { get; set; }
    public string CurrentCohort { get; set; } = string.Empty;
    public List<BiddingQuestionResponse> Questions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
