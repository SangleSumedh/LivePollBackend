using System.Collections.Generic;
using System.Threading.Tasks;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public enum PollKind
{
    Normal,
    Bidding,
    NotFound
}

public interface IVoteStateTracker
{
    void RecordVote(string pollId, VoteRequest vote);
    Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId);
    Dictionary<string, int> GetVoteCounts(string pollId, int questionIndex);
    Task<ActivePollState?> GetActivePollStateAsync(string pollId);
    Task<PollKind> GetPollKindAsync(string pollId);
    Task FlushToDatabaseAsync(string pollId);
    Task FlushAllPendingAsync();
    void ClearPoll(string pollId);
    void InvalidateActivePollState(string pollId);
}

public class ActivePollState
{
    public string PollId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int ActiveQuestionIndex { get; set; }
    public live_poll_backend.Models.Enums.QuestionType QuestionType { get; set; }
    public string Status { get; set; } = string.Empty;
}

