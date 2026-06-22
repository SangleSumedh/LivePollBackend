using System.Collections.Generic;

namespace live_poll_backend.Models.DTOs;

public class BiddingAnalyticsSummary
{
    public string PollId { get; set; } = string.Empty;
    public List<SkillAnalyticsRow> Rows { get; set; } = new();
}

public class SkillAnalyticsRow
{
    public int SkillId { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int HRVotes { get; set; }
    public int AcademiaVotes { get; set; }
    public int DivergenceScore { get; set; }
}

public class LockInRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<int> SkillIds { get; set; } = new();
}

public class AddSkillRequest
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
