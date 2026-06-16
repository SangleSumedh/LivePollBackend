using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public interface IVoteService
{
    Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId);
    Task CastVoteAsync(string pollId, VoteRequest request);
}
