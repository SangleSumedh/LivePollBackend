using System.Collections.Generic;
using System.Threading.Tasks;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public interface IBiddingService
{
    Task<List<Skill>> GetSkillsAsync();
    Task<Skill> AddSkillAsync(string name, string category);
    Task DeleteSkillAsync(int id);
    
    // BiddingPoll CRUD
    Task<List<BiddingPollResponse>> GetBiddingPollsAsync(string userId);
    Task<BiddingPollResponse> GetBiddingPollByIdAsync(string pollId);
    Task<BiddingPollResponse> CreateBiddingPollAsync(CreateBiddingPollRequest request, string userId, string userEmail, string userName);
    Task<BiddingPollResponse> UpdateBiddingPollAsync(string pollId, UpdateBiddingPollRequest request);
    Task DeleteBiddingPollAsync(string pollId);
    Task RestartBiddingPollAsync(string pollId);

    Task StartBiddingAsync(string pollId);
    Task StopBiddingAsync(string pollId);
    Task LockInBidsAsync(string pollId, string sessionId, List<int> skillIds);
    Task<BiddingAnalyticsSummary> GetAnalyticsAsync(string pollId);
}
