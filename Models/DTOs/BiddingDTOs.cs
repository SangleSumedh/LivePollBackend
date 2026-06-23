using System.Collections.Generic;

namespace live_poll_backend.Models.DTOs;

public class BiddingAnalyticsSummary
{
    public string PollId { get; set; } = string.Empty;
    public List<QuestionAnalytics> Questions { get; set; } = new();
}

public class QuestionAnalytics
{
    public int QuestionId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public int QuestionIndex { get; set; }
    public List<SkillAnalyticsRow> Skills { get; set; } = new();
}

public class SkillAnalyticsRow
{
    public int SkillId { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int HRCoins { get; set; }
    public int AcademiaCoins { get; set; }
    public int DivergenceScore { get; set; }
}

public class PlaceBidRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Cohort { get; set; } = string.Empty;
    public int BiddingSkillId { get; set; }
    public int QuestionIndex { get; set; }
    public int CoinsSpent { get; set; }
}

public class StartQuestionRequest
{
    public int QuestionIndex { get; set; }
    public string Cohort { get; set; } = string.Empty;
}
