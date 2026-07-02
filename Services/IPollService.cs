using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public interface IPollService
{
    Task<List<PollResponse>> GetPollsAsync(string userId);
    Task<PollResponse> GetPollByIdAsync(string pollId);
    Task<PollResponse> CreatePollAsync(CreatePollRequest request, string userId, string userEmail, string userName);
    Task<PollResponse> UpdatePollAsync(string pollId, UpdatePollRequest request);
    Task DeletePollAsync(string pollId);
    Task RestartPollAsync(string pollId);
    Task StartVotingAsync(string pollId, int questionIndex);
    Task StopVotingAsync(string pollId);
    Task NextQuestionAsync(string pollId);
    Task PrevQuestionAsync(string pollId);
    Task EndPollAsync(string pollId);
    Task<byte[]> ExportPollDataAsync(string pollId);
}
