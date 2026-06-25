using System.Collections.Generic;
using System.Threading.Tasks;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public interface IVoteStateTracker
{
    void RecordVote(string pollId, VoteRequest vote);
    Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId);
    Dictionary<string, int> GetVoteCounts(string pollId, int questionIndex);
    Task FlushToDatabaseAsync(string pollId);
    Task FlushAllPendingAsync();
    void ClearPoll(string pollId);
}
